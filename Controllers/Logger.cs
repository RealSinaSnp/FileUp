using System;
using System.IO;

public static class Logger
{
    private static readonly object _lock = new object();
    private static readonly string logFile;
    private static readonly StreamWriter writer;

    static Logger()
    {
        string prodPath = "/var/lib/fileup";
        string devPath = "C:\\Users\\ssasa\\Desktop\\fileup\\FileUp\\uploads";

        if (Directory.Exists(prodPath))
            logFile = "/var/lib/fileup/upload_log.txt";
        else if (Directory.Exists(devPath))
            logFile = "C:\\Users\\ssasa\\Desktop\\fileup\\FileUp\\upload_log.txt";
        else
            throw new Exception("No valid upload directory found!");

        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);

        // Open the file once and flush immediately
        writer = new StreamWriter(logFile, append: true) { AutoFlush = true };
    }

    public static void Log(string message)
    {
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}";
        lock (_lock)
        {
            writer.WriteLine(line);
        }
    }
}
