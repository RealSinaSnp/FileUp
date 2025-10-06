using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;

// Scan the uploads folder at startup,
// delete expired files,
// creates dictionary,
// and passes it to BackgroundCleanup

namespace FileUp.Controllers
{
    public static class StartupScan
    {
        public static SortedDictionary<DateTime, List<string>> ScanAndBuildQueue(
            string baseUploads,
            ConcurrentDictionary<string, FileRecord> fileStore)
        {
            var expiryQueue = new SortedDictionary<DateTime, List<string>>();

            foreach (var filePath in Directory.EnumerateFiles(baseUploads, "*.*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(filePath);
                var parts = fileName.Split('_');
                if (parts.Length < 2)
                    continue;

                var lastPart = Path.GetFileNameWithoutExtension(parts[^1]); // get expiry part after _
                if (!DateTime.TryParseExact(lastPart, "yyyyMMddHHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var expireAt))
                {
                    continue; // skip
                }

                if (expireAt <= DateTime.UtcNow)
                {
                    try
                    {
                        File.Delete(filePath);
                        Logger.Log($"[StartupScan] Deleted expired file: {filePath}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[StartupScan] Failed to delete {filePath}: {ex.Message}");
                    }
                    continue;
                }

                // add/update to ConcurrentDictionary (indexer works for add/update)
                fileStore[fileName] = new FileRecord { Path = filePath, ExpireAt = expireAt };

                // add to expiryQueue
                if (!expiryQueue.ContainsKey(expireAt))
                    expiryQueue[expireAt] = new List<string>();
                expiryQueue[expireAt].Add(fileName);
            }

            return expiryQueue;
        }
    }
}
