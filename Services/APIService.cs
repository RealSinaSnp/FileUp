using System.Net;

public static class APIService
{
    public static void MapAPIUploadEndpoints(this IEndpointRouteBuilder app,
        string baseUploads,
        Dictionary<string, FileRecord> fileStore,
        HashSet<string> allowedExt,
        long maxStorage,
        long maxFileSize,
        SortedDictionary<DateTime, List<string>> expiryQueue,
        object expiryLock)
    {
        app.MapPost("/api/public/upload", async (IFormFile file, HttpContext ctx) =>
        {
            if (file == null || file.Length == 0)
                return Results.Json(new { error = "No file selected" }, statusCode: (int)HttpStatusCode.ExpectationFailed);

            if (file.Length > maxFileSize)
                return Results.Json(new { error = $"File size exceeds limit of {maxFileSize / (1024*1024)} MB" }, statusCode: (int)HttpStatusCode.Forbidden);

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExt.Contains(ext))
                return Results.Json(new { error = "File type not allowed" }, statusCode: (int)HttpStatusCode.Forbidden);

            long current = Directory.EnumerateFiles(baseUploads, "*", SearchOption.AllDirectories)
                                    .Sum(p => new FileInfo(p).Length);
            if (current + file.Length > maxStorage)
                return Results.Json(new { error = "Storage limit exceeded. (Server storage is full)" }, statusCode: (int)HttpStatusCode.ExpectationFailed);

            var dir = Path.Combine(baseUploads, ext.TrimStart('.'));
            Directory.CreateDirectory(dir);

            var fname = $"{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(dir, fname);

            await using var fs = new FileStream(fullPath, FileMode.Create);
            await file.CopyToAsync(fs);

            var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/files/public/{ext.TrimStart('.')}/{fname}";

            // Track expiry (all public files expire)
            var expireAt = DateTime.UtcNow.AddMinutes(2); // change later as needed
            lock (expiryLock)
            {
                if (!expiryQueue.ContainsKey(expireAt))
                    expiryQueue[expireAt] = new List<string>();
                expiryQueue[expireAt].Add(fullPath);
            }

            fileStore[fname] = new FileRecord { Path = fullPath, ExpireAt = expireAt };

            return Results.Ok(new { fileName = fname, size = file.Length, url, expireAt });
        })
        .DisableAntiforgery();
    }
}
