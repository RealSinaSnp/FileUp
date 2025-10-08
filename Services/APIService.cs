using System.Net;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

// to let other servers use the API to upload files
// max epire is 24h

public static class APIService
{
    public static void MapAPIUploadEndpoints(
        this IEndpointRouteBuilder app,
        string baseUploads,
        ConcurrentDictionary<string, FileRecord> fileStore,
        SortedDictionary<DateTime, List<string>> expiryQueue,
        object expiryLock,
        HashSet<string> allowedExt,
        long maxStorage,
        long maxFileSize)
    {
        app.MapPost("/api/public/upload", async (HttpContext ctx) =>
        {
            var request = ctx.Request;
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "x";

            if (!request.HasFormContentType)
                return Results.Json(new { error = "Invalid form" }, statusCode: (int)HttpStatusCode.BadRequest);

            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");

            if (file == null || file.Length == 0)
                return Results.Json(new { error = "No file selected" }, statusCode: (int)HttpStatusCode.ExpectationFailed);

            // ── MIME sanity check ────────────────────────────────────────
            if (file.ContentType.Contains("exe") ||
                file.ContentType.Contains("php") ||
                file.ContentType.Contains("x-msdownload"))
            {
                Logger.Log($"[SECURITY] Suspicious MIME type from {ip}: '{file.FileName}' ({file.ContentType})");
                return Results.Json(new { error = "Suspicious file type detected" }, statusCode: (int)HttpStatusCode.Forbidden);
            }

            // ── rate limit guests (avoid flooding) ─────────────────────
            if (!UploadCounter.CheckAndIncrement(ip))
                return Results.Json(new { error = "Rate limit exceeded. Try again later." }, statusCode: (int)HttpStatusCode.TooManyRequests);

            // ── file size check ─────────────────────────────────────────
            if (file.Length > maxFileSize)
                return Results.Json(new { error = $"File size exceeds limit of {maxFileSize / (1024 * 1024)} MB" },
                    statusCode: (int)HttpStatusCode.Forbidden);

            // ── extension check ─────────────────────────────────────────
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExt.Contains(ext))
                return Results.Json(new { error = "File type not allowed" }, statusCode: (int)HttpStatusCode.Forbidden);

            // ── storage space check ─────────────────────────────────────
            long current = Directory.EnumerateFiles(baseUploads, "*", SearchOption.AllDirectories)
                                    .Sum(p => new FileInfo(p).Length);
            if (current + file.Length > maxStorage)
            {
                Logger.Log($"[SECURITY] Storage full. Upload attempt blocked. IP={ip}");
                return Results.Json(new { error = "Storage limit exceeded. (Server storage is full)" }, statusCode: (int)HttpStatusCode.ExpectationFailed);
            }

            // Save in uploads/public/<ext>/ directory
            var dir = Path.Combine(baseUploads, "public", ext.TrimStart('.'));
            Directory.CreateDirectory(dir);

            // Clean filename
            var originalName = Path.GetFileNameWithoutExtension(file.FileName);
            originalName = string.Join("_", originalName.Split(Path.GetInvalidFileNameChars()));
            originalName = Regex.Replace(originalName, @"[^a-zA-Z0-9]", "_");

            // Expiry logic
            int expireMinutes = 2;
            if (form.TryGetValue("expireMinutes", out var minutesValue) && int.TryParse(minutesValue, out var parsedMinutes))
                expireMinutes = Math.Clamp(parsedMinutes, 1, 1440); // 1 min – 24 hours
            var expireAt = DateTime.UtcNow.AddMinutes(expireMinutes);

            // View limit logic
            int? maxViews = null;
            if (form.TryGetValue("maxViews", out var maxViewsVal) && int.TryParse(maxViewsVal, out var parsedMaxViews))
                maxViews = Math.Clamp(parsedMaxViews, 1, 200);

            // Save file
            var safeName = $"{originalName}_{expireAt:yyyyMMddHHmmss}{ext}";
            var fullPath = Path.Combine(dir, safeName);

            // Save safely
            await using var fs = new FileStream(fullPath, FileMode.Create);
            await file.CopyToAsync(fs);



            // safe with ConcurrentDictionary
            fileStore[safeName] = new FileRecord
            {
                Path = fullPath,
                ExpireAt = expireAt,
                MaxViews = maxViews
            };

            // Register in expiryQueue (for BackgroundCleanup)
            lock (expiryLock)
            {
                if (!expiryQueue.TryGetValue(expireAt, out var list))
                {
                    list = new List<string>();
                    expiryQueue[expireAt] = list;
                }
                list.Add(safeName);
            }

            var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/files/public/{ext.TrimStart('.')}/{safeName}";

            Logger.Log($"[APIService] File uploaded via /api/public/upload: {safeName}, expires at {expireAt:u}");

            return Results.Ok(new
            {
                fileName = safeName,
                size = file.Length,
                url,
                expireAt,
                maxViews
            });
        })
        .DisableAntiforgery()
        .RequireRateLimiting("uploadPolicy");
    }
}
