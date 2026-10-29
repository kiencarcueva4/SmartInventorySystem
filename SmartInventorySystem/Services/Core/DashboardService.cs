using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;

namespace SmartInventorySystem.Services.Core
{
    public sealed record ChartPoint(string Label, double Value);

    public sealed record BarItem(string Label, double Value, string Display, string Kind = "info");

    public sealed record LowStockRow(int ItemId, string Name, string Unit, decimal Quantity, decimal ReorderPoint,
        decimal SafetyStock, StockStatus Status);

    public sealed record StockoutRiskRow(int ItemId, string Name, string Unit, decimal Quantity, decimal AvgDailyUsage,
        decimal DaysOfCover, int LeadTimeDays, string Risk);

    public sealed record RecommendationSummaryRow(int Id, string ItemName, string Unit, UrgencyLevel Urgency,
        decimal RecommendedQuantity, RecommendationStatus Status, DateTime CreatedAt);

    public sealed record MovementSummary(int Fast, int Moderate, int Slow, int NotAnalyzed, bool HasData);

    public sealed record TrendSummary(int Increasing, int Stable, int Decreasing, int Irregular, bool HasData);

    public sealed record TopMaterialRow(string Name, decimal ConsumptionValue, int Transactions);

    public sealed record DashboardData(
        int TotalItems,
        decimal TotalValue,
        Dictionary<StockStatus, int> StatusCounts,
        int AttentionTotal,
        List<LowStockRow> LowStock,
        int RiskTotal,
        List<StockoutRiskRow> Risks,
        List<ChartPoint> DailyUsage,
        int UsageTransactionsInWindow,
        List<TopMaterialRow> TopMaterials,
        MovementSummary Movement,
        TrendSummary Trend,
        bool HasRecommendations,
        int UrgentPending,
        int PendingTotal,
        List<RecommendationSummaryRow> RecentRecommendations);

    public static class UrgencyUi
    {
        public static string Label(UrgencyLevel u) => u switch
        {
            UrgencyLevel.Urgent => "URGENT",
            UrgencyLevel.Recommended => "RECOMMENDED",
            _ => "NOT YET NECESSARY"
        };

        public static string Kind(UrgencyLevel u) => u switch
        {
            UrgencyLevel.Urgent => "danger",
            UrgencyLevel.Recommended => "warn",
            _ => "muted"
        };
    }

    public interface IDashboardService
    {
        Task<DashboardData> GetAsync();
    }

    /// <summary>
    /// Builds the dashboard from real data only.
    /// Panels that depend on later phases (movement classification, trends, recommendations) read the
    /// same tables those phases will fill, so they light up automatically once data exists.
    ///
    /// Stockout-risk rule (explainable, no black box):
    ///   AverageDailyUsage = quantity used in the last 30 days / 30
    ///   DaysOfCover       = quantity on hand / AverageDailyUsage
    ///   LeadTime          = supplier lead time (or the default lead time setting)
    ///   HIGH risk   : DaysOfCover &lt; LeadTime               (will run out before a delivery can arrive)
    ///   MEDIUM risk : DaysOfCover &lt; LeadTime + safety days (setting Reorder.SafetyStockDays)
    /// </summary>
    public sealed class DashboardService : IDashboardService
    {
        private const int UsageWindowDays = 30;
        private readonly IDbContextFactory<AppDbContext> _factory;

        public DashboardService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

        public async Task<DashboardData> GetAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();

            var overstockMultiplier = await SettingsReader.GetDecimalAsync(db, SettingKeys.ReorderOverstockMultiplier, 3m);
            var defaultLeadTime = (int)await SettingsReader.GetDecimalAsync(db, SettingKeys.ReorderDefaultLeadTimeDays, 7m);
            var safetyDays = await SettingsReader.GetDecimalAsync(db, SettingKeys.ReorderSafetyStockDays, 3m);

            // ---------------- stock levels ----------------
            var items = await db.Items.AsNoTracking()
                .Include(i => i.Inventory)
                .Include(i => i.Supplier)
                .Where(i => i.Status == RecordStatus.Active)
                .ToListAsync();

            var levels = items.Select(i =>
            {
                var qty = i.Inventory?.QuantityOnHand ?? 0m;
                return new
                {
                    Item = i,
                    Qty = qty,
                    Status = StockStatusCalculator.Calculate(qty, i.SafetyStock, i.ReorderPoint, overstockMultiplier)
                };
            }).ToList();

            var statusCounts = Enum.GetValues<StockStatus>().ToDictionary(s => s, s => levels.Count(l => l.Status == s));
            var totalValue = levels.Sum(l => l.Qty * l.Item.UnitCost);

            var attention = levels
                .Where(l => l.Status is StockStatus.OutOfStock or StockStatus.Critical or StockStatus.LowStock)
                .OrderBy(l => StockStatusUi.Severity(l.Status))
                .ThenBy(l => l.Item.ItemName)
                .ToList();

            var lowStock = attention.Take(8)
                .Select(l => new LowStockRow(l.Item.ItemId, l.Item.ItemName, l.Item.UnitOfMeasure, l.Qty,
                    l.Item.ReorderPoint, l.Item.SafetyStock, l.Status))
                .ToList();

            // ---------------- usage in the last 30 days ----------------
            var windowStart = DateTime.Today.AddDays(-(UsageWindowDays - 1));
            var sinceUtc = windowStart.ToUniversalTime();
            var usage = await db.UsageTransactions.AsNoTracking()
                .Where(u => u.TransactionDate >= sinceUtc)
                .ToListAsync();

            static DateTime LocalDate(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().Date;

            var perDay = usage.GroupBy(u => LocalDate(u.TransactionDate)).ToDictionary(g => g.Key, g => g.Count());
            var dailyUsage = Enumerable.Range(0, UsageWindowDays)
                .Select(d => windowStart.AddDays(d).Date)
                .Select(day => new ChartPoint(day.ToString("MM-dd"), perDay.TryGetValue(day, out var c) ? c : 0))
                .ToList();

            var usedByItem = usage.GroupBy(u => u.ItemId).ToDictionary(g => g.Key, g => new
            {
                Quantity = g.Sum(u => u.QuantityUsed),
                Count = g.Count()
            });

            // ---------------- stockout risk ----------------
            var risks = new List<StockoutRiskRow>();
            foreach (var l in levels)
            {
                if (!usedByItem.TryGetValue(l.Item.ItemId, out var used) || used.Quantity <= 0) continue;

                var avgDaily = used.Quantity / UsageWindowDays;
                var cover = l.Qty / avgDaily;
                var leadTime = l.Item.Supplier?.LeadTimeDays ?? defaultLeadTime;

                string? risk = null;
                if (cover < leadTime) risk = "High";
                else if (cover < leadTime + safetyDays) risk = "Medium";

                if (risk is not null)
                {
                    risks.Add(new StockoutRiskRow(l.Item.ItemId, l.Item.ItemName, l.Item.UnitOfMeasure, l.Qty,
                        Math.Round(avgDaily, 2), Math.Round(cover, 1), leadTime, risk));
                }
            }
            risks = risks.OrderBy(r => r.DaysOfCover).ToList();

            // ---------------- top materials by consumption value ----------------
            var itemsById = items.ToDictionary(i => i.ItemId);
            var top = usedByItem
                .Where(kv => itemsById.ContainsKey(kv.Key))
                .Select(kv => new TopMaterialRow(itemsById[kv.Key].ItemName,
                    Math.Round(kv.Value.Quantity * itemsById[kv.Key].UnitCost, 2), kv.Value.Count))
                .OrderByDescending(t => t.ConsumptionValue)
                .Take(5)
                .ToList();

            // ---------------- movement classification (filled in Phase 6) ----------------
            var classifications = await db.MovementClassifications.AsNoTracking().ToListAsync();
            var activeIds = itemsById.Keys.ToHashSet();
            var latestMovement = classifications
                .Where(c => activeIds.Contains(c.ItemId))
                .GroupBy(c => c.ItemId)
                .Select(g => g.OrderByDescending(c => c.CalculatedAt).First().Classification)
                .ToList();

            var movement = new MovementSummary(
                latestMovement.Count(m => m == MovementClass.FastMoving),
                latestMovement.Count(m => m == MovementClass.ModerateMoving),
                latestMovement.Count(m => m == MovementClass.SlowMoving),
                items.Count - latestMovement.Count,
                latestMovement.Count > 0);

            // ---------------- inventory trends (filled in Phase 6) ----------------
            var trends = await db.InventoryTrends.AsNoTracking().ToListAsync();
            var latestTrend = trends
                .Where(t => activeIds.Contains(t.ItemId))
                .GroupBy(t => t.ItemId)
                .Select(g => g.OrderByDescending(t => t.GeneratedAt).First().TrendDirection)
                .ToList();

            var trend = new TrendSummary(
                latestTrend.Count(t => t == TrendDirection.Increasing),
                latestTrend.Count(t => t == TrendDirection.Stable),
                latestTrend.Count(t => t == TrendDirection.Decreasing),
                latestTrend.Count(t => t == TrendDirection.Irregular),
                latestTrend.Count > 0);

            // ---------------- reorder recommendations (filled in Phase 7) ----------------
            var hasRecommendations = await db.ReorderRecommendations.AnyAsync();
            var urgentPending = await db.ReorderRecommendations
                .CountAsync(r => r.Status == RecommendationStatus.Pending && r.Urgency == UrgencyLevel.Urgent);
            var pendingTotal = await db.ReorderRecommendations
                .CountAsync(r => r.Status == RecommendationStatus.Pending);

            var recent = await db.ReorderRecommendations.AsNoTracking()
                .Include(r => r.Item)
                .OrderByDescending(r => r.CreatedAt)
                .Take(5)
                .ToListAsync();

            var recentRows = recent.Select(r => new RecommendationSummaryRow(r.RecommendationId, r.Item.ItemName,
                r.Item.UnitOfMeasure, r.Urgency, r.RecommendedQuantity, r.Status, r.CreatedAt)).ToList();

            return new DashboardData(
                items.Count, totalValue, statusCounts, attention.Count, lowStock,
                risks.Count, risks.Take(8).ToList(), dailyUsage, usage.Count, top,
                movement, trend, hasRecommendations, urgentPending, pendingTotal, recentRows);
        }
    }
}
