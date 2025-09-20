using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FileUp.Controllers
{
    public static class BackgroundCleanup
    {
        public static void Start(
            Dictionary<string, FileRecord> fileStore,
            SortedDictionary<DateTime, List<string>> expiryQueue,
            object expiryLock)
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        KeyValuePair<DateTime, List<string>>? nextExpire = null;

                        lock (expiryLock)
                        {
                            if (expiryQueue.Any())
                                nextExpire = expiryQueue.First();
                        }

                        if (nextExpire == null)
                        {
                            await Task.Delay(10000); // wait 10s if nothing scheduled
                            continue;
                        }

                        var expireTime = nextExpire.Value.Key;
                        var delay = expireTime - DateTime.UtcNow;
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay);
                            continue;
                        }

                        List<string> filesToDelete;
                        lock (expiryLock)
                        {
                            filesToDelete = expiryQueue[expireTime];
                            expiryQueue.Remove(expireTime);
                        }

                        foreach (var fname in filesToDelete)
                        {
                            if (fileStore.TryGetValue(fname, out var rec))
                            {
                                try
                                {
                                    if (File.Exists(rec.Path))
                                    {
                                        File.Delete(rec.Path);
                                        Console.WriteLine($"[BackgroundCleanup] Deleted expired file: {rec.Path}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[BackgroundCleanup] Failed to delete {rec.Path}: {ex.Message}");
                                }

                                fileStore.Remove(fname);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BackgroundCleanup] Loop error: {ex.Message}");
                        await Task.Delay(5000); // small delay on error
                    }
                }
            });
        }
    }
}
