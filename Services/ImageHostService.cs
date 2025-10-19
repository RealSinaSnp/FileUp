using System.Net;
using System.Collections.Concurrent;

public static class ImghostService
{
    public static void MapImghostEndpoints(
        this IEndpointRouteBuilder app,
        string baseUploads,
        ConcurrentDictionary<string, FileRecord> fileStore,
        SortedDictionary<DateTime, List<string>> expiryQueue,
        object expiryLock,
        HashSet<string> allowedExt,
        long maxStorage,
        long maxFileSize)
    {
        app.MapPost("/api/files/imghost", async (HttpContext ctx) =>
        {
            try
            {
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var data = await ctx.Request.ReadFromJsonAsync<ShortLinkCreateRequest>();

                if (data == null || string.IsNullOrWhiteSpace(data.Url))
                    return Results.Json(new { error = "Missing URL" }, statusCode: (int)HttpStatusCode.BadRequest);

                if (!Uri.IsWellFormedUriString(data.Url, UriKind.Absolute))
                    return Results.Json(new { error = "Invalid URL format" }, statusCode: (int)HttpStatusCode.BadRequest);

                var ext = Path.GetExtension(data.Url).ToLowerInvariant();
                if (!allowedExt.Contains(ext))
                    return Results.Json(new { error = "File type not allowed" }, statusCode: (int)HttpStatusCode.Forbidden);

                if (!UploadCounter.CheckAndIncrement(ip))
                    return Results.Json(new { error = "Rate limit exceeded. Try again later." }, statusCode: (int)HttpStatusCode.TooManyRequests);

                long current = Directory.EnumerateFiles(baseUploads, "*", SearchOption.AllDirectories)
                                        .Sum(p => new FileInfo(p).Length);
                if (current > maxStorage)
                    return Results.Json(new { error = "Server storage full" }, statusCode: (int)HttpStatusCode.ExpectationFailed);

                // Download image
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(15);

                byte[] bytes;
                try
                {
                    bytes = await http.GetByteArrayAsync(data.Url);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Imghost] Download failed from {data.Url}: {ex.Message}");
                    return Results.Json(new { error = "Failed to fetch the provided URL" }, statusCode: (int)HttpStatusCode.BadRequest);
                }

                if (bytes.Length == 0)
                    return Results.Json(new { error = "Empty file" }, statusCode: (int)HttpStatusCode.ExpectationFailed);
                if (bytes.Length > maxFileSize)
                    return Results.Json(new { error = "File exceeds allowed size limit" }, statusCode: (int)HttpStatusCode.Forbidden);

                // ── Expiry & maxViews handling ───────────────────────
                int expireMinutes = 60; // default 1h
                int? maxViews = null;

                if (data.Expire > 0)
                    expireMinutes = Math.Clamp(data.Expire, 1, 60 * 24); // 1 min – 24h
                if (data.MaxViews > 0)
                    maxViews = Math.Clamp(data.MaxViews, 1, 200);

                var expireAt = DateTime.UtcNow.AddMinutes(expireMinutes);

                // ── Save locally ─────────────────────────────────────
                var subdir = ext.TrimStart('.');
                var dir = Path.Combine(baseUploads, "imghost", subdir);
                Directory.CreateDirectory(dir);

                var guidName = Guid.NewGuid().ToString("N");
                var fname = $"{guidName}_{expireAt:yyyyMMddHHmmss}{ext}";
                var fullPath = Path.Combine(dir, fname);
                await File.WriteAllBytesAsync(fullPath, bytes);

                var publicUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/files/imghost/{subdir}/{fname}";

                // ── Save in memory stores ───────────────────────────
                fileStore[fname] = new FileRecord
                {
                    Path = fullPath,
                    ExpireAt = expireAt,
                    MaxViews = maxViews
                };

                lock (expiryLock)
                {
                    if (!expiryQueue.TryGetValue(expireAt, out var list))
                    {
                        list = new List<string>();
                        expiryQueue[expireAt] = list;
                    }
                    list.Add(fname);
                }

                Logger.Log($"[Imghost] Image fetched: {data.Url} -> {publicUrl}, expires at {expireAt:u}");

                return Results.Ok(new
                {
                    fileName = fname,
                    url = publicUrl,
                    expireAt,
                    maxViews
                });
            }
            catch (Exception ex)
            {
                Logger.Log("[Imghost] EXCEPTION in /api/files/imghost:");
                Logger.Log(ex.ToString());
                return Results.Problem("Server exploded. Check logs.");
            }
        })
        .DisableAntiforgery()
        .RequireRateLimiting("uploadPolicy");
    }
}
