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
const long MAX_STORAGE = 5L * 1024 * 1024 * 1024;
const long MAX_FILE_GUEST = 3L * 1024 * 1024;
var allowedExt = new HashSet<string> {
    ".doc", ".docx", ".gif", ".jpg", ".jpeg", ".png", ".svg",
    ".mpg", ".mpeg", ".mp3", ".odt", ".odp", ".ods", ".pdf",
    ".ppt", ".pptx", ".tif", ".tiff", ".txt", ".xls", ".xlsx", ".wav"
};

/* ── boilerplate ───────────────────────────────────────── */
var builder = WebApplication.CreateBuilder(args);
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

app.UseRouting(); // ← MUST be before CORS
// CORS setup
//app.UseCors("AllowFrontend");
app.UseCors("AllowFrontend");

app.UseAuthentication();
app.UseAuthorization();

/* ── in-memory shortlink store ─────────────────────────── */
var ShortLinkStore = new Dictionary<string, ShortLinkRecord>();

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
        var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
        var exception = exceptionHandlerPathFeature?.Error;
        Console.WriteLine($"🔥 Unhandled error: {exception?.Message}");
        await context.Response.WriteAsync("Something went wrong.");
    });
});

/* ── upload endpoint ───────────────────────────────────── */
app.MapPost("/api/files/upload", async (IFormFile file, HttpContext ctx) =>
{
    var isAdmin = ctx.User.Identity?.IsAuthenticated == true;
    if (file == null || file.Length == 0) return Results.BadRequest("No file selected");

    if (!isAdmin && file.Length > MAX_FILE_GUEST)
        return Results.BadRequest("Guests: max 3 MB");
    if (file.Length > 24 * 1024 * 1024)
        return Results.BadRequest("Max 24 MB");

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!allowedExt.Contains(ext)) return Results.BadRequest("File type not allowed");

    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "x";
    if (!isAdmin && !UploadCounter.CheckAndIncrement(ip))
        return Results.StatusCode((int)HttpStatusCode.TooManyRequests);

    long current = Directory.EnumerateFiles(BASE_UPLOADS, "*", SearchOption.AllDirectories)
                            .Sum(p => new FileInfo(p).Length);
    if (current + file.Length > MAX_STORAGE)
        return Results.StatusCode((int)HttpStatusCode.InsufficientStorage);

    var dir = Path.Combine(BASE_UPLOADS, ext.TrimStart('.'));
    Directory.CreateDirectory(dir);

    var fname = $"{Guid.NewGuid():N}{ext}";
    await using var fs = new FileStream(Path.Combine(dir, fname), FileMode.Create);
    await file.CopyToAsync(fs);

    var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/files/{ext.TrimStart('.')}/{fname}";
    return Results.Ok(new { fileName = fname, size = file.Length, url });
})
.DisableAntiforgery()
.RequireRateLimiting("uploadPolicy");

app.MapPost("/api/files/imghost", async (HttpContext ctx) =>
{
    try
    {
        var data = await ctx.Request.ReadFromJsonAsync<ShortLinkCreateRequest>();
        if (data == null || string.IsNullOrWhiteSpace(data.Url))
            return Results.BadRequest("Missing URL");

        // Validate URL is absolute and well-formed
        if (!Uri.IsWellFormedUriString(data.Url, UriKind.Absolute))
            return Results.BadRequest("Invalid URL format");

        var ext = Path.GetExtension(data.Url).ToLowerInvariant();
        var valid = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        if (!valid.Contains(ext)) 
            return Results.BadRequest("Invalid file extension");

        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(data.Url);

        var subdir = ext.TrimStart('.');
        var dir = Path.Combine(BASE_UPLOADS, subdir);
        Directory.CreateDirectory(dir);

        var filename = $"{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(dir, filename);
        await File.WriteAllBytesAsync(fullPath, bytes);

        var publicUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/files/{subdir}/{filename}";

        var id = Guid.NewGuid().ToString("N")[..6];
        var expireAt = DateTime.UtcNow.AddHours(data.Expire <= 0 ? 1 : data.Expire);
        ShortLinkStore[id] = new ShortLinkRecord { OriginalUrl = publicUrl, ExpireAt = expireAt };

        var shortUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/s/{id}";
        return Results.Ok(new { link = shortUrl });
    }
    catch (Exception ex)
    {
        Console.WriteLine("🔥 EXCEPTION in /api/files/imghost:");
        Console.WriteLine(ex);
        return Results.Problem("Server exploded. Check console.");
    }
});



/* ── redirect shortlink ────────────────────────────────── */
app.MapGet("/s/{id}", (string id) =>
{
    if (!ShortLinkStore.TryGetValue(id, out var record))
        return Results.NotFound();

    if (record.ExpireAt < DateTime.UtcNow)
    {
        ShortLinkStore.Remove(id);
        return Results.StatusCode(410); // gone
    }

    return Results.Redirect(record.OriginalUrl);
});

/* ── admin panel ───────────────────────────────────────── */
app.MapGet("/admin", () =>
{
    var files = Directory.EnumerateFiles(BASE_UPLOADS, "*", SearchOption.AllDirectories)
                         .Select(p => new FileInfo(p))
                         .OrderByDescending(f => f.CreationTimeUtc);

    var html = "<h1>Uploaded files</h1><ul>";
    html += string.Join("", files.Select(f =>
        $"<li>{WebUtility.HtmlEncode(f.Name)} — {(f.Length / 1024.0):F1} KB</li>"));
    html += "</ul>";
    return Results.Content(html, MediaTypeNames.Text.Html);
}).RequireAuthorization();

app.Run();

/* ── support types ─────────────────────────────────────── */
record ShortLinkRecord
{
    public string OriginalUrl { get; set; } = default!;
    public DateTime ExpireAt { get; set; }
}

record ShortLinkCreateRequest
{
    public string Url { get; set; } = default!;
    public int Expire { get; set; }
}

