using System;
using System.Collections.Generic;

namespace GraveyardKeeperCoop.Network
{
    public static class NetworkDiagnostics
    {
        private const int MaxEntries = 12;
        private static readonly Queue<string> recentErrors = new Queue<string>();

        public static void RecordError(string source, string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            string entry = $"{DateTime.Now:HH:mm:ss} [{source}] {message}";
            recentErrors.Enqueue(entry);
            while (recentErrors.Count > MaxEntries)
                recentErrors.Dequeue();
        }

        public static string[] RecentErrors()
        {
            return recentErrors.ToArray();
        }
    }
}
