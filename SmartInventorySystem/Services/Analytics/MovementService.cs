using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Core;

namespace SmartInventorySystem.Services.Analytics
{
    public interface IMovementService
    {
        Task<MovementSettingsInfo> GetSettingsAsync();
        Task<MovementSnapshot> GetLatestAsync();
        Task<ServiceResult> RunAsync(DateTime from, DateTime to, int userId);
    }

    public sealed class MovementService : IMovementService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;

        public MovementService(IDbContextFactory<AppDbContext> factory, IAuditService audit)
        {
            _factory = factory;
            _audit = audit;
        }

        public async Task<MovementSettingsInfo> GetSettingsAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            return await LoadSettingsAsync(db);
        }

        private static async Task<MovementSettingsInfo> LoadSettingsAsync(AppDbContext db) => new(
            (int)await SettingsReader.GetDecimalAsync(db, SettingKeys.MovementEvaluationDays, 90m),
            await SettingsReader.GetDecimalAsync(db, SettingKeys.MovementFastMinTurnover, 2m),
            await SettingsReader.GetDecimalAsync(db, SettingKeys.MovementSlowMaxTurnover, 0.5m),
            (int)await SettingsReader.GetDecimalAsync(db, SettingKeys.AlertObsolescenceNoUseDays, 60m));

        public async Task<MovementSnapshot> GetLatestAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();

            var items = await db.Items.AsNoTracking()
                .Include(i => i.Category)
                .Include(i => i.Inventory)
                .Where(i => i.Status == RecordStatus.Active)
                .ToListAsync();

            var all = await db.MovementClassifications.AsNoTracking().ToListAsync();
            var latest = all
                .GroupBy(c => c.ItemId)
                .Select(g => g.OrderByDescending(c => c.CalculatedAt).First())
                .ToDictionary(c => c.ItemId);

            var rows = items
                .Where(i => latest.ContainsKey(i.ItemId))
                .Select(i =>
                {
                    var c = latest[i.ItemId];
                    return new MovementRow(i.ItemId, i.ItemName, i.SKU, i.CategoryId, i.Category.CategoryName,
                        i.UnitOfMeasure, c.EvaluationPeriod, c.TotalConsumption, c.ConsumptionFrequency,
                        c.AverageDailyUsage, c.TurnoverRate, c.Classification, c.ObsolescenceRisk,
                        c.DaysSinceLastUse, i.Inventory?.QuantityOnHand ?? 0m, c.CalculatedAt);
                })
                .OrderByDescending(r => r.TurnoverRate)
                .ThenBy(r => r.ItemName)
                .ToList();

            var newest = rows.OrderByDescending(r => r.CalculatedAt).FirstOrDefault();
            return new MovementSnapshot(rows, newest?.Period, newest?.CalculatedAt);
        }

        public async Task<ServiceResult> RunAsync(DateTime from, DateTime to, int userId)
        {
            var today = DateTime.Today;
            from = from.Date;
            to = to.Date;
            if (to > today) to = today;
            if (from > to) return ServiceResult.Fail("The start date must be on or before the end date.");

            var days = (to - from).Days + 1;
            if (days < 7) return ServiceResult.Fail("Please choose a range of at least 7 days.");
            if (days > 730) return ServiceResult.Fail("Please choose a range of 2 years or less.");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var settings = await LoadSettingsAsync(db);

                // Thresholds are defined for the standard evaluation window (default 90 days).
                // For a shorter or longer range they are scaled so results stay comparable.
                var scale = settings.EvaluationDays > 0 ? days / (decimal)settings.EvaluationDays : 1m;
                var fastMin = settings.FastMinTurnover * scale;
                var slowMax = settings.SlowMaxTurnover * scale;

                var set = await HistoryLoader.LoadAsync(db);
                if (set.Items.Count == 0) return ServiceResult.Fail("There are no active items to analyze.");

                var label = $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}";

                // Re-running the same range replaces its earlier result instead of piling up duplicates.
                var old = await db.MovementClassifications.Where(c => c.EvaluationPeriod == label).ToListAsync();
                db.MovementClassifications.RemoveRange(old);

                var now = DateTime.UtcNow;
                foreach (var h in set.Items)
                {
                    var m = MovementCalculator.Measure(h, from, to);
                    var classification = MovementCalculator.Classify(m.TurnoverRate, fastMin, slowMax);

                    int? sinceLast = h.LastUsed is DateTime last ? (today - last).Days : (int?)null;
                    var risk = MovementCalculator.ObsolescenceRisk(sinceLast, settings.ObsolescenceNoUseDays,
                        h.CurrentQuantity, m.AverageDailyUsage, m.FirstHalfUsage, m.SecondHalfUsage, classification);

                    db.MovementClassifications.Add(new MovementClassification
                    {
                        ItemId = h.ItemId,
                        EvaluationPeriod = label,
                        TotalConsumption = Math.Round(m.TotalConsumption, 2),
                        ConsumptionFrequency = m.Frequency,
                        TurnoverRate = Math.Round(m.TurnoverRate, 2),
                        AverageDailyUsage = Math.Round(m.AverageDailyUsage, 2),
                        Classification = classification,
                        ObsolescenceRisk = risk,
                        DaysSinceLastUse = sinceLast,
                        CalculatedAt = now
                    });
                }

                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "Movement Classification", "MovementClassification", null,
                    $"Classified {set.Items.Count} items for {label}.");

                return ServiceResult.Ok(message: $"Movement analysis finished for {set.Items.Count} items ({label}).");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Movement analysis failed.", ex);
                return ServiceResult.Fail("Unable to complete the analysis. Please try again.");
            }
        }
    }
}
