using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;

namespace FileUp.Controllers
{
    public static class StartupScan
    {
        /// Scan the uploads folder at startup,
        /// delete expired files,
        /// populate FileStore for scheduled deletion,
        /// Only deletes files with expiry in their names.
        public static SortedDictionary<DateTime, List<string>> ScanAndBuildQueue(
            string baseUploads,
            ConcurrentDictionary<string, FileRecord> fileStore)
        {
            var expiryQueue = new SortedDictionary<DateTime, List<string>>();

            foreach (var filePath in Directory.EnumerateFiles(baseUploads, "*.*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(filePath);
                // Only files with expiry timestamp in name (yyyyMMddHHmmss)
                var parts = fileName.Split('_');
                if (parts.Length < 2)
                    continue;

                var lastPart = Path.GetFileNameWithoutExtension(parts[^1]); // get expiry part
                if (!DateTime.TryParseExact(lastPart, "yyyyMMddHHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var expireAt))
                {
                    continue; // skip files without proper expiry
                }

                if (expireAt <= DateTime.UtcNow)
                {
                    try
                    {
                        File.Delete(filePath);
                        Console.WriteLine($"[StartupScan] Deleted expired file: {filePath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[StartupScan] Failed to delete {filePath}: {ex.Message}");
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
