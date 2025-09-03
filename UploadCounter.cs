using System.Collections.Concurrent;

public static class UploadCounter
{
    /* key = IP, value = (count, firstUploadTime) */
    private static readonly ConcurrentDictionary<string, (int c, DateTime t)>
        _map = new();

    public static bool CheckAndIncrement(string ip)
    {
        var now = DateTime.UtcNow;
        _map.AddOrUpdate(
            ip,
            _ => (1, now),
            (_, v) =>
            {
                /* reset after 24 h */
                if ((now - v.t).TotalHours >= 24) return (1, now);
                return (v.c + 1, v.t);
            });

        return _map[ip].c <= 3;   // true = within limit
    }
}
