using System.Net;
using System.Net.Mime;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Diagnostics;
using System.Text.Json.Serialization;
using System.Linq;

/* ── constants ─────────────────────────────────────────── */
const string BASE_UPLOADS = "/var/lib/fileup/uploads"; 
// const string BASE_UPLOADS = "C:\\Users\\ssasa\\Desktop\\fileup\\FileUp\\uploads";
// global stores & constants
var FileStore = new Dictionary<string, FileRecord>();
var allowedExt = new HashSet<string> {
    ".doc", ".docx", ".gif", ".jpg", ".jpeg", ".png", ".svg",
    ".mpg", ".mpeg", ".mp3", ".odt", ".odp", ".ods", ".pdf",
    ".ppt", ".pptx", ".tif", ".tiff", ".txt", ".xls", ".xlsx", ".wav"
};
const long MAX_STORAGE = 5L * 1024 * 1024 * 1024;
const long MAX_FILE_GUEST = 3L * 1024 * 1024;





/* ── boilerplate ───────────────────────────────────────── */
var builder = WebApplication.CreateBuilder(args);
// Add secrets.json (ignore if missing)
builder.Configuration.AddJsonFile("secrets.json", optional: true, reloadOnChange: true);

builder.WebHost.UseUrls("http://localhost:4000");

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
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
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
//app.UseCors("AllowFrontend");
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
        var feature = context.Features.Get<IExceptionHandlerPathFeature>();
        var ex = feature?.Error;

        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";

        var payload = new { error = ex?.Message ?? "Unexpected server error" };
        await context.Response.WriteAsJsonAsync(payload);
    });
});

/* ── upload endpoint ───────────────────────────────────── */
app.MapUploadEndpoints(BASE_UPLOADS, FileStore, allowedExt, MAX_STORAGE, MAX_FILE_GUEST);


/* ── in-memory shortlink store ─────────────────────────── */
var ShortLinkStore = new Dictionary<string, ShortLinkRecord>();

/* ── upload endpoints ──────────────────────────────────── */
app.MapUploadEndpoints(BASE_UPLOADS, FileStore, allowedExt, MAX_STORAGE, MAX_FILE_GUEST);

/* ── imghost endpoints ─────────────────────────────────── */
app.MapImghostEndpoints(BASE_UPLOADS, ShortLinkStore);


/* ── admin panel ────────────────────────────────────────── */
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


/* ── redirect shortlink ────────────────────────────────── */
app.MapGet("/s/{id}", (string id) =>
{
    if (!ShortLinkStore.TryGetValue(id, out var record))
        return Results.NotFound();

    if (record.ExpireAt < DateTime.UtcNow)
    {
        Console.WriteLine($"🗑 Attempting to delete the expired file: {record.FilePath}");
        ShortLinkStore.Remove(id);
        try
        {
            if (File.Exists(record.FilePath))
            {
                File.Delete(record.FilePath);
                Console.WriteLine($"🗑 Deleted expired file: {record.FilePath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to delete {record.FilePath}: {ex.Message}");
        }

        return Results.StatusCode(410); // link gone
    }

    return Results.Redirect(record.OriginalUrl);
});

/* ── background cleanup loop ─────────────────────────────── */
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            var now = DateTime.UtcNow;
            var expired = FileStore.Where(kv => kv.Value.ExpireAt != null && kv.Value.ExpireAt < now).ToList();
            foreach (var kv in expired)
            {
                try
                {
                    if (File.Exists(kv.Value.Path))
                    {
                        File.Delete(kv.Value.Path);
                        Console.WriteLine($"Deleted expired file: {kv.Value.Path}");
                    }
                    FileStore.Remove(kv.Key);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"! Failed to delete {kv.Value.Path}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"! Cleanup loop error: {ex.Message}");
        }

        await Task.Delay(TimeSpan.FromSeconds(10)); // Deletion runs every 10 min
    }
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
}


