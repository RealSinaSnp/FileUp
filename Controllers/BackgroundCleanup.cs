using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;

// 

namespace FileUp.Controllers
{
    public static class BackgroundCleanup
    {
        // Accept ConcurrentDictionary for thread-safe access
        public static void Start(
            ConcurrentDictionary<string, FileRecord> fileStore,
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

                        // pick the earliest expiry under lock
                        lock (expiryLock)
                        {
                            if (expiryQueue.Any())
                                nextExpire = expiryQueue.First();
                        }

                        if (nextExpire == null)
                        {
                            await Task.Delay(1*3600*1000); // if nothing scheduled, chill (in ms)
                            continue;
                        }

                        var expireTime = nextExpire.Value.Key;
                        var now = DateTime.UtcNow;

                        if (expireTime <= now)
                        {
                            List<string> filesToDelete;
                            lock (expiryLock)
                            {
                                if (!expiryQueue.TryGetValue(expireTime, out filesToDelete))
                                    continue;

                                expiryQueue.Remove(expireTime);
                            }

                            foreach (var fname in filesToDelete)
                            {
                                if (fileStore.TryRemove(fname, out var rec))
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
                                }
                            }
                            lock (expiryLock)
                            {
                                Logger.Log("[BackgroundCleanup] Current expiryQueue:");
                                if (expiryQueue.Any())
                                {
                                    foreach (var kv in expiryQueue)
                                        Console.WriteLine($"  {kv.Key:u} <-> {string.Join(", ", kv.Value)}");
                                }
                                else
                                {
                                    Console.WriteLine("  (empty)");
                                }
                            }
                        }
                        else
                        {
                            // wait until 'closer to expiry' or a 'short delay'
                            var shortDelay = TimeSpan.FromSeconds(10);
                            var wait = expireTime - now < shortDelay ? expireTime - now : shortDelay;
                            await Task.Delay(wait);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BackgroundCleanup] Loop error: {ex.Message}");
                        await Task.Delay(1*3600*1000); // on error, chill for a while (in ms)
                    }
                }
            });
        }
    }
}
