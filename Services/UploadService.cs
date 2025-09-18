using System.Net;

public static class UploadService
{
    public static void MapUploadEndpoints(this IEndpointRouteBuilder app, string baseUploads, 
                                          Dictionary<string, FileRecord> fileStore,
                                          HashSet<string> allowedExt,
                                          long maxStorage,
                                          long maxFileGuest)
    {
        app.MapPost("/api/files/upload", async (IFormFile file, HttpContext ctx) =>
        {
            var isAdmin = ctx.User.Identity?.IsAuthenticated == true;
            if (file == null || file.Length == 0) return Results.BadRequest(new { error = "No file selected" });

            if (!isAdmin && file.Length > maxFileGuest)
                return Results.BadRequest(new { error = "Guests can upload max 3 MB" });
            if (file.Length > 24 * 1024 * 1024)
                return Results.BadRequest(new { error = "Max 24 MB" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExt.Contains(ext)) return Results.BadRequest(new { error = "File type not allowed" });

            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "x";
            if (!isAdmin && !UploadCounter.CheckAndIncrement(ip))
                return Results.Json(new { error = "Rate limit exceeded. Try again later." }, statusCode: (int)HttpStatusCode.TooManyRequests);

            long current = Directory.EnumerateFiles(baseUploads, "*", SearchOption.AllDirectories)
                                    .Sum(p => new FileInfo(p).Length);
            if (current + file.Length > maxStorage)
                return Results.Json(new { error = "Storage limit exceeded. (Server storage is full)" }, statusCode: (int)HttpStatusCode.TooManyRequests);

            var dir = Path.Combine(baseUploads, ext.TrimStart('.'));
            Directory.CreateDirectory(dir);

            var fname = $"{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(dir, fname);

            await using var fs = new FileStream(fullPath, FileMode.Create);
            await file.CopyToAsync(fs);

            var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/files/{ext.TrimStart('.')}/{fname}";

            // track expiry
            DateTime? expireAt = null;
            if (!isAdmin) // guests get auto-expiry
            {
                expireAt = DateTime.UtcNow.AddSeconds(2);
            }

            fileStore[fname] = new FileRecord { Path = fullPath, ExpireAt = expireAt };

            return Results.Ok(new { fileName = fname, size = file.Length, url, expireAt });
        })
        .DisableAntiforgery()
        .RequireRateLimiting("uploadPolicy");
    }
}
