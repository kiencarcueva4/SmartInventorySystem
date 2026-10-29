using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Services;
using SmartInventorySystem.Services.Admin;
using SmartInventorySystem.Services.Alerts;
using SmartInventorySystem.Services.Reordering;
using Xunit;

namespace SmartInventorySystem.Tests
{
    // =====================================================================================
    // Reorder recommendations + the owner's approval workflow + restock tracking
    // =====================================================================================
    public class ReorderWorkflowTests : IAsyncLifetime
    {
        private World _w = null!;
        private ReorderService _reorder = null!;
        private RestockService _restock = null!;

        public async Task InitializeAsync()
        {
            _w = await World.CreateAsync();
            _reorder = new ReorderService(_w.Db, _w.Audit, _w.Auth);
            _restock = new RestockService(_w.Db, _w.Audit, _w.Auth);
        }

        public Task DisposeAsync()
        {
            _w.Dispose();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Stock 20, safety 5, reorder point 30, supplier lead time 5 days, used 10 per day for 40 days.
        /// Forecast = 10 per day, so lead-time demand = 50, target = 55 and the suggestion is 55 - 20 = 35.
        /// </summary>
        private async Task<int> UrgentItemAsync(string name = "A4 Bond Paper")
        {
            var id = await _w.AddItemAsync(name, quantity: 20, safetyStock: 5, reorderPoint: 30);
            for (var d = 1; d <= 40; d++) await _w.AddUsageAsync(id, 10m, d);
            return id;
        }

        private async Task<List<ReorderRecommendation>> AllRecommendationsAsync()
        {
            await using var db = _w.Db.CreateDbContext();
            return await db.ReorderRecommendations.AsNoTracking().ToListAsync();
        }

        private DecisionInput Approve(int recommendationId, decimal quantity = 35) => new()
        {
            RecommendationId = recommendationId, Decision = OwnerDecision.Approved,
            ApprovedQuantity = quantity, ExpectedDelivery = DateTime.Today.AddDays(5)
        };

        [Fact]
        public async Task Evaluate_CreatesAnUrgentRecommendation_WithTheCalculatedQuantity()
        {
            var item = await UrgentItemAsync();

            var r = await _reorder.EvaluateAsync(_w.AdminId);
            Assert.True(r.Success, r.Error);

            var rec = Assert.Single(await AllRecommendationsAsync());
            Assert.Equal(item, rec.ItemId);
            Assert.Equal(UrgencyLevel.Urgent, rec.Urgency);
            Assert.Equal(20m, rec.CurrentStock);
            Assert.Equal(5, rec.LeadTimeDays);
            Assert.Equal(50m, rec.LeadTimeDemand);
            Assert.Equal(35m, rec.RecommendedQuantity);
            Assert.Equal(RecommendationStatus.Pending, rec.Status);
            Assert.Equal(OwnerDecision.Pending, rec.OwnerDecision);
            Assert.False(string.IsNullOrWhiteSpace(rec.Reason));
        }

        [Fact]
        public async Task Evaluate_WithPlentyOfStock_SavesNothing()
        {
            var id = await _w.AddItemAsync("Well stocked", quantity: 10000, safetyStock: 5, reorderPoint: 30);
            for (var d = 1; d <= 40; d++) await _w.AddUsageAsync(id, 10m, d);

            var r = await _reorder.EvaluateAsync(_w.AdminId);

            Assert.True(r.Success, r.Error);
            Assert.Empty(await AllRecommendationsAsync());
        }

        [Fact]
        public async Task Evaluate_Twice_DoesNotCreateDuplicates()
        {
            await UrgentItemAsync();

            await _reorder.EvaluateAsync(_w.AdminId);
            await _reorder.EvaluateAsync(_w.AdminId);

            Assert.Single(await AllRecommendationsAsync());
        }

        [Fact]
        public async Task Evaluate_NeverOrdersAnything()
        {
            await UrgentItemAsync();

            await _reorder.EvaluateAsync(_w.AdminId);

            await using var db = _w.Db.CreateDbContext();
            Assert.Empty(await db.PurchaseRestockRecords.ToListAsync()); // nothing is created without the owner's approval
        }

        [Fact]
        public async Task Evaluate_AutoClosesARecommendation_WhenStockIsSufficientAgain()
        {
            var item = await UrgentItemAsync();
            await _reorder.EvaluateAsync(_w.AdminId);

            await _w.SetQuantityAsync(item, 1000);
            await _reorder.EvaluateAsync(_w.AdminId);

            var rec = Assert.Single(await AllRecommendationsAsync());
            Assert.Equal(RecommendationStatus.Dismissed, rec.Status);
            Assert.Equal(OwnerDecision.Pending, rec.OwnerDecision); // closed by the system, not decided by the owner
        }

        [Fact]
        public async Task Approve_RecordsTheDecision_AndCreatesAPlannedRestock()
        {
            await UrgentItemAsync();
            await _reorder.EvaluateAsync(_w.AdminId);
            var rec = Assert.Single(await AllRecommendationsAsync());

            var r = await _reorder.DecideAsync(Approve(rec.RecommendationId), _w.AdminId);
            Assert.True(r.Success, r.Error);

            await using var db = _w.Db.CreateDbContext();
            var saved = await db.ReorderRecommendations.SingleAsync();
            Assert.Equal(RecommendationStatus.Approved, saved.Status);
            Assert.Equal(OwnerDecision.Approved, saved.OwnerDecision);
            Assert.Equal(_w.AdminId, saved.DecidedByUserId);
            Assert.NotNull(saved.DecisionDate);

            var restock = await db.PurchaseRestockRecords.SingleAsync();
            Assert.Equal(RestockStatus.Planned, restock.Status);
            Assert.Equal(35m, restock.ApprovedQuantity);
            Assert.Equal(_w.SupplierId, restock.SupplierId);
            Assert.True(await _w.AuditContainsAsync("Approve Recommendation"));
        }

        [Fact]
        public async Task ADecidedRecommendation_CannotBeDecidedAgain()
        {
            await UrgentItemAsync();
            await _reorder.EvaluateAsync(_w.AdminId);
            var rec = Assert.Single(await AllRecommendationsAsync());
            await _reorder.DecideAsync(Approve(rec.RecommendationId), _w.AdminId);

            var again = await _reorder.DecideAsync(Approve(rec.RecommendationId), _w.AdminId);

            Assert.False(again.Success);
            await using var db = _w.Db.CreateDbContext();
            Assert.Single(await db.PurchaseRestockRecords.ToListAsync());
        }

        [Fact]
        public async Task OnlyTheOwner_CanDecide()
        {
            await UrgentItemAsync();
            await _reorder.EvaluateAsync(_w.AdminId);
            var rec = Assert.Single(await AllRecommendationsAsync());

            _w.Auth.SignInAs(RoleNames.InventoryPersonnel, _w.InventoryUserId);
            var r = await _reorder.DecideAsync(Approve(rec.RecommendationId), _w.InventoryUserId);

            Assert.False(r.Success);
            Assert.Contains("Only the owner", r.Error);
            Assert.Equal(RecommendationStatus.Pending, (await AllRecommendationsAsync()).Single().Status);
        }

        [Fact]
        public async Task Approve_RequiresAPositiveQuantity_AndAFutureOrTodayDeliveryDate()
        {
            await UrgentItemAsync();
            await _reorder.EvaluateAsync(_w.AdminId);
            var id = Assert.Single(await AllRecommendationsAsync()).RecommendationId;

            Assert.False((await _reorder.DecideAsync(Approve(id, 0), _w.AdminId)).Success);

            var past = Approve(id);
            past.ExpectedDelivery = DateTime.Today.AddDays(-1);
            Assert.False((await _reorder.DecideAsync(past, _w.AdminId)).Success);

            Assert.Equal(RecommendationStatus.Pending, (await AllRecommendationsAsync()).Single().Status);
        }

        [Fact]
        public async Task Evaluate_SkipsItemsThatAreAlreadyOnOrder()
        {
            await UrgentItemAsync();
            await _reorder.EvaluateAsync(_w.AdminId);
            var id = Assert.Single(await AllRecommendationsAsync()).RecommendationId;
            await _reorder.DecideAsync(Approve(id), _w.AdminId);

            await _reorder.EvaluateAsync(_w.AdminId);

            Assert.Single(await AllRecommendationsAsync()); // no second suggestion for the same item
        }

        [Fact]
        public async Task Dismiss_RecordsTheDecision_AndCreatesNoRestock()
        {
            await UrgentItemAsync();
            await _reorder.EvaluateAsync(_w.AdminId);
            var id = Assert.Single(await AllRecommendationsAsync()).RecommendationId;

            var r = await _reorder.DecideAsync(new DecisionInput { RecommendationId = id, Decision = OwnerDecision.Dismissed, Note = "Not needed" }, _w.AdminId);
            Assert.True(r.Success, r.Error);

            await using var db = _w.Db.CreateDbContext();
            var rec = await db.ReorderRecommendations.SingleAsync();
            Assert.Equal(RecommendationStatus.Dismissed, rec.Status);
            Assert.Equal(OwnerDecision.Dismissed, rec.OwnerDecision);
            Assert.Contains("Owner note", rec.Reason);
            Assert.Empty(await db.PurchaseRestockRecords.ToListAsync());
        }

        [Fact]
        public async Task Defer_KeepsTheRecommendation_AndTheOwnersNoteSurvivesANewEvaluation()
        {
            await UrgentItemAsync();
            await _reorder.EvaluateAsync(_w.AdminId);
            var id = Assert.Single(await AllRecommendationsAsync()).RecommendationId;

            await _reorder.DecideAsync(new DecisionInput { RecommendationId = id, Decision = OwnerDecision.Deferred, Note = "Ask supplier first" }, _w.AdminId);
            await _reorder.EvaluateAsync(_w.AdminId);

            var rec = Assert.Single(await AllRecommendationsAsync());
            Assert.Equal(RecommendationStatus.Deferred, rec.Status);
            Assert.Contains("Ask supplier first", rec.Reason);
        }

        [Fact]
        public async Task Restock_FollowsPlannedOrderedReceived_AndCompletesTheRecommendation()
        {
            await UrgentItemAsync();
            await _reorder.EvaluateAsync(_w.AdminId);
            var recId = Assert.Single(await AllRecommendationsAsync()).RecommendationId;
            await _reorder.DecideAsync(Approve(recId), _w.AdminId);

            int restockId;
            await using (var db = _w.Db.CreateDbContext()) restockId = (await db.PurchaseRestockRecords.SingleAsync()).PurchaseRestockId;

            // cannot jump straight from Planned to Received
            Assert.False((await _restock.UpdateAsync(new RestockUpdateInput { RestockId = restockId, NewStatus = RestockStatus.Received }, _w.AdminId)).Success);

            Assert.True((await _restock.UpdateAsync(new RestockUpdateInput { RestockId = restockId, NewStatus = RestockStatus.Ordered, PurchaseDate = DateTime.Today }, _w.AdminId)).Success);
            Assert.True((await _restock.UpdateAsync(new RestockUpdateInput { RestockId = restockId, NewStatus = RestockStatus.Received }, _w.AdminId)).Success);

            await using var check = _w.Db.CreateDbContext();
            Assert.Equal(RestockStatus.Received, (await check.PurchaseRestockRecords.SingleAsync()).Status);
            Assert.Equal(RecommendationStatus.Completed, (await check.ReorderRecommendations.SingleAsync()).Status);

            // a finished restock cannot be changed any more
            Assert.False((await _restock.UpdateAsync(new RestockUpdateInput { RestockId = restockId, NewStatus = RestockStatus.Cancelled }, _w.AdminId)).Success);
        }

        [Fact]
        public async Task Restock_CannotBeUpdatedByACashier()
        {
            await UrgentItemAsync();
            await _reorder.EvaluateAsync(_w.AdminId);
            var recId = Assert.Single(await AllRecommendationsAsync()).RecommendationId;
            await _reorder.DecideAsync(Approve(recId), _w.AdminId);
            int restockId;
            await using (var db = _w.Db.CreateDbContext()) restockId = (await db.PurchaseRestockRecords.SingleAsync()).PurchaseRestockId;

            _w.Auth.SignInAs(RoleNames.SalesCashier, _w.CashierId);
            var r = await _restock.UpdateAsync(new RestockUpdateInput { RestockId = restockId, NewStatus = RestockStatus.Ordered, PurchaseDate = DateTime.Today }, _w.CashierId);

            Assert.False(r.Success);
        }

        [Fact]
        public async Task Evaluate_RefusesToRun_WhenTheForecastWeightsAreInvalid()
        {
            await UrgentItemAsync();
            await using (var db = _w.Db.CreateDbContext())
            {
                db.SystemSettings.Add(new SystemSetting { SettingKey = SettingKeys.ForecastWeightRecent, SettingValue = "0.90" });
                await db.SaveChangesAsync(); // 0.90 + default 0.30 + 0.20 = 1.40
            }

            var r = await _reorder.EvaluateAsync(_w.AdminId);

            Assert.False(r.Success);
            Assert.Contains("1.00", r.Error);
        }
    }

    // =====================================================================================
    // Alerts
    // =====================================================================================
    public class AlertServiceTests : IAsyncLifetime
    {
        private World _w = null!;
        private AlertService _alerts = null!;

        public async Task InitializeAsync()
        {
            _w = await World.CreateAsync();
            _alerts = new AlertService(_w.Db, _w.Audit);
        }

        public Task DisposeAsync()
        {
            _w.Dispose();
            return Task.CompletedTask;
        }

        private async Task<List<Alert>> OpenAlertsAsync()
        {
            await using var db = _w.Db.CreateDbContext();
            return await db.Alerts.AsNoTracking().Where(a => !a.IsAcknowledged).ToListAsync();
        }

        [Fact]
        public async Task LowStock_RaisesACriticalAlert_ForTheRightItem()
        {
            // quantity 5 is at/below 50% of the safety stock of 10 -> Critical
            var item = await _w.AddItemAsync("Black Ink", quantity: 5, safetyStock: 10, reorderPoint: 20);

            var open = await _alerts.RefreshAsync();

            Assert.True(open >= 1);
            var alert = (await OpenAlertsAsync()).Single(a => a.AlertType == AlertType.LowStock);
            Assert.Equal(item, alert.ItemId);
            Assert.Equal(AlertSeverity.Critical, alert.Severity);
        }

        [Fact]
        public async Task RefreshingTwice_DoesNotDuplicateAlerts()
        {
            await _w.AddItemAsync("Black Ink", quantity: 5, safetyStock: 10, reorderPoint: 20);

            await _alerts.RefreshAsync();
            await _alerts.RefreshAsync();

            Assert.Single((await OpenAlertsAsync()).Where(a => a.AlertType == AlertType.LowStock));
        }

        [Fact]
        public async Task AlertCloses_WhenTheProblemIsGone()
        {
            var item = await _w.AddItemAsync("Black Ink", quantity: 5, safetyStock: 10, reorderPoint: 20);
            await _alerts.RefreshAsync();

            await _w.SetQuantityAsync(item, 100);
            await _alerts.RefreshAsync();

            Assert.DoesNotContain(await OpenAlertsAsync(), a => a.AlertType == AlertType.LowStock);

            await using var db = _w.Db.CreateDbContext();
            var closed = await db.Alerts.SingleAsync(a => a.AlertType == AlertType.LowStock);
            Assert.True(closed.IsAcknowledged);
            Assert.Null(closed.AcknowledgedByUserId); // resolved by the system, nobody pressed the button
        }

        [Fact]
        public async Task Acknowledge_RecordsWhoDidIt()
        {
            await _w.AddItemAsync("Black Ink", quantity: 5, safetyStock: 10, reorderPoint: 20);
            await _alerts.RefreshAsync();
            var id = (await OpenAlertsAsync()).First().AlertId;

            var r = await _alerts.AcknowledgeAsync(id, _w.AdminId);

            Assert.True(r.Success, r.Error);
            await using var db = _w.Db.CreateDbContext();
            var alert = await db.Alerts.SingleAsync(a => a.AlertId == id);
            Assert.True(alert.IsAcknowledged);
            Assert.Equal(_w.AdminId, alert.AcknowledgedByUserId);
            Assert.NotNull(alert.AcknowledgedAt);
        }

        [Fact]
        public async Task HealthyStock_RaisesNoAlerts()
        {
            await _w.AddItemAsync("Fine item", quantity: 50, safetyStock: 10, reorderPoint: 30); // above reorder point, below the overstock level (30 x 3)

            await _alerts.RefreshAsync();

            Assert.Empty(await OpenAlertsAsync());
        }
    }

    // =====================================================================================
    // System settings
    // =====================================================================================
    public class SettingsServiceTests : IAsyncLifetime
    {
        private World _w = null!;
        private SettingsService _settings = null!;

        public async Task InitializeAsync()
        {
            _w = await World.CreateAsync();
            _settings = new SettingsService(_w.Db, _w.Audit, _w.Auth);
        }

        public Task DisposeAsync()
        {
            _w.Dispose();
            return Task.CompletedTask;
        }

        private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) =>
            pairs.ToDictionary(p => p.Key, p => p.Value);

        [Fact]
        public async Task ForecastWeights_MustTotalOne()
        {
            var r = await _settings.SaveAsync(Values(
                (SettingKeys.ForecastWeightRecent, "0.5"),
                (SettingKeys.ForecastWeightPrevious, "0.5"),
                (SettingKeys.ForecastWeightOlder, "0.5")), _w.AdminId);

            Assert.False(r.Success);
            Assert.Contains("1.00", r.Error);
        }

        [Fact]
        public async Task SlowLimit_MustBeBelowTheFastLimit()
        {
            var r = await _settings.SaveAsync(Values(
                (SettingKeys.MovementFastMinTurnover, "1"),
                (SettingKeys.MovementSlowMaxTurnover, "2")), _w.AdminId);

            Assert.False(r.Success);
        }

        [Theory]
        [InlineData("Movement.EvaluationDays", "3")]       // below the minimum of 7
        [InlineData("Movement.EvaluationDays", "abc")]     // not a number
        [InlineData("Movement.EvaluationDays", "30.5")]    // must be whole
        [InlineData("Reorder.OverstockMultiplier", "0.5")] // below the minimum of 1
        [InlineData("Alert.CriticalStockPercentOfSafety", "150")]
        public async Task InvalidValues_AreRejected(string key, string value)
        {
            var r = await _settings.SaveAsync(Values((key, value)), _w.AdminId);

            Assert.False(r.Success);
        }

        [Fact]
        public async Task ValidChange_IsSaved_AndAudited()
        {
            var r = await _settings.SaveAsync(Values((SettingKeys.MovementEvaluationDays, "60")), _w.AdminId);
            Assert.True(r.Success, r.Error);

            var entry = (await _settings.GetAsync()).Single(e => e.Definition.Key == SettingKeys.MovementEvaluationDays);
            Assert.Equal("60", entry.Value);
            Assert.True(await _w.AuditContainsAsync("System Setting Change"));
        }

        [Fact]
        public async Task NothingIsSaved_WhenOneValueIsInvalid()
        {
            var r = await _settings.SaveAsync(Values(
                (SettingKeys.MovementEvaluationDays, "60"),      // valid
                (SettingKeys.ReorderDefaultLeadTimeDays, "-4")), _w.AdminId); // invalid

            Assert.False(r.Success);
            var saved = (await _settings.GetAsync()).Single(e => e.Definition.Key == SettingKeys.MovementEvaluationDays);
            Assert.NotEqual("60", saved.Value);
        }

        [Fact]
        public async Task OnlyTheAdministrator_CanChangeSettings()
        {
            _w.Auth.SignInAs(RoleNames.InventoryPersonnel, _w.InventoryUserId);

            var r = await _settings.SaveAsync(Values((SettingKeys.MovementEvaluationDays, "60")), _w.InventoryUserId);

            Assert.False(r.Success);
        }
    }
}
