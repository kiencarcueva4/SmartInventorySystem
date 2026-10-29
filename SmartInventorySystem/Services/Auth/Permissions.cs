using System.Collections.Generic;
using System.Linq;

namespace SmartInventorySystem.Services.Auth
{
    /// <summary>
    /// Every action a user can be allowed to do, and which role gets which.
    /// This is the ONE place to change role access. Pages use [Authorize(Policy = Permissions.X)],
    /// the sidebar uses IAuthService.Can(...). Both read from this table.
    /// </summary>
    public static class Permissions
    {
        /// <summary>Claim type used inside the signed-in user's identity.</summary>
        public const string ClaimType = "permission";

        public const string DashboardView = "Dashboard.View";
        public const string InventoryView = "Inventory.View";
        public const string ItemsManage = "Items.Manage";
        public const string CategoriesManage = "Categories.Manage";
        public const string SuppliersManage = "Suppliers.Manage";
        public const string StockInRecord = "StockIn.Record";
        public const string StockOutRecord = "StockOut.Record";      // includes usage transactions
        public const string AnalyticsView = "Analytics.View";        // movement, forecasts, trends
        public const string RecommendationsView = "Recommendations.View";
        public const string RecommendationsDecide = "Recommendations.Decide"; // approve / defer / dismiss
        public const string RestockView = "Restock.View";
        public const string RestockManage = "Restock.Manage";       // mark ordered / received / cancelled
        public const string AlertsView = "Alerts.View";
        public const string ReportsView = "Reports.View";
        public const string UsersManage = "Users.Manage";
        public const string SettingsManage = "Settings.Manage";
        public const string AuditView = "Audit.View";

        public static IReadOnlyList<string> All { get; } = new[]
        {
            DashboardView, InventoryView, ItemsManage, CategoriesManage, SuppliersManage,
            StockInRecord, StockOutRecord, AnalyticsView, RecommendationsView, RecommendationsDecide,
            RestockView, RestockManage, AlertsView, ReportsView, UsersManage, SettingsManage, AuditView
        };

        private static readonly IReadOnlySet<string> None = new HashSet<string>();

        private static readonly Dictionary<string, IReadOnlySet<string>> ByRole = new()
        {
            // Owner: everything.
            [RoleNames.Administrator] = new HashSet<string>(All),

            // Inventory personnel: everything except user management, settings, audit log
            // and the owner's approve/defer/dismiss decision.
            [RoleNames.InventoryPersonnel] = new HashSet<string>(All.Where(p =>
                p != RecommendationsDecide && p != UsersManage && p != SettingsManage && p != AuditView)),

            // Sales / cashier: see availability and record material usage only.
            [RoleNames.SalesCashier] = new HashSet<string> { DashboardView, InventoryView, StockOutRecord },
        };

        public static IReadOnlySet<string> ForRole(string roleName) =>
            ByRole.TryGetValue(roleName, out var set) ? set : None;
    }
}
