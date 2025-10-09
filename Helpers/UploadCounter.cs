using System.Collections.Concurrent;

// limit uploads per IP, per 24 hours
public static class UploadCounter
{
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

        return _map[ip].c <= 5;   // change limit here
    }
}
