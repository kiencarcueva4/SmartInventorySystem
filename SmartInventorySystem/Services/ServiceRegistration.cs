using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SmartInventorySystem.Services.Admin;
using SmartInventorySystem.Services.Alerts;
using SmartInventorySystem.Services.Analytics;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Core;
using SmartInventorySystem.Services.Reordering;
using SmartInventorySystem.Services.Reports;

namespace SmartInventorySystem.Services
{
    public static class ServiceRegistration
    {
        /// <summary>Registers application-layer services. Later phases add theirs here.</summary>
        public static IServiceCollection AddAppServices(this IServiceCollection services)
        {
            // One authorization policy per permission, e.g. [Authorize(Policy = Permissions.ItemsManage)].
            services.AddAuthorizationCore(options =>
            {
                foreach (var permission in Permissions.All)
                {
                    options.AddPolicy(permission, policy => policy.RequireClaim(Permissions.ClaimType, permission));
                }
            });

            // Singletons: the desktop app has exactly one signed-in user at a time.
            services.AddSingleton<AppAuthStateProvider>();
            services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<AppAuthStateProvider>());
            services.AddSingleton<IAuditService, AuditService>();
            services.AddSingleton<IAuthService, AuthService>();

            // Phase 4: core inventory
            services.AddSingleton<ICategoryService, CategoryService>();
            services.AddSingleton<ISupplierService, SupplierService>();
            services.AddSingleton<IItemService, ItemService>();
            services.AddSingleton<IInventoryService, InventoryService>();
            services.AddSingleton<IStockService, StockService>();

            // Phase 5: dashboard
            services.AddSingleton<IDashboardService, DashboardService>();

            // Phase 6: analytics
            services.AddSingleton<IMovementService, MovementService>();
            services.AddSingleton<IForecastService, ForecastService>();
            services.AddSingleton<ITrendService, TrendService>();

            // Phase 7: reordering
            services.AddSingleton<IReorderService, ReorderService>();
            services.AddSingleton<IRestockService, RestockService>();

            // Phase 8: supporting features
            services.AddSingleton<IAlertService, AlertService>();
            services.AddSingleton<ISubstituteService, SubstituteService>();
            services.AddSingleton<IExportService, ExportService>();
            services.AddSingleton<IReportService, ReportService>();
            services.AddSingleton<IUserService, UserService>();
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IAuditQueryService, AuditQueryService>();

            return services;
        }
    }
}
