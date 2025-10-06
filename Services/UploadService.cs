using System.Net;
using System.Globalization;
using System.Collections.Concurrent;

public static class UploadService
{
    public static void MapUploadEndpoints(
        this IEndpointRouteBuilder app,
        string baseUploads,
        ConcurrentDictionary<string, FileRecord> fileStore,
        SortedDictionary<DateTime, List<string>> expiryQueue,
        object expiryLock,
        HashSet<string> allowedExt,
        long maxStorage,
        long maxFileGuest)
    {
        app.MapPost("/api/files/upload", async (IFormFile file, HttpContext ctx) =>
        {
            var isAdmin = ctx.User.Identity?.IsAuthenticated == true;
            var form = await ctx.Request.ReadFormAsync();

            if (file == null || file.Length == 0)
                return Results.Json(new { error = "No file selected" }, statusCode: (int)HttpStatusCode.ExpectationFailed);

            if (!isAdmin && file.Length > maxFileGuest)
                return Results.Json(new { error = "Guests can upload max 3 MB" }, statusCode: (int)HttpStatusCode.Forbidden);
            if (file.Length > 24 * 1024 * 1024)
                return Results.Json(new { error = "Max upload size is 24 MB" }, statusCode: (int)HttpStatusCode.Forbidden);

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExt.Contains(ext))
                return Results.Json(new { error = "File type not allowed" }, statusCode: (int)HttpStatusCode.Forbidden);

            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "x";
            if (!isAdmin && !UploadCounter.CheckAndIncrement(ip))
                return Results.Json(new { error = "Rate limit exceeded. Try again later." }, statusCode: (int)HttpStatusCode.TooManyRequests);

            long current = Directory.EnumerateFiles(baseUploads, "*", SearchOption.AllDirectories)
                                    .Sum(p => new FileInfo(p).Length);
            if (current + file.Length > maxStorage)
                return Results.Json(new { error = "Storage limit exceeded. (Server storage is full)" }, statusCode: (int)HttpStatusCode.ExpectationFailed);

            var dir = Path.Combine(baseUploads, ext.TrimStart('.'));
            Directory.CreateDirectory(dir);

            // ── optional form fields ───────────────────────────────
            int? expireMinutes = null;
            int? maxViews = null;

            if (form.TryGetValue("expireMinutes", out var expStr) && int.TryParse(expStr, out var expVal))
                expireMinutes = expVal;

            if (form.TryGetValue("maxViews", out var mvStr) && int.TryParse(mvStr, out var mvVal))
                maxViews = mvVal;

            var originalName = Path.GetFileNameWithoutExtension(file.FileName);
            DateTime? expireAt = null;
            if (!isAdmin)
            {
                expireAt = DateTime.UtcNow.AddMinutes(1); // configurable expiry
            }

            // filename: originalName_yyyyMMddHHmmss.ext
            var fname = expireAt.HasValue
                        ? $"{originalName}_{expireAt:yyyyMMddHHmmss}{ext}"
                        : $"{originalName}{ext}";

            var fullPath = Path.Combine(dir, fname);
            await using var fs = new FileStream(fullPath, FileMode.Create);
            await file.CopyToAsync(fs);

            // add to ConcurrentDictionary
            fileStore[fname] = new FileRecord { Path = fullPath, ExpireAt = expireAt, MaxViews = maxViews };
            if (expireAt.HasValue)
            {
                lock (expiryLock)
                {
                    if (!expiryQueue.TryGetValue(expireAt.Value, out var list))
                    {
                        list = new List<string>();
                        expiryQueue[expireAt.Value] = list;
                    }
                    list.Add(fname);
                }
            }

            var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/files/{ext.TrimStart('.')}/{fname}";
            var originalFullName = file.FileName;
            Logger.Log($"[UploadService] Incoming file.FileName='{originalFullName}', expireAt='{expireAt}, url='{url}'");


            return Results.Ok(new
            {
                fileName = fname,
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
