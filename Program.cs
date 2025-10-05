using System.Net;
using System.Net.Mime;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using System.Collections.Concurrent; // for ConcurrentDictionary
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Diagnostics;
using System.Text.Json.Serialization;
using System.Linq;
using FileUp.Controllers;
using Microsoft.AspNetCore.StaticFiles; // for FileExtensionContentTypeProvider

AppDomain.CurrentDomain.ProcessExit += (s, e) =>
{
    Logger.Log("[BOOT] FileUp shutting down!");
};

/* ── constants ─────────────────────────────────────────── */
string prodPath = "/var/lib/fileup/uploads";
string devPath = "C:\\Users\\ssasa\\Desktop\\fileup\\FileUp\\uploads";
string BASE_UPLOADS;
int port;
Logger.Log($"[BOOT] FileUp servce started at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
if (Directory.Exists(prodPath))
{
    BASE_UPLOADS = prodPath;
    port = 4000;
    Logger.Log($"[INFO] Using production uploads directory: {BASE_UPLOADS}");
}
else if (Directory.Exists(devPath))
{
    BASE_UPLOADS = devPath;
    port = 5057;
    Logger.Log($"[INFO] Using development uploads directory: {BASE_UPLOADS}");
}
else
{
    throw new Exception("No valid upload directory found!");
}



// global stores & constants
/* ── in-memory shortlink store () ─────────────────────────── */
var FileStore = new ConcurrentDictionary<string, FileRecord>();
var expiryQueue = new SortedDictionary<DateTime, List<string>>(); // in-memory priority queue
var expiryLock = new object(); // to synchronize access to expiryQueue

var allowedExt = new HashSet<string> {
    ".doc", ".docx", ".gif", ".jpg", ".jpeg", ".png", ".svg",
    ".mpg", ".mpeg", ".mp3", ".odt", ".odp", ".ods", ".pdf",
    ".ppt", ".pptx", ".tif", ".tiff", ".txt", ".xls", ".xlsx", ".wav"
};
const long MAX_STORAGE = 5L * 1024 * 1024 * 1024;
const long MAX_FILE_GUEST = 3L * 1024 * 1024;



// ── Startup scan ───────────────────────────
// Populate FileStore and expiryQueue with existing files at startup
var startupExpiryQueue = FileUp.Controllers.StartupScan.ScanAndBuildQueue(BASE_UPLOADS, FileStore);

// Merge startupExpiryQueue into our main expiryQueue (thread-safe via lock)
lock (expiryLock)
{
    foreach (var kv in startupExpiryQueue)
    {
        if (!expiryQueue.ContainsKey(kv.Key))
            expiryQueue[kv.Key] = new List<string>();

        expiryQueue[kv.Key].AddRange(kv.Value);
    }
}

// Start background cleanup with thread-safe access
FileUp.Controllers.BackgroundCleanup.Start(FileStore, expiryQueue, expiryLock);

Logger.Log($"[INFO] Startup scan completed. {FileStore.Count} files registered for cleanup.");


/* ── boilerplate ───────────────────────────────────────── */
var builder = WebApplication.CreateBuilder(args);
// Add secrets.json (ignore if missing)
builder.Configuration.AddJsonFile("secrets.json", optional: true, reloadOnChange: true);

// builder.WebHost.UseUrls("http://localhost:{port}");
builder.WebHost.UseUrls($"http://localhost:{port}"); // listen on all interfaces
Logger.Log($"[INFO] Binding to port {port}");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("uploadPolicy", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "x",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(20),
                QueueLimit = 0
            }));
});

// CORS Setup
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .WithOrigins(
                "https://img.sinasnp.com",
                "http://147.93.127.162",
                "http://localhost:3000"
            )
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials(); // if needed, else remove this line
    });
});


builder.Services.AddAuthentication("Basic")
    .AddScheme<AuthenticationSchemeOptions, BasicAuthHandler>("Basic", _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();



// atch framework-level errors (like 404, 415, etc.)
app.Use(async (ctx, next) =>
{
    await next();

    if (!ctx.Response.HasStarted &&
        ctx.Response.StatusCode >= 400 &&
        ctx.Response.ContentType != "application/json")
    {
        ctx.Response.ContentType = "application/json";
        var problem = new { error = $"HTTP {ctx.Response.StatusCode}" };
        await ctx.Response.WriteAsJsonAsync(problem);
    }
});

app.UseRouting(); // ← MUST be before CORS
// CORS setup
app.UseCors("AllowFrontend");

app.UseAuthentication();
app.UseAuthorization();

/* ── in-memory shortlink store ─────────────────────────── */
// var ShortLinkStore = new Dictionary<string, ShortLinkRecord>();

/* ── middleware ────────────────────────────────────────── */
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(BASE_UPLOADS),
    RequestPath = "/files",
    ServeUnknownFileTypes = false
});
app.UseSwagger();
app.UseSwaggerUI();
app.UseRateLimiter();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";

        try
        {
            var feature = context.Features.Get<IExceptionHandlerPathFeature>();
            var ex = feature?.Error;

            var payload = new { error = ex?.Message ?? "Unexpected server error" };
            await context.Response.WriteAsJsonAsync(payload);
        }
        catch
        {
            await context.Response.WriteAsync("{\"error\":\"Fatal error while handling exception\"}");
        }
    });
});

/* ── upload endpoint (UploadService.cs) ───────────────────── */
app.MapUploadEndpoints(
    BASE_UPLOADS,
    FileStore,
    expiryQueue,
    expiryLock,
    allowedExt,
    MAX_STORAGE,
    MAX_FILE_GUEST
);


/* ── upload endpoint (APIService.cs) ───────────────────── */
app.MapAPIUploadEndpoints(BASE_UPLOADS, FileStore, allowedExt, MAX_STORAGE, MAX_FILE_GUEST);



/* ── imghost endpoints ────────────────────────────────────── */
// app.MapImghostEndpoints(BASE_UPLOADS, FileStore );


/* ── admin panel ──────────────────────────────────────────── */
app.MapGet("/admin", () =>
{
    var files = Directory.EnumerateFiles(BASE_UPLOADS, "*", SearchOption.AllDirectories)
                         .Select(p => new FileInfo(p))
                         .OrderByDescending(f => f.CreationTimeUtc);

    var html = "<h1>Uploaded files</h1><ul>";
    html += string.Join("", files.Select(f =>
        $"<li>{WebUtility.HtmlEncode(f.Name)} —- {(f.Length / 1024.0):F1} KB</li>"));
    html += "</ul>";
    return Results.Content(html, MediaTypeNames.Text.Html);
}).RequireAuthorization();

app.MapGet("/files/public/{ext}/{fileName}", (string ext, string fileName) =>
{
    if (!FileStore.TryGetValue(fileName, out var record))
        return Results.Json(new { error = "error 404 - file not found" }, statusCode: 404);

    // Check expiration
    if (record.ExpireAt.HasValue && DateTime.UtcNow > record.ExpireAt.Value)
        return Results.Json(
            new { error = "error 410 - the file's expiration time has been reached" },
            statusCode: 410
        );

    // Increment and check MaxViews
    if (record.MaxViews.HasValue)
    {
        var views = ViewCounter.IncrementView(fileName);
        if (views > record.MaxViews.Value)
            return Results.Json(
                new { error = $"error 403 - max views of {record.MaxViews.Value} reached" },
                statusCode: 403
            );
    }
    else
    {
        ViewCounter.IncrementView(fileName);
    }

    // check if file can be opened in browser
    // othervise download
    var provider = new FileExtensionContentTypeProvider();
    if (!provider.TryGetContentType(record.Path, out var contentType))
        contentType = "application/octet-stream"; // fallback

    // ── Serve file inline (not as attachment) ─────────────
    return Results.File(record.Path, contentType);
});


app.Run();

/* ── support types ─────────────────────────────────────── */
public record ShortLinkRecord
{
    public string OriginalUrl { get; set; } = default!; //or =string.Empty;
    public string FilePath { get; set; } = default!;  // Absolute path on Server, for cleanup
    public DateTime ExpireAt { get; set; }
}

// from Services/ImageHostService.cs
record ShortLinkCreateRequest
{
    public string Url { get; set; } = default!;
    public int Expire { get; set; }
}

// from Services/UploadService.cs
// For tracking uploaded files and their expiration
public record FileRecord
{
    public string Path { get; set; } = default!;
    public DateTime? ExpireAt { get; set; } // null = forever
    public int? MaxViews { get; set; }      // null = unlimited
}


