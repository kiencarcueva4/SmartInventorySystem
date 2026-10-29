using System.Collections.Generic;

namespace SmartInventorySystem.Services.Auth
{
    /// <param name="AvailableInPhase">null = page exists. A number = planned for that phase
    /// (shown greyed out in the sidebar; no fake page is created).</param>
    public sealed record NavItem(string Title, string Href, string Permission, int? AvailableInPhase = null);

    public sealed record NavGroup(string? Title, IReadOnlyList<NavItem> Items);

    /// <summary>Sidebar structure. When a phase builds a page, remove its AvailableInPhase value.</summary>
    public static class NavCatalog
    {
        public static IReadOnlyList<NavGroup> Groups { get; } = new[]
        {
            new NavGroup(null, new[]
            {
                new NavItem("Dashboard", "/", Permissions.DashboardView),
            }),
            new NavGroup("Inventory", new[]
            {
                new NavItem("Inventory Monitoring", "/inventory", Permissions.InventoryView),
                new NavItem("Items", "/items", Permissions.ItemsManage),
                new NavItem("Categories", "/categories", Permissions.CategoriesManage),
                new NavItem("Stock-In", "/stock-in", Permissions.StockInRecord),
                new NavItem("Stock-Out", "/stock-out", Permissions.StockOutRecord),
            }),
            new NavGroup("Analytics", new[]
            {
                new NavItem("Fast/Slow-Moving", "/analytics/movement", Permissions.AnalyticsView),
                new NavItem("Demand Forecasting", "/analytics/forecasting", Permissions.AnalyticsView),
                new NavItem("Inventory Trends", "/analytics/trends", Permissions.AnalyticsView),
            }),
            new NavGroup("Reordering", new[]
            {
                new NavItem("Recommendations", "/reorder/recommendations", Permissions.RecommendationsView),
                new NavItem("Restock Records", "/reorder/restock", Permissions.RestockView),
            }),
            new NavGroup(null, new[]
            {
                new NavItem("Suppliers", "/suppliers", Permissions.SuppliersManage),
                new NavItem("Alerts", "/alerts", Permissions.AlertsView),
                new NavItem("Reports", "/reports", Permissions.ReportsView),
            }),
            new NavGroup("Administration", new[]
            {
                new NavItem("Users", "/users", Permissions.UsersManage),
                new NavItem("Settings", "/settings", Permissions.SettingsManage),
                new NavItem("Audit Logs", "/audit-logs", Permissions.AuditView),
            }),
        };
    }
}
