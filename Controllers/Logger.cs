using System;
using System.IO;

public static class Logger
{
    private static readonly object _lock = new object();
    private static readonly string logFile = Path.Combine(AppContext.BaseDirectory, "upload_log.txt");

    public static void Log(string message)
    {
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}";
        lock (_lock)
        {
            File.AppendAllText(logFile, line + Environment.NewLine);
        }
    }
}
