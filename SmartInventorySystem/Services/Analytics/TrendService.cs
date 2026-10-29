using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Core;

namespace SmartInventorySystem.Services.Analytics
{
    public interface ITrendService
    {
        Task<List<TrendRow>> GetAsync();
        Task<ServiceResult> GenerateAsync(DateTime from, DateTime to, int userId);
    }

    /// <summary>
    /// Splits the chosen range into whole 7-day periods (counted back from the end date) and, for each
    /// item and period, records beginning stock, stock-in, stock-out, ending stock, consumption,
    /// the number of days the item was out of stock, and an overstock flag.
    /// </summary>
    public sealed class TrendService : ITrendService
    {
        private const int PeriodDays = 7;
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;

        public TrendService(IDbContextFactory<AppDbContext> factory, IAuditService audit)
        {
            _factory = factory;
            _audit = audit;
        }

        public async Task<List<TrendRow>> GetAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var trends = await db.InventoryTrends.AsNoTracking()
                .Include(t => t.Item).ThenInclude(i => i.Category)
                .Where(t => t.Item.Status == Domain.Enums.RecordStatus.Active)
                .ToListAsync();

            return trends
                .OrderBy(t => t.Item.ItemName)
                .ThenBy(t => t.PeriodStart)
                .Select(t => new TrendRow(t.TrendId, t.ItemId, t.Item.ItemName, t.Item.CategoryId,
                    t.Item.Category.CategoryName, t.Item.UnitOfMeasure, t.Period, t.PeriodStart, t.PeriodEnd,
                    t.BeginningStock, t.StockInQuantity, t.StockOutQuantity, t.EndingStock, t.Consumption,
                    t.StockoutOccurrences, t.OverstockIndicator, t.TrendDirection, t.GeneratedAt))
                .ToList();
        }

        public async Task<ServiceResult> GenerateAsync(DateTime from, DateTime to, int userId)
        {
            var today = DateTime.Today;
            from = from.Date;
            to = to.Date;
            if (to > today) to = today;
            if (from > to) return ServiceResult.Fail("The start date must be on or before the end date.");

            var span = (to - from).Days + 1;
            if (span < PeriodDays * 2) return ServiceResult.Fail("Please choose a range of at least 14 days (two weekly periods).");
            if (span > 365) return ServiceResult.Fail("Please choose a range of one year or less.");

            var blocks = span / PeriodDays;                                  // whole weeks only
            var firstStart = to.AddDays(-(blocks * PeriodDays - 1));         // counted back from the end date

            try
            {
                await using var db = await _factory.CreateDbContextAsync();

                var overstockMultiplier = await SettingsReader.GetDecimalAsync(db, SettingKeys.ReorderOverstockMultiplier, 3m);
                var set = await HistoryLoader.LoadAsync(db);
                if (set.Items.Count == 0) return ServiceResult.Fail("There are no active items to analyze.");

                // Replace earlier results that start inside this range.
                var existing = await db.InventoryTrends
                    .Where(t => t.PeriodStart >= firstStart && t.PeriodStart <= to)
                    .ToListAsync();
                db.InventoryTrends.RemoveRange(existing);

                var now = DateTime.UtcNow;
                foreach (var h in set.Items)
                {
                    // Index 0 = the day before the first period; index i = (firstStart - 1 day) + i days.
                    var stock = h.EndOfDayStock(firstStart.AddDays(-1), to);

                    var rows = new List<InventoryTrend>();
                    var consumptions = new List<decimal>();

                    for (var b = 0; b < blocks; b++)
                    {
                        var start = firstStart.AddDays(b * PeriodDays);
                        var end = start.AddDays(PeriodDays - 1);

                        var beginning = stock[b * PeriodDays];
                        var ending = stock[b * PeriodDays + PeriodDays];

                        var stockoutDays = 0;
                        for (var d = b * PeriodDays + 1; d <= b * PeriodDays + PeriodDays; d++)
                            if (stock[d] <= 0) stockoutDays++;

                        var consumption = h.UsageBetween(start, end);
                        consumptions.Add(consumption);

                        rows.Add(new InventoryTrend
                        {
                            ItemId = h.ItemId,
                            Period = $"{start:yyyy-MM-dd} to {end:yyyy-MM-dd}",
                            PeriodStart = start,
                            PeriodEnd = end,
                            BeginningStock = Math.Round(beginning, 2),
                            StockInQuantity = Math.Round(h.InBetween(start, end), 2),
                            StockOutQuantity = Math.Round(h.OutBetween(start, end), 2),
                            EndingStock = Math.Round(ending, 2),
                            Consumption = Math.Round(consumption, 2),
                            StockoutOccurrences = stockoutDays,
                            OverstockIndicator = h.ReorderPoint > 0 && ending > h.ReorderPoint * overstockMultiplier,
                            GeneratedAt = now
                        });
                    }

                    // One direction for the whole range, stored on every period row of the item.
                    var direction = TrendCalculator.Direction(consumptions);
                    foreach (var r in rows)
                    {
                        r.TrendDirection = direction;
                        db.InventoryTrends.Add(r);
                    }
                }

                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "Trend Analysis", "InventoryTrend", null,
                    $"Generated trends for {set.Items.Count} items, {blocks} weekly periods ({firstStart:yyyy-MM-dd} to {to:yyyy-MM-dd}).");

                return ServiceResult.Ok(message:
                    $"Trend analysis finished: {set.Items.Count} items, {blocks} weekly periods ({firstStart:yyyy-MM-dd} to {to:yyyy-MM-dd}).");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Trend analysis failed.", ex);
                return ServiceResult.Fail("Unable to complete the analysis. Please try again.");
            }
        }
    }
}
