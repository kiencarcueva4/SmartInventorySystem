using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services;

namespace SmartInventorySystem
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            try
            {
                var services = new ServiceCollection();
                services.AddWpfBlazorWebView();
#if DEBUG
                services.AddBlazorWebViewDeveloperTools();
#endif
                services.AddInfrastructure();
                services.AddAppServices(); // authentication, authorization, audit (Phase 3+)

                var provider = services.BuildServiceProvider();

                // Create/upgrade the database and seed it BEFORE the UI starts.
                // Task.Run avoids deadlocking the WPF UI thread.
                Task.Run(() => DatabaseInitializer.InitializeAsync(provider)).GetAwaiter().GetResult();

                blazorWebView.Services = provider;
            }
            catch (Exception ex)
            {
                LogStartupError(ex);
                MessageBox.Show(
                    "The application could not start.\n\n" + ex.Message +
                    "\n\nDetails were saved to:\n" + AppPaths.LogFolder,
                    "Smart Inventory System", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown();
            }
        }

        private static void LogStartupError(Exception ex)
        {
            try
            {
                AppPaths.EnsureFolders();
                File.AppendAllText(
                    Path.Combine(AppPaths.LogFolder, "startup-error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // Logging must never crash the error handler.
            }
        }
    }
}
