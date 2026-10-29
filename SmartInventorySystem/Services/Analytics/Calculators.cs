using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;

namespace SmartInventorySystem.Services.Analytics
{
    // =====================================================================================
    // History of one item, loaded once and reused by movement, forecast and trend analysis.
    // Everything is keyed by LOCAL calendar date (transactions are stored in UTC).
    // =====================================================================================
    internal sealed class ItemHistory
    {
        public int ItemId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Sku { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;
        public int CategoryId { get; init; }
        public string CategoryName { get; init; } = string.Empty;
        public decimal CurrentQuantity { get; init; }
        public decimal ReorderPoint { get; init; }
        public decimal SafetyStock { get; init; }

        public Dictionary<DateTime, decimal> Usage { get; } = new();
        public Dictionary<DateTime, decimal> In { get; } = new();
        public Dictionary<DateTime, decimal> Out { get; } = new();
        public DateTime? LastUsed { get; set; }

        private static decimal Get(Dictionary<DateTime, decimal> map, DateTime day) =>
            map.TryGetValue(day, out var v) ? v : 0m;

        public decimal UsageOn(DateTime day) => Get(Usage, day);

        public decimal UsageBetween(DateTime from, DateTime to)
        {
            decimal sum = 0m;
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) sum += Get(Usage, d);
            return sum;
        }

        public decimal InBetween(DateTime from, DateTime to)
        {
            decimal sum = 0m;
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) sum += Get(In, d);
            return sum;
        }

        public decimal OutBetween(DateTime from, DateTime to)
        {
            decimal sum = 0m;
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) sum += Get(Out, d);
            return sum;
        }

        /// <summary>
        /// End-of-day stock for every day from..to (to must be today or earlier).
        /// Rebuilt backwards from today's quantity:
        ///     stock at end of day D-1 = stock at end of day D - stock-in on D + stock-out on D
        /// </summary>
        public decimal[] EndOfDayStock(DateTime from, DateTime to)
        {
            var first = from.Date;
            var last = to.Date;
            var result = new decimal[(last - first).Days + 1];

            var stock = CurrentQuantity;
            for (var d = DateTime.Today; d >= first; d = d.AddDays(-1))
            {
                if (d <= last) result[(d - first).Days] = stock;
                stock = stock - Get(In, d) + Get(Out, d);
            }
            return result;
        }
    }

    internal sealed class HistorySet
    {
        public List<ItemHistory> Items { get; } = new();
        public DateTime? EarliestUsage { get; set; }
    }

    internal static class HistoryLoader
    {
        public static DateTime LocalDate(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().Date;

        /// <summary>
        /// Loads every active item with its full usage / stock-in / stock-out history.
        /// A printing shop produces a few thousand rows a year, so loading them once and
        /// calculating in memory is simple, fast and keeps the formulas easy to read.
        /// </summary>
        public static async Task<HistorySet> LoadAsync(AppDbContext db)
        {
            var items = await db.Items.AsNoTracking()
                .Include(i => i.Category)
                .Include(i => i.Inventory)
                .Where(i => i.Status == RecordStatus.Active)
                .OrderBy(i => i.ItemName)
                .ToListAsync();

            var usage = await db.UsageTransactions.AsNoTracking()
                .Select(u => new { u.ItemId, u.QuantityUsed, u.TransactionDate }).ToListAsync();
            var ins = await db.StockIns.AsNoTracking()
                .Select(s => new { s.ItemId, s.Quantity, s.StockInDate }).ToListAsync();
            var outs = await db.StockOuts.AsNoTracking()
                .Select(s => new { s.ItemId, s.Quantity, s.StockOutDate }).ToListAsync();

            var set = new HistorySet();
            var byId = new Dictionary<int, ItemHistory>();
            foreach (var i in items)
            {
                var h = new ItemHistory
                {
                    ItemId = i.ItemId, Name = i.ItemName, Sku = i.SKU, Unit = i.UnitOfMeasure,
                    CategoryId = i.CategoryId, CategoryName = i.Category.CategoryName,
                    CurrentQuantity = i.Inventory?.QuantityOnHand ?? 0m,
                    ReorderPoint = i.ReorderPoint, SafetyStock = i.SafetyStock
                };
                byId[i.ItemId] = h;
                set.Items.Add(h);
            }

            foreach (var u in usage)
            {
                var day = LocalDate(u.TransactionDate);
                if (set.EarliestUsage is null || day < set.EarliestUsage) set.EarliestUsage = day;

                if (!byId.TryGetValue(u.ItemId, out var h)) continue;
                h.Usage[day] = (h.Usage.TryGetValue(day, out var cur) ? cur : 0m) + u.QuantityUsed;
                if (h.LastUsed is null || day > h.LastUsed) h.LastUsed = day;
            }
            foreach (var s in ins)
            {
                if (!byId.TryGetValue(s.ItemId, out var h)) continue;
                var day = LocalDate(s.StockInDate);
                h.In[day] = (h.In.TryGetValue(day, out var cur) ? cur : 0m) + s.Quantity;
            }
            foreach (var s in outs)
            {
                if (!byId.TryGetValue(s.ItemId, out var h)) continue;
                var day = LocalDate(s.StockOutDate);
                h.Out[day] = (h.Out.TryGetValue(day, out var cur) ? cur : 0m) + s.Quantity;
            }

            return set;
        }
    }

    // =====================================================================================
    // MOVEMENT CLASSIFICATION
    // =====================================================================================
    public sealed record MovementMetrics(decimal TotalConsumption, int Frequency, decimal AverageDailyUsage,
        decimal AverageInventory, decimal TurnoverRate, decimal FirstHalfUsage, decimal SecondHalfUsage);

    public static class MovementCalculator
    {
        /// <summary>
        /// For the chosen date range (D days):
        ///   TotalConsumption  = quantity used in the range
        ///   Frequency         = number of days with any usage
        ///   AverageDailyUsage = TotalConsumption / D
        ///   AverageInventory  = average end-of-day stock over the range
        ///   TurnoverRate      = TotalConsumption / AverageInventory
        /// </summary>
        internal static MovementMetrics Measure(ItemHistory h, DateTime from, DateTime to)
        {
            var days = (to.Date - from.Date).Days + 1;
            var total = h.UsageBetween(from, to);

            var frequency = 0;
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
                if (h.UsageOn(d) > 0) frequency++;

            var stock = h.EndOfDayStock(from, to);
            var avgInventory = stock.Length == 0 ? 0m : stock.Average();
            var turnover = avgInventory > 0 ? total / avgInventory : 0m;

            var mid = from.Date.AddDays(days / 2 - 1);
            var firstHalf = h.UsageBetween(from, mid);
            var secondHalf = total - firstHalf;

            return new MovementMetrics(total, frequency, total / days, avgInventory, turnover, firstHalf, secondHalf);
        }

        /// <summary>Fast if turnover is at or above fastMin, Slow if at or below slowMax, otherwise Moderate.</summary>
        public static MovementClass Classify(decimal turnover, decimal fastMin, decimal slowMax)
        {
            if (turnover >= fastMin) return MovementClass.FastMoving;
            if (turnover <= slowMax) return MovementClass.SlowMoving;
            return MovementClass.ModerateMoving;
        }

        /// <summary>
        /// Obsolescence / ageing risk. Points are added for each warning sign:
        ///   Not used for a long time   : +2 (>= no-use days or never used), +1 (>= half of that)
        ///   Too much stock for demand  : +2 (more than 180 days of cover, or stock but no usage), +1 (more than 90 days)
        ///   Usage declining            : +1 (second half of the period is under half of the first half)
        ///   Slow-moving                : +1
        /// 4 or more points = High, 2 or 3 = Medium, otherwise Low.
        /// </summary>
        public static RiskLevel ObsolescenceRisk(int? daysSinceLastUse, int noUseDays, decimal currentQuantity,
            decimal averageDailyUsage, decimal firstHalfUsage, decimal secondHalfUsage, MovementClass classification)
        {
            var points = 0;

            if (daysSinceLastUse is null || daysSinceLastUse >= noUseDays) points += 2;
            else if (daysSinceLastUse >= noUseDays / 2) points += 1;

            if (currentQuantity > 0)
            {
                if (averageDailyUsage <= 0) points += 2;
                else
                {
                    var cover = currentQuantity / averageDailyUsage;
                    if (cover > 180) points += 2;
                    else if (cover > 90) points += 1;
                }
            }

            if (firstHalfUsage > 0 && secondHalfUsage < firstHalfUsage * 0.5m) points += 1;
            if (classification == MovementClass.SlowMoving) points += 1;

            return points >= 4 ? RiskLevel.High : points >= 2 ? RiskLevel.Medium : RiskLevel.Low;
        }
    }

    // =====================================================================================
    // DEMAND FORECASTING
    // =====================================================================================
    public static class ForecastCalculator
    {
        /// <summary>
        /// Weighted Moving Average:
        ///   Forecast = (Demand1 x Weight1) + (Demand2 x Weight2) + (Demand3 x Weight3)
        /// demandsMostRecentFirst[0] is the most recent complete period. Weights must total 1.00.
        /// </summary>
        public static decimal WeightedMovingAverage(IReadOnlyList<decimal> demandsMostRecentFirst, IReadOnlyList<decimal> weights)
        {
            if (demandsMostRecentFirst.Count != weights.Count)
                throw new ArgumentException("Each period needs exactly one weight.");

            decimal forecast = 0m;
            for (var i = 0; i < weights.Count; i++) forecast += demandsMostRecentFirst[i] * weights[i];
            return forecast;
        }

        /// <summary>
        /// Simple Exponential Smoothing:  Level(t) = alpha x Demand(t) + (1 - alpha) x Level(t-1),
        /// starting with Level(1) = Demand(1). The final level is the forecast for the next period.
        /// </summary>
        public static decimal SimpleExponentialSmoothing(IReadOnlyList<decimal> demandsOldestFirst, decimal alpha)
        {
            if (demandsOldestFirst.Count == 0) return 0m;

            var level = demandsOldestFirst[0];
            for (var i = 1; i < demandsOldestFirst.Count; i++)
                level = alpha * demandsOldestFirst[i] + (1 - alpha) * level;
            return level;
        }

        public static bool WeightsAreValid(decimal w1, decimal w2, decimal w3) =>
            w1 >= 0 && w2 >= 0 && w3 >= 0 && Math.Abs(w1 + w2 + w3 - 1m) <= 0.001m;

        /// <summary>
        /// Absolute error = |actual - forecast|.  Percentage error = absolute error / actual x 100
        /// (not defined when actual is 0 but something was forecast).
        /// </summary>
        public static (decimal Absolute, decimal? Percentage) Error(decimal forecast, decimal actual)
        {
            var absolute = Math.Abs(actual - forecast);
            decimal? percentage;
            if (actual > 0) percentage = absolute / actual * 100m;
            else if (forecast == 0) percentage = 0m;
            else percentage = null;
            return (absolute, percentage);
        }

        /// <summary>MAE = average absolute error.  MAPE = average percentage error.</summary>
        public static ForecastAccuracy Summarize(IEnumerable<ForecastRow> rows)
        {
            var evaluated = rows.Where(r => r.AbsoluteError is not null).ToList();
            var pct = evaluated.Where(r => r.PercentageError is not null).Select(r => r.PercentageError!.Value).ToList();

            var perItem = evaluated
                .GroupBy(r => new { r.ItemId, r.ItemName, r.Unit })
                .Select(g =>
                {
                    var p = g.Where(r => r.PercentageError is not null).Select(r => r.PercentageError!.Value).ToList();
                    return new ForecastAccuracyRow(g.Key.ItemName, g.Key.Unit, g.Count(),
                        g.Average(r => r.AbsoluteError!.Value), p.Count > 0 ? (decimal?)p.Average() : null);
                })
                .OrderBy(r => r.ItemName)
                .ToList();

            return new ForecastAccuracy(evaluated.Count, pct.Count > 0 ? (decimal?)pct.Average() : null, perItem);
        }
    }

    // =====================================================================================
    // INVENTORY TREND
    // =====================================================================================
    public static class TrendCalculator
    {
        /// <summary>
        /// Direction of consumption across the periods (least-squares line):
        ///   change = slope x (periods - 1) / average consumption
        ///   change >= +20%  -> Increasing      change <= -20% -> Decreasing
        ///   otherwise: coefficient of variation above 0.75 -> Irregular, else Stable
        /// </summary>
        public static TrendDirection Direction(IReadOnlyList<decimal> consumption)
        {
            var n = consumption.Count;
            if (n < 2) return TrendDirection.Stable;

            var y = consumption.Select(c => (double)c).ToArray();
            var mean = y.Average();
            if (mean <= 0) return TrendDirection.Stable;

            var xMean = (n - 1) / 2.0;
            var numerator = 0.0;
            var denominator = 0.0;
            for (var i = 0; i < n; i++)
            {
                numerator += (i - xMean) * (y[i] - mean);
                denominator += (i - xMean) * (i - xMean);
            }

            var slope = numerator / denominator;
            var change = slope * (n - 1) / mean;

            if (change >= 0.20) return TrendDirection.Increasing;
            if (change <= -0.20) return TrendDirection.Decreasing;

            var variance = y.Select(v => (v - mean) * (v - mean)).Average();
            var cv = Math.Sqrt(variance) / mean;
            return cv > 0.75 ? TrendDirection.Irregular : TrendDirection.Stable;
        }
    }
}
