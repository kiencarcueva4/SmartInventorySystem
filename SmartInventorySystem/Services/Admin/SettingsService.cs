using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services.Analytics;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Core;

namespace SmartInventorySystem.Services.Admin
{
    public sealed record SettingDefinition(string Key, string Label, string Group, string Help, decimal Min, decimal Max, bool IsInteger);

    public sealed record SettingEntry(SettingDefinition Definition, string Value);

    public interface ISettingsService
    {
        Task<List<SettingEntry>> GetAsync();
        Task<ServiceResult> SaveAsync(IReadOnlyDictionary<string, string> values, int userId);
    }

    public sealed class SettingsService : ISettingsService
    {
        // Every setting the system actually reads. Nothing here is decoration.
        public static readonly IReadOnlyList<SettingDefinition> Definitions = new[]
        {
            new SettingDefinition(SettingKeys.MovementEvaluationDays, "Evaluation period (days)", "Movement classification",
                "Standard length of history used to classify items. Other ranges are scaled to this.", 7, 730, true),
            new SettingDefinition(SettingKeys.MovementFastMinTurnover, "Fast-moving: turnover at or above", "Movement classification",
                "Turnover = quantity used / average stock during the period.", 0.1m, 1000, false),
            new SettingDefinition(SettingKeys.MovementSlowMaxTurnover, "Slow-moving: turnover at or below", "Movement classification",
                "Items between the two limits are moderate-moving. Must be lower than the fast limit.", 0, 1000, false),

            new SettingDefinition(SettingKeys.ForecastWeightRecent, "Weight - most recent period", "Demand forecasting",
                "The three weights must total 1.00.", 0, 1, false),
            new SettingDefinition(SettingKeys.ForecastWeightPrevious, "Weight - previous period", "Demand forecasting", "", 0, 1, false),
            new SettingDefinition(SettingKeys.ForecastWeightOlder, "Weight - older period", "Demand forecasting", "", 0, 1, false),
            new SettingDefinition(SettingKeys.ForecastPeriodDays, "Forecast period length (days)", "Demand forecasting",
                "One forecast covers this many days.", 1, 60, true),
            new SettingDefinition(SettingKeys.ForecastAlpha, "Smoothing factor (exponential smoothing)", "Demand forecasting",
                "Between 0.01 and 1. Higher reacts faster to recent changes.", 0.01m, 1, false),

            new SettingDefinition(SettingKeys.ReorderDefaultLeadTimeDays, "Default lead time (days)", "Reordering",
                "Used when an item has no supplier.", 0, 365, true),
            new SettingDefinition(SettingKeys.ReorderSafetyStockDays, "Safety days for stockout-risk checks", "Reordering",
                "An item is flagged medium risk when its days of stock left are below lead time + this many days.", 0, 90, true),
            new SettingDefinition(SettingKeys.ReorderOverstockMultiplier, "Overstock multiplier", "Reordering",
                "An item is overstocked when stock exceeds reorder point x this value.", 1, 20, false),

            new SettingDefinition(SettingKeys.AlertCriticalStockPercent, "Critical low-stock level (% of safety stock)", "Alerts",
                "Low-stock alerts become Critical at or below this share of safety stock (or when out of stock).", 0, 100, false),
            new SettingDefinition(SettingKeys.AlertObsolescenceNoUseDays, "Days without use before ageing risk rises", "Alerts",
                "Used by the obsolescence / ageing risk rating.", 7, 730, true),
            new SettingDefinition(SettingKeys.AlertForecastErrorPercent, "Forecast error warning (%)", "Alerts",
                "Raise a Forecast Warning when a finished forecast was off by more than this.", 1, 1000, false),
        };

        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;
        private readonly IAuthService _auth;

        public SettingsService(IDbContextFactory<AppDbContext> factory, IAuditService audit, IAuthService auth)
        {
            _factory = factory;
            _audit = audit;
            _auth = auth;
        }

        public async Task<List<SettingEntry>> GetAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var stored = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(s => s.SettingKey, s => s.SettingValue);
            return Definitions.Select(d => new SettingEntry(d, stored.TryGetValue(d.Key, out var v) ? v : string.Empty)).ToList();
        }

        private static bool TryParse(string? text, out decimal value) =>
            decimal.TryParse((text ?? string.Empty).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            || decimal.TryParse((text ?? string.Empty).Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value);

        public async Task<ServiceResult> SaveAsync(IReadOnlyDictionary<string, string> values, int userId)
        {
            if (!_auth.Can(Permissions.SettingsManage))
                return ServiceResult.Fail("Only the administrator can change system settings.");

            // ---- validate everything first; nothing is saved if anything is wrong ----
            var parsed = new Dictionary<string, decimal>();
            foreach (var d in Definitions)
            {
                if (!values.TryGetValue(d.Key, out var text)) continue;

                if (!TryParse(text, out var number))
                    return ServiceResult.Fail($"'{d.Label}' must be a number.");
                if (d.IsInteger && number != Math.Floor(number))
                    return ServiceResult.Fail($"'{d.Label}' must be a whole number.");
                if (number < d.Min || number > d.Max)
                    return ServiceResult.Fail($"'{d.Label}' must be between {d.Min.ToString("0.##", CultureInfo.InvariantCulture)} and {d.Max.ToString("0.##", CultureInfo.InvariantCulture)}.");

                parsed[d.Key] = number;
            }

            decimal Get(string key, decimal fallback) => parsed.TryGetValue(key, out var v) ? v : fallback;

            var w1 = Get(SettingKeys.ForecastWeightRecent, 0.5m);
            var w2 = Get(SettingKeys.ForecastWeightPrevious, 0.3m);
            var w3 = Get(SettingKeys.ForecastWeightOlder, 0.2m);
            if (!ForecastCalculator.WeightsAreValid(w1, w2, w3))
                return ServiceResult.Fail($"The forecast weights must total 1.00 (currently {(w1 + w2 + w3):0.00}).");

            if (Get(SettingKeys.MovementSlowMaxTurnover, 0.5m) >= Get(SettingKeys.MovementFastMinTurnover, 2m))
                return ServiceResult.Fail("The slow-moving limit must be lower than the fast-moving limit.");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var existing = await db.SystemSettings.ToDictionaryAsync(s => s.SettingKey);
                var changes = new List<string>();

                foreach (var d in Definitions.Where(d => parsed.ContainsKey(d.Key)))
                {
                    var newText = parsed[d.Key].ToString(d.IsInteger ? "0" : "0.0#", CultureInfo.InvariantCulture);

                    if (existing.TryGetValue(d.Key, out var row))
                    {
                        var same = TryParse(row.SettingValue, out var oldNumber) && oldNumber == parsed[d.Key];
                        if (same) continue;

                        changes.Add($"{d.Label}: {row.SettingValue} -> {newText}");
                        row.SettingValue = newText;
                        row.UpdatedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        changes.Add($"{d.Label}: (new) {newText}");
                        db.SystemSettings.Add(new SystemSetting { SettingKey = d.Key, SettingValue = newText, SettingGroup = d.Group, Description = d.Label });
                    }
                }

                if (changes.Count == 0) return ServiceResult.Ok(message: "No changes to save.");

                await db.SaveChangesAsync();

                var description = string.Join("; ", changes);
                await _audit.LogAsync(userId, "System Setting Change", "SystemSetting", null,
                    description.Length > 950 ? description.Substring(0, 947) + "..." : description);

                return ServiceResult.Ok(message: $"{changes.Count} setting(s) saved.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Saving settings failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }
    }
}
