using System;
using System.IO;

public static class Logger
{
    private static readonly object _lock = new object();
    private static readonly string logFile;
    private static readonly StreamWriter? writer;

    static Logger()
    {
        try
        {
            // prefer app-owned folder in production
            string prodDir = "/var/lib/fileup";
            string devDir = Path.Combine(AppContext.BaseDirectory, "logs"); // safe dev path under app

            string dir;
            if (Directory.Exists(prodDir))
                dir = prodDir;
            else
                dir = devDir;

            Directory.CreateDirectory(dir); // ensure exists
            logFile = Path.Combine(dir, "upload_log.txt");

            // Open once, append, and flush immediately
            writer = new StreamWriter(logFile, append: true) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            // If we can't open file, still surface the error to journal so we know why
            Console.Error.WriteLine($"[Logger] static ctor failed: {ex}");
            // writer stays null — Log() will still write to Console.Error
        }
    }

    public static void Log(string message)
    {
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}";
        lock (_lock)
        {
            try
            {
                if (writer != null)
                {
                    writer.WriteLine(line);
                }
                else
                {
                    // fallback: print to stderr so systemd/journal shows it
                    Console.Error.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                // if file write fails, at least write to journal so we can debug
                Console.Error.WriteLine($"[Logger] Write failed: {ex}. Original: {line}");
            }
        }
    }
}
