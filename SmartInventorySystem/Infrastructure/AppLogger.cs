using System;
using System.IO;

namespace SmartInventorySystem.Infrastructure
{
    /// <summary>
    /// Writes technical errors to Documents\SmartInventorySystem\logs so users only ever
    /// see friendly messages (never raw database exceptions).
    /// </summary>
    public static class AppLogger
    {
        private static readonly object Gate = new();

        public static void Error(string message, Exception? ex = null)
        {
            try
            {
                AppPaths.EnsureFolders();
                var file = Path.Combine(AppPaths.LogFolder, $"app-{DateTime.Now:yyyyMMdd}.log");
                var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}" +
                           (ex is null ? string.Empty : Environment.NewLine + ex) + Environment.NewLine;
                lock (Gate) { File.AppendAllText(file, text); }
            }
            catch (Exception)
            {
                // Logging must never crash the application.
            }
        }
    }
}
