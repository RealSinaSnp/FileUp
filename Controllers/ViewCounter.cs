using System;
using System.IO;
using System.Collections.Concurrent;

namespace FileUp.Controllers
{
    public static class ViewCounter
    {
        // Increment the view count
        public static long IncrementView(string fileName)
        {
            var db = RedisConnector.DB;
            long views = db.StringIncrement(fileName);
            Console.WriteLine($"[ViewCounter] {fileName} has been viewed {views} times.");
            return views;
        }

        // get current view count
        public static long GetViewCount(string fileName)
        {
            var db = RedisConnector.DB;
            var val = db.StringGet(fileName);
            return val.HasValue ? (long)val : 0;
        }

        // Optional: reset view counter manually
        public static void ResetViewCount(string fileName)
        {
            var db = RedisConnector.DB;
            db.KeyDelete(fileName);
            Console.WriteLine($"[ViewCounter] View count for {fileName} reset.");
        }
    }
}
