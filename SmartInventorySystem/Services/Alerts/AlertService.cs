using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services.Analytics;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Core;

namespace SmartInventorySystem.Services.Alerts
{
    public sealed record AlertRow(int Id, int? ItemId, string? ItemName, AlertType Type, AlertSeverity Severity,
        string Message, DateTime CreatedAt, bool IsAcknowledged, DateTime? AcknowledgedAt, string? AcknowledgedBy);

    public interface IAlertService
    {
        /// <summary>Re-checks the inventory, creates new alerts, updates existing ones and closes alerts whose problem is gone. Returns the open count.</summary>
        Task<int> RefreshAsync();
        Task<int> OpenCountAsync();
        Task<List<AlertRow>> ListAsync();
        Task<ServiceResult> AcknowledgeAsync(int alertId, int userId);
        Task<ServiceResult> AcknowledgeAllAsync(int userId);
    }

    /// <summary>
    /// Alert rules (all use real data, nothing is invented):
    ///   Low Stock         : stock at or below the reorder point (Critical when out of stock or at/below
    ///                       the "critical % of safety stock" setting, otherwise Warning)
    ///   Urgent Reorder    : a pending URGENT reorder recommendation exists
    ///   Stockout Risk     : days of stock left (last 30 days of usage) is below lead time (Critical)
    ///                       or below lead time + safety days (Warning)
    ///   Overstock         : stock above reorder point x overstock multiplier (Info)
    ///   Slow-Moving Item  : latest movement classification is Slow-Moving (Info)
    ///   Obsolescence Risk : latest ageing risk is High (Warning)
    ///   Forecast Warning  : the latest finished forecast was off by more than the warning % (Warning)
    /// </summary>
    public sealed class AlertService : IAlertService
    {
        private const int UsageDays = 30;

        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;

        public AlertService(IDbContextFactory<AppDbContext> factory, IAuditService audit)
        {
            _factory = factory;
            _audit = audit;
        }

        private sealed record Candidate(int ItemId, AlertType Type, AlertSeverity Severity, string Message);

        public async Task<int> RefreshAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();

            var overstockMultiplier = await SettingsReader.GetDecimalAsync(db, SettingKeys.ReorderOverstockMultiplier, 3m);
            var criticalPercent = await SettingsReader.GetDecimalAsync(db, SettingKeys.AlertCriticalStockPercent, 50m);
            var forecastWarnPercent = await SettingsReader.GetDecimalAsync(db, SettingKeys.AlertForecastErrorPercent, 30m);
            var defaultLead = (int)await SettingsReader.GetDecimalAsync(db, SettingKeys.ReorderDefaultLeadTimeDays, 7m);
            var safetyDays = await SettingsReader.GetDecimalAsync(db, SettingKeys.ReorderSafetyStockDays, 3m);

            var set = await HistoryLoader.LoadAsync(db);
            var suppliers = await db.Items.AsNoTracking().Include(i => i.Supplier)
                .Where(i => i.Status == RecordStatus.Active).ToListAsync();
            var leadByItem = suppliers.ToDictionary(i => i.ItemId, i => i.Supplier?.LeadTimeDays ?? defaultLead);

            var candidates = new List<Candidate>();
            var today = DateTime.Today;

            foreach (var h in set.Items)
            {
                var qty = h.CurrentQuantity;
                var status = StockStatusCalculator.Calculate(qty, h.SafetyStock, h.ReorderPoint, overstockMultiplier);

                if (status == StockStatus.OutOfStock)
                {
                    candidates.Add(new(h.ItemId, AlertType.LowStock, AlertSeverity.Critical, $"{h.Name} is out of stock."));
                }
                else if (status is StockStatus.Critical or StockStatus.LowStock)
                {
                    var severe = qty <= h.SafetyStock * criticalPercent / 100m;
                    candidates.Add(new(h.ItemId, AlertType.LowStock, severe ? AlertSeverity.Critical : AlertSeverity.Warning,
                        $"{h.Name} is low: {Fmt.Qty(qty)} {h.Unit} left (reorder point {Fmt.Qty(h.ReorderPoint)}, safety stock {Fmt.Qty(h.SafetyStock)})."));
                }
                else if (status == StockStatus.Overstock)
                {
                    candidates.Add(new(h.ItemId, AlertType.Overstock, AlertSeverity.Info,
                        $"{h.Name} may be overstocked: {Fmt.Qty(qty)} {h.Unit} on hand (reorder point {Fmt.Qty(h.ReorderPoint)})."));
                }

                // Stockout risk from the last 30 days of usage.
                var used = h.UsageBetween(today.AddDays(-(UsageDays - 1)), today);
                if (used > 0 && qty > 0)
                {
                    var avgDaily = used / UsageDays;
                    var cover = qty / avgDaily;
                    var lead = leadByItem.TryGetValue(h.ItemId, out var l) ? l : defaultLead;

                    if (cover < lead)
                        candidates.Add(new(h.ItemId, AlertType.StockoutRisk, AlertSeverity.Critical,
                            $"{h.Name} may run out in about {Fmt.Qty(Math.Round(cover, 1))} day(s), before a delivery (lead time {lead} days) could arrive."));
                    else if (cover < lead + safetyDays)
                        candidates.Add(new(h.ItemId, AlertType.StockoutRisk, AlertSeverity.Warning,
                            $"{h.Name} has about {Fmt.Qty(Math.Round(cover, 1))} day(s) of stock left, close to its lead time of {lead} days."));
                }
            }

            var activeIds = set.Items.Select(i => i.ItemId).ToHashSet();
            var nameById = set.Items.ToDictionary(i => i.ItemId, i => i.Name);
            var unitById = set.Items.ToDictionary(i => i.ItemId, i => i.Unit);

            // Urgent reorder recommendations waiting for the owner.
            var urgent = await db.ReorderRecommendations.AsNoTracking()
                .Where(r => r.Status == RecommendationStatus.Pending && r.Urgency == UrgencyLevel.Urgent)
                .ToListAsync();
            foreach (var r in urgent.Where(r => activeIds.Contains(r.ItemId)))
            {
                candidates.Add(new(r.ItemId, AlertType.UrgentReorder, AlertSeverity.Critical,
                    $"Urgent reorder recommended for {nameById[r.ItemId]}: {Fmt.Qty(r.RecommendedQuantity)} {unitById[r.ItemId]}. Waiting for the owner's decision."));
            }

            // Latest movement classification per item: slow-moving and ageing risk.
            var classifications = (await db.MovementClassifications.AsNoTracking().ToListAsync())
                .Where(c => activeIds.Contains(c.ItemId))
                .GroupBy(c => c.ItemId)
                .Select(g => g.OrderByDescending(c => c.CalculatedAt).First());
            foreach (var c in classifications)
            {
                if (c.Classification == MovementClass.SlowMoving)
                    candidates.Add(new(c.ItemId, AlertType.SlowMovingItem, AlertSeverity.Info,
                        $"{nameById[c.ItemId]} is slow-moving (turnover {c.TurnoverRate:0.##} in {c.EvaluationPeriod})."));

                if (c.ObsolescenceRisk == RiskLevel.High)
                    candidates.Add(new(c.ItemId, AlertType.ObsolescenceRisk, AlertSeverity.Warning,
                        $"{nameById[c.ItemId]} has a high ageing risk" +
                        (c.DaysSinceLastUse is int d ? $": last used {d} day(s) ago." : ": it has never been used.")));
            }

            // Latest finished forecast per item that was far from the real usage.
            var finished = (await db.Forecasts.AsNoTracking().Where(f => f.ActualValue != null).ToListAsync())
                .Where(f => activeIds.Contains(f.ItemId))
                .GroupBy(f => f.ItemId)
                .Select(g => g.OrderByDescending(f => f.PeriodEnd).First());
            foreach (var f in finished)
            {
                if (f.PercentageError is decimal pct && pct > forecastWarnPercent)
                    candidates.Add(new(f.ItemId, AlertType.ForecastWarning, AlertSeverity.Warning,
                        $"The forecast for {nameById[f.ItemId]} was off by {pct:0.#}% for {f.ForecastPeriod}. Review the demand assumptions."));
            }

            // ---------- sync with the Alerts table ----------
            var wanted = candidates.GroupBy(c => (c.ItemId, c.Type)).ToDictionary(g => g.Key, g => g.First());
            var open = await db.Alerts.Where(a => !a.IsAcknowledged).ToListAsync();
            var now = DateTime.UtcNow;
            var seen = new HashSet<(int, AlertType)>();

            foreach (var a in open)
            {
                var key = (a.ItemId ?? 0, a.AlertType);
                if (wanted.TryGetValue(key, out var c) && seen.Add(key))
                {
                    a.Message = c.Message;      // keep it current
                    a.Severity = c.Severity;
                }
                else
                {
                    a.IsAcknowledged = true;    // the problem is gone (or a duplicate): resolved by the system
                    a.AcknowledgedAt = now;
                    a.AcknowledgedByUserId = null;
                }
            }

            foreach (var kv in wanted.Where(entry => !seen.Contains(entry.Key)))
            {
                db.Alerts.Add(new Alert
                {
                    ItemId = kv.Value.ItemId,
                    AlertType = kv.Value.Type,
                    Severity = kv.Value.Severity,
                    Message = kv.Value.Message.Length > 500 ? kv.Value.Message.Substring(0, 497) + "..." : kv.Value.Message,
                    CreatedAt = now
                });
            }

            await db.SaveChangesAsync();
            return await db.Alerts.CountAsync(a => !a.IsAcknowledged);
        }

        public async Task<int> OpenCountAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            return await db.Alerts.CountAsync(a => !a.IsAcknowledged);
        }

        public async Task<List<AlertRow>> ListAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var alerts = await db.Alerts.AsNoTracking()
                .Include(a => a.Item)
                .Include(a => a.AcknowledgedBy)
                .OrderByDescending(a => a.CreatedAt)
                .Take(500)
                .ToListAsync();

            return alerts
                .OrderBy(a => a.IsAcknowledged ? 1 : 0)
                .ThenByDescending(a => (int)a.Severity)
                .ThenByDescending(a => a.CreatedAt)
                .Select(a => new AlertRow(a.AlertId, a.ItemId, a.Item?.ItemName, a.AlertType, a.Severity, a.Message,
                    a.CreatedAt, a.IsAcknowledged, a.AcknowledgedAt, a.AcknowledgedBy?.FullName))
                .ToList();
        }

        public async Task<ServiceResult> AcknowledgeAsync(int alertId, int userId)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var alert = await db.Alerts.FirstOrDefaultAsync(a => a.AlertId == alertId);
                if (alert is null) return ServiceResult.Fail("The alert no longer exists.");
                if (alert.IsAcknowledged) return ServiceResult.Ok(alertId, "The alert was already closed.");

                alert.IsAcknowledged = true;
                alert.AcknowledgedAt = DateTime.UtcNow;
                alert.AcknowledgedByUserId = userId;
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "Acknowledge Alert", "Alert", alertId.ToString(), alert.Message);
                return ServiceResult.Ok(alertId, "Alert acknowledged.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Acknowledging the alert failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }

        public async Task<ServiceResult> AcknowledgeAllAsync(int userId)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var open = await db.Alerts.Where(a => !a.IsAcknowledged).ToListAsync();
                var now = DateTime.UtcNow;
                foreach (var a in open)
                {
                    a.IsAcknowledged = true;
                    a.AcknowledgedAt = now;
                    a.AcknowledgedByUserId = userId;
                }
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "Acknowledge Alert", "Alert", null, $"Acknowledged all {open.Count} open alerts.");
                return ServiceResult.Ok(message: $"{open.Count} alert(s) acknowledged.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Acknowledging all alerts failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }
    }
}
