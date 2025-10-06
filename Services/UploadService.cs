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
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "x";

            if (file == null || file.Length == 0)
                return Results.Json(new { error = "No file selected" }, statusCode: (int)HttpStatusCode.ExpectationFailed);

            if (file.ContentType.StartsWith("application/x-msdownload") ||
                file.ContentType.Contains("exe") ||
                file.ContentType.Contains("php"))
            {
                Logger.Log($"[SECURITY] Suspicious upload attempt from {ip}: '{file.FileName}' ({file.ContentType})");
                return Results.Json(new { error = "Suspicious file type detected" }, statusCode: (int)HttpStatusCode.Forbidden);
            }


            if (!isAdmin && file.Length > maxFileGuest)
                return Results.Json(new { error = "Guests can upload max 3 MB" }, statusCode: (int)HttpStatusCode.Forbidden);
            if (file.Length > 24 * 1024 * 1024)
                return Results.Json(new { error = "Max upload size is 24 MB" }, statusCode: (int)HttpStatusCode.Forbidden);

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExt.Contains(ext))
                return Results.Json(new { error = "File type not allowed" }, statusCode: (int)HttpStatusCode.Forbidden);

            if (!isAdmin && !UploadCounter.CheckAndIncrement(ip))
                return Results.Json(new { error = "Rate limit exceeded. Try again later." }, statusCode: (int)HttpStatusCode.TooManyRequests);

            long current = Directory.EnumerateFiles(baseUploads, "*", SearchOption.AllDirectories)
                                    .Sum(p => new FileInfo(p).Length);
            if (current + file.Length > maxStorage)
            {
                Logger.Log($"[SECURITY] Suspicious upload attempt from {ip}: '{file.FileName}' ({file.ContentType})");
                return Results.Json(new { error = "Storage limit exceeded. (Server storage is full)" }, statusCode: (int)HttpStatusCode.ExpectationFailed);
            }
            var dir = Path.Combine(baseUploads, ext.TrimStart('.'));
            Directory.CreateDirectory(dir);

            // ── optional form fields ───────────────────────────────
            int expireMinutes = 1;
            int? maxViews = null;

            if (form.TryGetValue("expireMinutes", out var expStr) && int.TryParse(expStr, out var expVal))
                expireMinutes = Math.Clamp(expVal, 1, 60 * 24); // keep it between 1 min to 24h

            if (form.TryGetValue("maxViews", out var mvStr) && int.TryParse(mvStr, out var mvVal))
                maxViews = mvVal;

            if (maxViews.HasValue)
            {
                maxViews = Math.Clamp(maxViews.Value, 1, 200); // limit to 100
            }


            // Remove any weird or dangerous characters from original filename
            var originalName = Path.GetFileNameWithoutExtension(file.FileName);
            originalName = string.Join("_", originalName.Split(Path.GetInvalidFileNameChars()));
            originalName = System.Text.RegularExpressions.Regex.Replace(originalName, @"[^a-zA-Z0-9_-]", "_");


            DateTime? expireAt = null;
            if (!isAdmin)
            {
                expireAt = DateTime.UtcNow.AddMinutes(expireMinutes);
            }
            else
            {
                expireAt = null; // do it again cause i feel like it
                Logger.Log($"[Admin] file uploaded as admin. filename='{file.FileName}'");
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
