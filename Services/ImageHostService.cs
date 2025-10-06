using System.Net;

public static class ImghostService
{
    public static void MapImghostEndpoints(this IEndpointRouteBuilder app, 
                                           string baseUploads,
                                           Dictionary<string, ShortLinkRecord> shortLinkStore)
    {
        // POST /api/files/imghost
        app.MapPost("/api/files/imghost", async (HttpContext ctx) =>
        {
            try
            {
                var data = await ctx.Request.ReadFromJsonAsync<ShortLinkCreateRequest>();
                if (data == null || string.IsNullOrWhiteSpace(data.Url))
                    return Results.BadRequest(new { error = "Missing URL" });

                if (!Uri.IsWellFormedUriString(data.Url, UriKind.Absolute))
                    return Results.BadRequest(new { error = "Invalid URL format" });

                var ext = Path.GetExtension(data.Url).ToLowerInvariant();
                var valid = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
                if (!valid.Contains(ext))
                    return Results.BadRequest(new { error = "Invalid file extension" });

                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(data.Url);

                var subdir = ext.TrimStart('.');
                var dir = Path.Combine(baseUploads, subdir);
                Directory.CreateDirectory(dir);

                var filename = $"{Guid.NewGuid():N}{ext}";
                var fullPath = Path.Combine(dir, filename);
                await File.WriteAllBytesAsync(fullPath, bytes);

                var publicUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/files/{subdir}/{filename}";

                var id = Guid.NewGuid().ToString("N")[..6];
                var expireAt = DateTime.UtcNow.AddSeconds(data.Expire <= 0 ? 5 : data.Expire);
                shortLinkStore[id] = new ShortLinkRecord { OriginalUrl = publicUrl, FilePath = fullPath, ExpireAt = expireAt };

                var shortUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/s/{id}";
                return Results.Ok(new { link = shortUrl });
            }
            catch (Exception ex)
            {
                Logger.Log("[ImageHost] EXCEPTION in /api/files/imghost:");
                Logger.Log(ex.ToString());
                return Results.Problem("Server exploded. Check console.");
            }
        });
    }
}
