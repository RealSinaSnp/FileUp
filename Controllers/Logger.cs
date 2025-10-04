using System;
using System.IO;

public static class Logger
{
    private static readonly object _lock = new object();
    private static string logFile;

    static Logger()
    {
        string prodPath = "/var/lib/fileup";
        string devPath = "C:\\Users\\ssasa\\Desktop\\fileup\\FileUp";

        if (Directory.Exists(prodPath))
        {
            logFile = Path.Combine(prodPath, "upload.log");
        }
        else if (Directory.Exists(devPath))
        {
            logFile = Path.Combine(devPath, "upload.log");
        }
        else
        {
            throw new Exception("No valid upload directory found!");
        }

        // create if doesn't exist
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
    }

    public static void Log(string message)
    {
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}";
        lock (_lock)
        {
            File.AppendAllText(logFile, line + Environment.NewLine);
        }
        Console.WriteLine(message);
    }
}
