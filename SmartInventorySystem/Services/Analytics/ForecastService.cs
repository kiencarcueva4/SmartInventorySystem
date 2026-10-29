using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Core;

namespace SmartInventorySystem.Services.Analytics
{
    public interface IForecastService
    {
        Task<ForecastSettingsInput> GetSettingsAsync();
        Task<ServiceResult> SaveSettingsAsync(ForecastSettingsInput input, int userId);
        Task<List<ForecastRow>> GetAsync();
        /// <summary>Forecast the next period for every active item, then fill in actuals for finished periods.</summary>
        Task<ServiceResult> GenerateAsync(ForecastMethod method, int userId);
        /// <summary>Re-forecast recent finished periods using only the data that existed back then, and compare with actuals.</summary>
        Task<ServiceResult> BacktestAsync(ForecastMethod method, int periods, int userId);
    }

    /// <summary>
    /// A "period" is PeriodDays long (default 7). For a forecast that starts on day S, the history windows are:
    ///   window 1 (most recent) = the PeriodDays before S,  window 2 = the PeriodDays before that,  and so on.
    /// Weighted Moving Average uses windows 1-3 with the configured weights.
    /// Simple Exponential Smoothing uses up to 8 windows.
    /// </summary>
    public sealed class ForecastService : IForecastService
    {
        private const int SmoothingWindows = 8;
        private const int MinWindows = 3;

        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;
        private readonly IAuthService _auth;

        public ForecastService(IDbContextFactory<AppDbContext> factory, IAuditService audit, IAuthService auth)
        {
            _factory = factory;
            _audit = audit;
            _auth = auth;
        }

        // The parameters saved with each forecast, so every number can be reproduced later.
        internal sealed class ForecastParams
        {
            public int PeriodDays { get; set; }
            public List<decimal> History { get; set; } = new();   // most recent period first
            public List<decimal>? Weights { get; set; }
            public decimal? Alpha { get; set; }
            public bool Backtest { get; set; }
        }

        // ------------------------------------------------------------------ settings
        public async Task<ForecastSettingsInput> GetSettingsAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            return await LoadSettingsAsync(db);
        }

        internal static async Task<ForecastSettingsInput> LoadSettingsAsync(AppDbContext db) => new()
        {
            WeightRecent = await SettingsReader.GetDecimalAsync(db, SettingKeys.ForecastWeightRecent, 0.5m),
            WeightPrevious = await SettingsReader.GetDecimalAsync(db, SettingKeys.ForecastWeightPrevious, 0.3m),
            WeightOlder = await SettingsReader.GetDecimalAsync(db, SettingKeys.ForecastWeightOlder, 0.2m),
            PeriodDays = (int)await SettingsReader.GetDecimalAsync(db, SettingKeys.ForecastPeriodDays, 7m),
            Alpha = await SettingsReader.GetDecimalAsync(db, SettingKeys.ForecastAlpha, 0.3m),
        };

        public async Task<ServiceResult> SaveSettingsAsync(ForecastSettingsInput input, int userId)
        {
            if (!_auth.Can(Permissions.SettingsManage))
                return ServiceResult.Fail("Only the administrator can change forecast settings.");

            if (input.WeightRecent < 0 || input.WeightPrevious < 0 || input.WeightOlder < 0)
                return ServiceResult.Fail("Forecast weights cannot be negative.");
            if (!ForecastCalculator.WeightsAreValid(input.WeightRecent, input.WeightPrevious, input.WeightOlder))
                return ServiceResult.Fail($"Forecast weights must total 1.00 (currently {input.WeightTotal:0.00}).");
            if (input.PeriodDays < 1 || input.PeriodDays > 60)
                return ServiceResult.Fail("Period length must be between 1 and 60 days.");
            if (input.Alpha <= 0 || input.Alpha > 1)
                return ServiceResult.Fail("The smoothing factor must be greater than 0 and at most 1.");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                await SetAsync(db, SettingKeys.ForecastWeightRecent, input.WeightRecent.ToString("0.00", CultureInfo.InvariantCulture));
                await SetAsync(db, SettingKeys.ForecastWeightPrevious, input.WeightPrevious.ToString("0.00", CultureInfo.InvariantCulture));
                await SetAsync(db, SettingKeys.ForecastWeightOlder, input.WeightOlder.ToString("0.00", CultureInfo.InvariantCulture));
                await SetAsync(db, SettingKeys.ForecastPeriodDays, input.PeriodDays.ToString(CultureInfo.InvariantCulture));
                await SetAsync(db, SettingKeys.ForecastAlpha, input.Alpha.ToString("0.00", CultureInfo.InvariantCulture));
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "System Setting Change", "SystemSetting", null,
                    $"Forecast settings: weights {input.WeightRecent:0.00}/{input.WeightPrevious:0.00}/{input.WeightOlder:0.00}, period {input.PeriodDays} days, alpha {input.Alpha:0.00}.");

                return ServiceResult.Ok(message: "Forecast settings saved.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Saving forecast settings failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }

        private static async Task SetAsync(AppDbContext db, string key, string value)
        {
            var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == key);
            if (setting is null)
            {
                db.SystemSettings.Add(new SystemSetting { SettingKey = key, SettingValue = value, SettingGroup = "Forecast" });
            }
            else
            {
                setting.SettingValue = value;
                setting.UpdatedAt = DateTime.UtcNow;
            }
        }

        // ------------------------------------------------------------------ reading
        public async Task<List<ForecastRow>> GetAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var forecasts = await db.Forecasts.AsNoTracking()
                .Include(f => f.Item).ThenInclude(i => i.Category)
                .Where(f => f.Item.Status == RecordStatus.Active)
                .ToListAsync();

            return forecasts
                .OrderByDescending(f => f.PeriodStart)
                .ThenBy(f => f.Item.ItemName)
                .Select(f =>
                {
                    var p = ReadParams(f.ForecastParameters);
                    return new ForecastRow(f.ForecastId, f.ItemId, f.Item.ItemName, f.Item.CategoryId,
                        f.Item.Category.CategoryName, f.Item.UnitOfMeasure, f.ForecastPeriod, f.PeriodStart,
                        f.PeriodEnd, f.ForecastMethod, f.ForecastValue, f.ActualValue, f.ForecastError,
                        f.PercentageError, p.History, p.Backtest, f.GeneratedAt);
                })
                .ToList();
        }

        private static ForecastParams ReadParams(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new ForecastParams();
            try { return JsonSerializer.Deserialize<ForecastParams>(json) ?? new ForecastParams(); }
            catch (JsonException) { return new ForecastParams(); }
        }

        // ------------------------------------------------------------------ core calculation
        /// <summary>Demand of the n periods that finished just before periodStart (most recent first).</summary>
        internal static List<decimal> History(ItemHistory h, DateTime periodStart, int periodDays, int count)
        {
            var list = new List<decimal>();
            for (var j = 0; j < count; j++)
            {
                var end = periodStart.AddDays(-(j * periodDays) - 1);
                var start = end.AddDays(-(periodDays - 1));
                list.Add(h.UsageBetween(start, end));
            }
            return list;
        }

        /// <summary>How many full periods of data exist before periodStart (0 if there is no usage history at all).</summary>
        internal static int AvailableWindows(HistorySet set, DateTime periodStart, int periodDays) =>
            set.EarliestUsage is DateTime earliest ? Math.Max(0, (periodStart - earliest).Days / periodDays) : 0;

        private static (decimal Value, ForecastParams Parameters) Calculate(ItemHistory h, DateTime periodStart,
            ForecastMethod method, ForecastSettingsInput s, int available, bool backtest)
        {
            if (method == ForecastMethod.WeightedMovingAverage)
            {
                var history = History(h, periodStart, s.PeriodDays, 3);
                var weights = new List<decimal> { s.WeightRecent, s.WeightPrevious, s.WeightOlder };
                var value = ForecastCalculator.WeightedMovingAverage(history, weights);
                return (value, new ForecastParams { PeriodDays = s.PeriodDays, History = history, Weights = weights, Backtest = backtest });
            }
            else
            {
                var count = Math.Min(SmoothingWindows, available);
                var history = History(h, periodStart, s.PeriodDays, count);          // most recent first
                var oldestFirst = Enumerable.Reverse(history).ToList();
                var value = ForecastCalculator.SimpleExponentialSmoothing(oldestFirst, s.Alpha);
                return (value, new ForecastParams { PeriodDays = s.PeriodDays, History = history, Alpha = s.Alpha, Backtest = backtest });
            }
        }

        private static string Label(DateTime start, DateTime end) => $"{start:yyyy-MM-dd} to {end:yyyy-MM-dd}";

        private static string? ValidateSettings(ForecastSettingsInput s, ForecastMethod method)
        {
            if (method == ForecastMethod.WeightedMovingAverage &&
                !ForecastCalculator.WeightsAreValid(s.WeightRecent, s.WeightPrevious, s.WeightOlder))
                return $"The forecast weights must total 1.00 (currently {s.WeightTotal:0.00}). Ask the administrator to correct them.";
            if (s.PeriodDays < 1) return "The forecast period length is invalid.";
            return null;
        }

        // ------------------------------------------------------------------ generate next period
        public async Task<ServiceResult> GenerateAsync(ForecastMethod method, int userId)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var settings = await LoadSettingsAsync(db);
                var problem = ValidateSettings(settings, method);
                if (problem is not null) return ServiceResult.Fail(problem);

                var set = await HistoryLoader.LoadAsync(db);
                if (set.Items.Count == 0) return ServiceResult.Fail("There are no active items to forecast.");

                var today = DateTime.Today;
                var available = AvailableWindows(set, today, settings.PeriodDays);
                if (available < MinWindows)
                    return ServiceResult.Fail($"At least {MinWindows} full periods ({MinWindows * settings.PeriodDays} days) of usage history are needed. Record more stock-out transactions first.");

                var start = today;
                var end = today.AddDays(settings.PeriodDays - 1);
                var count = await UpsertForecastsAsync(db, set, method, settings, start, end, available, backtest: false);

                var evaluated = await EvaluateActualsAsync(db, set);
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "Forecast Generation", "Forecast", null,
                    $"{AnalyticsUi.Method(method)} forecast for {count} items, period {Label(start, end)}.");

                return ServiceResult.Ok(message:
                    $"Forecast generated for {count} items for {Label(start, end)} using {AnalyticsUi.Method(method)}." +
                    (evaluated > 0 ? $" {evaluated} earlier forecast(s) were compared with actual usage." : string.Empty));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Generating forecasts failed.", ex);
                return ServiceResult.Fail("Unable to generate the forecast. Please try again.");
            }
        }

        // ------------------------------------------------------------------ back-test
        public async Task<ServiceResult> BacktestAsync(ForecastMethod method, int periods, int userId)
        {
            if (periods < 1 || periods > 12) return ServiceResult.Fail("Choose between 1 and 12 periods to back-test.");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var settings = await LoadSettingsAsync(db);
                var problem = ValidateSettings(settings, method);
                if (problem is not null) return ServiceResult.Fail(problem);

                var set = await HistoryLoader.LoadAsync(db);
                if (set.Items.Count == 0) return ServiceResult.Fail("There are no active items to forecast.");

                var today = DateTime.Today;
                var made = 0;
                var skippedPeriods = 0;

                for (var k = periods; k >= 1; k--)
                {
                    var start = today.AddDays(-(k * settings.PeriodDays));
                    var end = start.AddDays(settings.PeriodDays - 1);

                    // Only forecast periods that had enough history BEFORE them (no peeking at the future).
                    var available = AvailableWindows(set, start, settings.PeriodDays);
                    if (available < MinWindows) { skippedPeriods++; continue; }

                    made += await UpsertForecastsAsync(db, set, method, settings, start, end, available, backtest: true);
                }

                if (made == 0)
                    return ServiceResult.Fail("There is not enough usage history to back-test yet. Record more usage and try again.");

                var evaluated = await EvaluateActualsAsync(db, set);
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "Forecast Generation", "Forecast", null,
                    $"Back-test with {AnalyticsUi.Method(method)}: {made} forecasts over {periods - skippedPeriods} past periods.");

                return ServiceResult.Ok(message:
                    $"Back-test finished: {made} past forecasts were created and {evaluated} compared with actual usage." +
                    (skippedPeriods > 0 ? $" {skippedPeriods} older period(s) were skipped because there was not enough earlier history." : string.Empty));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Forecast back-test failed.", ex);
                return ServiceResult.Fail("Unable to run the back-test. Please try again.");
            }
        }

        /// <summary>Creates or refreshes one forecast per item for the period. Returns how many were written.</summary>
        private static async Task<int> UpsertForecastsAsync(AppDbContext db, HistorySet set, ForecastMethod method,
            ForecastSettingsInput settings, DateTime start, DateTime end, int available, bool backtest)
        {
            var existing = (await db.Forecasts
                    .Where(f => f.PeriodStart == start && f.ForecastMethod == method)
                    .ToListAsync())
                .ToDictionary(f => f.ItemId);

            var now = DateTime.UtcNow;
            var written = 0;
            foreach (var h in set.Items)
            {
                var (value, parameters) = Calculate(h, start, method, settings, available, backtest);
                value = Math.Round(value, 2);
                var json = JsonSerializer.Serialize(parameters);

                if (existing.TryGetValue(h.ItemId, out var f))
                {
                    f.ForecastValue = value;
                    f.ForecastParameters = json;
                    f.PeriodEnd = end;
                    f.ForecastPeriod = Label(start, end);
                    f.ActualValue = null;
                    f.ForecastError = null;
                    f.PercentageError = null;
                    f.GeneratedAt = now;
                }
                else
                {
                    db.Forecasts.Add(new Forecast
                    {
                        ItemId = h.ItemId,
                        ForecastPeriod = Label(start, end),
                        PeriodStart = start,
                        PeriodEnd = end,
                        ForecastValue = value,
                        ForecastMethod = method,
                        ForecastParameters = json,
                        GeneratedAt = now
                    });
                }
                written++;
            }
            return written;
        }

        /// <summary>For every forecast whose period has finished, store the actual usage and the errors.</summary>
        private static async Task<int> EvaluateActualsAsync(AppDbContext db, HistorySet set)
        {
            var byItem = set.Items.ToDictionary(h => h.ItemId);
            var today = DateTime.Today;

            var open = await db.Forecasts.Where(f => f.ActualValue == null && f.PeriodEnd < today).ToListAsync();
            var count = 0;
            foreach (var f in open)
            {
                if (!byItem.TryGetValue(f.ItemId, out var h)) continue;

                var actual = Math.Round(h.UsageBetween(f.PeriodStart, f.PeriodEnd), 2);
                var (absolute, percentage) = ForecastCalculator.Error(f.ForecastValue, actual);

                f.ActualValue = actual;
                f.ForecastError = Math.Round(absolute, 2);
                f.PercentageError = percentage.HasValue ? Math.Round(percentage.Value, 1) : (decimal?)null;
                count++;
            }
            return count;
        }
    }
}
