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

namespace SmartInventorySystem.Services.Reordering
{
    public interface IReorderService
    {
        Task<List<RecommendationRow>> ListAsync();
        Task<RecommendationRow?> GetAsync(int recommendationId);
        /// <summary>Checks every active item and creates / refreshes recommendations. Never orders anything.</summary>
        Task<ServiceResult> EvaluateAsync(int userId);
        /// <summary>Owner decision: approve (creates a restock record), defer or dismiss.</summary>
        Task<ServiceResult> DecideAsync(DecisionInput input, int userId);
    }

    public sealed class ReorderService : IReorderService
    {
        private const int AverageUsageDays = 30;
        private const string OwnerNotePrefix = "Owner note";

        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;
        private readonly IAuthService _auth;

        public ReorderService(IDbContextFactory<AppDbContext> factory, IAuditService audit, IAuthService auth)
        {
            _factory = factory;
            _audit = audit;
            _auth = auth;
        }

        // ------------------------------------------------------------------ reading
        private static IQueryable<ReorderRecommendation> Query(AppDbContext db) => db.ReorderRecommendations.AsNoTracking()
            .Include(r => r.Item).ThenInclude(i => i.Category)
            .Include(r => r.Item).ThenInclude(i => i.Supplier)
            .Include(r => r.DecidedBy)
            .Include(r => r.PurchaseRestockRecord);

        private static RecommendationRow Map(ReorderRecommendation r)
        {
            var restock = r.PurchaseRestockRecord;
            return new RecommendationRow(
                r.RecommendationId, r.ItemId, r.Item.ItemName, r.Item.SKU, r.Item.Category.CategoryName,
                r.Item.UnitOfMeasure, r.Item.Supplier?.SupplierName, r.CurrentStock, r.AverageDailyUsage,
                r.ForecastedDemand, r.LeadTimeDemand, r.LeadTimeDays, r.SafetyStock, r.ReorderPoint,
                r.Item.ReorderQuantity, r.RecommendedQuantity, r.Urgency, r.Reason, r.Status, r.OwnerDecision,
                r.DecisionDate, r.DecidedBy?.FullName, r.CreatedAt, restock?.PurchaseRestockId,
                restock?.Status, restock?.ApprovedQuantity, restock?.ExpectedDeliveryDate);
        }

        public async Task<List<RecommendationRow>> ListAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var list = await Query(db).ToListAsync();
            return list
                .OrderByDescending(r => (int)r.Urgency)
                .ThenByDescending(r => r.CreatedAt)
                .Select(Map)
                .ToList();
        }

        public async Task<RecommendationRow?> GetAsync(int recommendationId)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var r = await Query(db).FirstOrDefaultAsync(x => x.RecommendationId == recommendationId);
            return r is null ? null : Map(r);
        }

        // ------------------------------------------------------------------ evaluation
        public async Task<ServiceResult> EvaluateAsync(int userId)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync();

                var fs = await ForecastService.LoadSettingsAsync(db);
                if (!ForecastCalculator.WeightsAreValid(fs.WeightRecent, fs.WeightPrevious, fs.WeightOlder))
                    return ServiceResult.Fail($"The forecast weights must total 1.00 (currently {fs.WeightTotal:0.00}). Ask the administrator to correct them on the Demand Forecasting page.");

                var defaultLeadTime = (int)await SettingsReader.GetDecimalAsync(db, SettingKeys.ReorderDefaultLeadTimeDays, 7m);

                var set = await HistoryLoader.LoadAsync(db);
                if (set.Items.Count == 0) return ServiceResult.Fail("There are no active items to evaluate.");

                var items = await db.Items.AsNoTracking().Include(i => i.Supplier)
                    .Where(i => i.Status == RecordStatus.Active).ToListAsync();
                var itemById = items.ToDictionary(i => i.ItemId);

                var today = DateTime.Today;
                var periodDays = Math.Max(1, fs.PeriodDays);
                var weights = new[] { fs.WeightRecent, fs.WeightPrevious, fs.WeightOlder };
                var available = ForecastService.AvailableWindows(set, today, periodDays);

                // Recommendations that are still open, plus approved ones (to avoid ordering the same item twice).
                var open = await db.ReorderRecommendations
                    .Include(r => r.PurchaseRestockRecord)
                    .Where(r => r.Status == RecommendationStatus.Pending
                             || r.Status == RecommendationStatus.Deferred
                             || r.Status == RecommendationStatus.Approved)
                    .ToListAsync();

                int urgent = 0, recommended = 0, notNeeded = 0, created = 0, updated = 0, closed = 0, onOrder = 0;
                var now = DateTime.UtcNow;

                foreach (var h in set.Items)
                {
                    if (!itemById.TryGetValue(h.ItemId, out var item)) continue;

                    // Demand forecast: Weighted Moving Average of the last 3 periods; falls back to the
                    // recent daily average when there is less than 3 periods of history.
                    var averageDaily = h.UsageBetween(today.AddDays(-(AverageUsageDays - 1)), today) / AverageUsageDays;
                    decimal periodForecast;
                    var limitedHistory = false;
                    if (available >= 3)
                    {
                        var demands = ForecastService.History(h, today, periodDays, 3);
                        periodForecast = ForecastCalculator.WeightedMovingAverage(demands, weights);
                    }
                    else
                    {
                        periodForecast = averageDaily * periodDays;
                        limitedHistory = true;
                    }
                    var dailyForecast = periodForecast / periodDays;

                    var leadTime = item.Supplier?.LeadTimeDays ?? defaultLeadTime;
                    var result = ReorderCalculator.Evaluate(h.CurrentQuantity, h.SafetyStock, h.ReorderPoint,
                        dailyForecast, leadTime, item.ReorderQuantity, h.Unit);

                    var reasonText = result.Reason;
                    if (limitedHistory) reasonText += " (Demand is based on the recent daily average because there are fewer than 3 periods of history.)";
                    if (item.Supplier is null) reasonText += $" (No supplier is assigned, so the default lead time of {defaultLeadTime} days was used.)";

                    var existingOpen = open
                        .Where(r => r.ItemId == h.ItemId
                                 && (r.Status == RecommendationStatus.Pending || r.Status == RecommendationStatus.Deferred))
                        .OrderByDescending(r => r.CreatedAt)
                        .FirstOrDefault();

                    var alreadyOnOrder = open.Any(r => r.ItemId == h.ItemId
                        && r.Status == RecommendationStatus.Approved
                        && (r.PurchaseRestockRecord is null
                            || r.PurchaseRestockRecord.Status == RestockStatus.Planned
                            || r.PurchaseRestockRecord.Status == RestockStatus.Ordered));

                    if (result.Urgency == UrgencyLevel.NotYetNecessary)
                    {
                        notNeeded++;
                        if (existingOpen is not null)
                        {
                            // Stock is sufficient again: close the old suggestion (this is not an owner decision).
                            existingOpen.Status = RecommendationStatus.Dismissed;
                            existingOpen.Reason = Truncate((existingOpen.Reason ?? string.Empty) + " Closed automatically: stock is now sufficient.");
                            closed++;
                        }
                        continue;
                    }

                    if (result.Urgency == UrgencyLevel.Urgent) urgent++; else recommended++;

                    if (alreadyOnOrder) { onOrder++; continue; }

                    if (existingOpen is not null)
                    {
                        var wasUrgent = existingOpen.Urgency == UrgencyLevel.Urgent;
                        Fill(existingOpen, h.CurrentQuantity, averageDaily, periodForecast, result, leadTime, h, reasonText);

                        // A deferred item that has become urgent needs the owner's attention again.
                        if (existingOpen.Status == RecommendationStatus.Deferred && result.Urgency == UrgencyLevel.Urgent && !wasUrgent)
                        {
                            existingOpen.Status = RecommendationStatus.Pending;
                            existingOpen.OwnerDecision = OwnerDecision.Pending;
                            existingOpen.Reason = Truncate(existingOpen.Reason + " Re-opened: the item is now urgent.");
                        }
                        updated++;
                    }
                    else
                    {
                        var rec = new ReorderRecommendation
                        {
                            ItemId = h.ItemId,
                            Status = RecommendationStatus.Pending,
                            OwnerDecision = OwnerDecision.Pending,
                            CreatedAt = now
                        };
                        Fill(rec, h.CurrentQuantity, averageDaily, periodForecast, result, leadTime, h, reasonText);
                        db.ReorderRecommendations.Add(rec);
                        created++;
                    }
                }

                await db.SaveChangesAsync();

                var summary = $"{set.Items.Count} items checked: {urgent} urgent, {recommended} recommended, {notNeeded} not yet necessary.";
                await _audit.LogAsync(userId, "Reorder Recommendation", "ReorderRecommendation", null,
                    $"{summary} New: {created}, updated: {updated}, closed automatically: {closed}, already on order: {onOrder}.");

                return ServiceResult.Ok(message:
                    $"{summary} {created} new and {updated} updated recommendation(s)" +
                    (closed > 0 ? $", {closed} closed because stock is sufficient again" : string.Empty) +
                    (onOrder > 0 ? $", {onOrder} skipped because they are already on order" : string.Empty) + ".");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Reorder evaluation failed.", ex);
                return ServiceResult.Fail("Unable to evaluate the inventory. Please try again.");
            }
        }

        private static void Fill(ReorderRecommendation rec, decimal currentStock, decimal averageDaily, decimal periodForecast,
            ReorderResult result, int leadTime, ItemHistory h, string reasonText)
        {
            rec.CurrentStock = Math.Round(currentStock, 2);
            rec.AverageDailyUsage = Math.Round(averageDaily, 2);
            rec.ForecastedDemand = Math.Round(periodForecast, 2);
            rec.LeadTimeDemand = Math.Round(result.LeadTimeDemand, 2);
            rec.LeadTimeDays = leadTime;
            rec.SafetyStock = h.SafetyStock;
            rec.ReorderPoint = h.ReorderPoint;
            rec.RecommendedQuantity = Math.Round(result.RecommendedQuantity, 2);
            rec.Urgency = result.Urgency;

            // Keep any notes the owner added earlier.
            var notes = (rec.Reason ?? string.Empty).Split('\n')
                .Where(l => l.StartsWith(OwnerNotePrefix, StringComparison.Ordinal)).ToList();
            rec.Reason = Truncate(reasonText + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : string.Empty));
        }

        private static string Truncate(string text) => text.Length <= 1000 ? text : text.Substring(0, 997) + "...";

        // ------------------------------------------------------------------ owner decision
        public async Task<ServiceResult> DecideAsync(DecisionInput input, int userId)
        {
            if (!_auth.Can(Permissions.RecommendationsDecide))
                return ServiceResult.Fail("Only the owner can approve, defer or dismiss recommendations.");

            if (input.Decision is not (OwnerDecision.Approved or OwnerDecision.Deferred or OwnerDecision.Dismissed))
                return ServiceResult.Fail("Please choose approve, defer or dismiss.");

            var note = Text.NullIfEmpty(input.Note);
            if (note is not null && note.Length > 300) return ServiceResult.Fail("The note is too long (maximum 300 characters).");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var rec = await db.ReorderRecommendations
                    .Include(r => r.Item).ThenInclude(i => i.Supplier)
                    .FirstOrDefaultAsync(r => r.RecommendationId == input.RecommendationId);
                if (rec is null) return ServiceResult.Fail("The recommendation no longer exists.");

                if (rec.Status is not (RecommendationStatus.Pending or RecommendationStatus.Deferred))
                    return ServiceResult.Fail($"This recommendation is already {rec.Status.ToString().ToLowerInvariant()} and can no longer be changed here.");

                var now = DateTime.UtcNow;
                var description = $"{rec.Item.ItemName}";

                if (input.Decision == OwnerDecision.Approved)
                {
                    var quantity = Math.Round(input.ApprovedQuantity, 2);
                    if (quantity <= 0) return ServiceResult.Fail("The approved quantity must be greater than zero.");
                    if (rec.Item.Status != RecordStatus.Active) return ServiceResult.Fail("This item is inactive. Activate it before approving a restock.");
                    if (input.ExpectedDelivery.Date < DateTime.Today) return ServiceResult.Fail("The expected delivery date cannot be in the past.");

                    db.PurchaseRestockRecords.Add(new PurchaseRestockRecord
                    {
                        RecommendationId = rec.RecommendationId,
                        ItemId = rec.ItemId,
                        SupplierId = rec.Item.SupplierId,
                        ApprovedQuantity = quantity,
                        ExpectedDeliveryDate = input.ExpectedDelivery.Date,
                        Status = RestockStatus.Planned,
                        Notes = note
                    });

                    rec.Status = RecommendationStatus.Approved;
                    rec.OwnerDecision = OwnerDecision.Approved;
                    description += $": approved {Fmt.Qty(quantity)} {rec.Item.UnitOfMeasure}, expected {input.ExpectedDelivery:yyyy-MM-dd}.";
                }
                else
                {
                    rec.Status = input.Decision == OwnerDecision.Deferred ? RecommendationStatus.Deferred : RecommendationStatus.Dismissed;
                    rec.OwnerDecision = input.Decision;
                    description += $": {input.Decision.ToString().ToLowerInvariant()}.";

                    if (note is not null)
                    {
                        rec.Reason = Truncate((rec.Reason ?? string.Empty) +
                            $"\n{OwnerNotePrefix} ({input.Decision}, {DateTime.Today:yyyy-MM-dd}): {note}");
                    }
                }

                rec.DecisionDate = now;
                rec.DecidedByUserId = userId;
                await db.SaveChangesAsync();

                var action = input.Decision switch
                {
                    OwnerDecision.Approved => "Approve Recommendation",
                    OwnerDecision.Deferred => "Defer Recommendation",
                    _ => "Dismiss Recommendation"
                };
                await _audit.LogAsync(userId, action, "ReorderRecommendation", rec.RecommendationId.ToString(),
                    description + (note is not null ? $" Note: {note}" : string.Empty));

                return ServiceResult.Ok(rec.RecommendationId, input.Decision switch
                {
                    OwnerDecision.Approved => "Recommendation approved. A restock record was created. Nothing has been ordered automatically - contact the supplier, then mark the restock as Ordered.",
                    OwnerDecision.Deferred => "Recommendation deferred. It will stay in the list and be refreshed at the next evaluation.",
                    _ => "Recommendation dismissed."
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error("Saving the owner decision failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }
    }
}
