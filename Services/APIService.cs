using System.Net;
using System.Collections.Concurrent;

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
            if (!request.HasFormContentType)
                return Results.Json(new { error = "Invalid form" }, statusCode: (int)HttpStatusCode.BadRequest);

            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");

            if (file == null || file.Length == 0)
                return Results.Json(new { error = "No file selected" }, statusCode: (int)HttpStatusCode.ExpectationFailed);

            if (file.Length > maxFileSize)
                return Results.Json(new { error = $"File size exceeds limit of {maxFileSize / (1024 * 1024)} MB" }, statusCode: (int)HttpStatusCode.Forbidden);

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExt.Contains(ext))
                return Results.Json(new { error = "File type not allowed" }, statusCode: (int)HttpStatusCode.Forbidden);

            long current = Directory.EnumerateFiles(baseUploads, "*", SearchOption.AllDirectories)
                                    .Sum(p => new FileInfo(p).Length);
            if (current + file.Length > maxStorage)
                return Results.Json(new { error = "Storage limit exceeded. (Server storage is full)" }, statusCode: (int)HttpStatusCode.ExpectationFailed);

            // Save in uploads/public/<ext>/ directory
            var dir = Path.Combine(baseUploads, "public", ext.TrimStart('.'));
            Directory.CreateDirectory(dir);

            var originalName = Path.GetFileNameWithoutExtension(file.FileName);

            // Optional expireMinutes from form
            int expireMinutes = 2;
            if (form.TryGetValue("expireMinutes", out var minutesValue) && int.TryParse(minutesValue, out var parsedMinutes))
                expireMinutes = Math.Clamp(parsedMinutes, 1, 1440); // 1 min – 24 hours

            var expireAt = DateTime.UtcNow.AddMinutes(expireMinutes);

            var safeName = $"{originalName}_{expireAt:yyyyMMddHHmmss}{ext}";
            var fullPath = Path.Combine(dir, safeName);

            await using var fs = new FileStream(fullPath, FileMode.Create);
            await file.CopyToAsync(fs);

            var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/files/public/{ext.TrimStart('.')}/{safeName}";

            // Optional maxViews
            int? maxViews = null;
            if (form.TryGetValue("maxViews", out var maxViewsVal) && int.TryParse(maxViewsVal, out var parsedMaxViews))
                maxViews = Math.Max(parsedMaxViews, 1); // at least 1

            // ✅ safe with ConcurrentDictionary
            fileStore[safeName] = new FileRecord
            {
                Path = fullPath,
                ExpireAt = expireAt,
                MaxViews = maxViews
            };

            // ✅ Register in expiryQueue (for BackgroundCleanup)
            lock (expiryLock)
            {
                if (!expiryQueue.TryGetValue(expireAt, out var list))
                {
                    list = new List<string>();
                    expiryQueue[expireAt] = list;
                }
                list.Add(safeName);
            }

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
        .DisableAntiforgery();
    }
}
