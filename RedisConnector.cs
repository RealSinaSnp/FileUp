using StackExchange.Redis;

// global for simplicity
public static class RedisConnector
{
    private static Lazy<ConnectionMultiplexer> lazyConnection = new Lazy<ConnectionMultiplexer>(() =>
    {
        return ConnectionMultiplexer.Connect("localhost:6379"); // or your VPS IP
    });

    public static ConnectionMultiplexer Connection => lazyConnection.Value;

    public static IDatabase DB => Connection.GetDatabase();
}
