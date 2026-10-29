using System;
using System.IO;

namespace SmartInventorySystem.Infrastructure
{
    public static class AppPaths
    {
        public static string DataFolder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SmartInventorySystem");

        public static string DatabasePath => Path.Combine(DataFolder, "smart_inventory.db");
        public static string LogFolder => Path.Combine(DataFolder, "logs");

        public static void EnsureFolders()
        {
            Directory.CreateDirectory(DataFolder);
            Directory.CreateDirectory(LogFolder);
        }
    }
}
