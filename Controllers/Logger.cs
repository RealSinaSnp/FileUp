using System;
using System.IO;

public static class Logger
{
    private static readonly object _lock = new object();
    private static readonly string logFile;

    static Logger()
    {


        string prodPath = "/var/lib/fileup/uploads";
        string devPath = "C:\\Users\\ssasa\\Desktop\\fileup\\FileUp\\uploads";

        if (Directory.Exists(prodPath))
        {
            logFile = "/var/lib/fileup/uploads/upload_log.txt";
        }
        else if (Directory.Exists(devPath))
        {
            logFile = "C:\\Users\\ssasa\\Desktop\\fileup\\FileUp\\upload_log.txt";
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
    }
    
}
