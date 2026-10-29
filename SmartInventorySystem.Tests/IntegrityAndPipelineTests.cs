using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services;
using SmartInventorySystem.Services.Alerts;
using SmartInventorySystem.Services.Analytics;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Core;
using SmartInventorySystem.Services.Reordering;
using SmartInventorySystem.Services.Reports;
using Xunit;

namespace SmartInventorySystem.Tests
{
    // =====================================================================================
    // Rules enforced by the DATABASE itself (not only by the screens)
    // =====================================================================================
    public class DatabaseConstraintTests : IAsyncLifetime
    {
        private World _w = null!;

        public async Task InitializeAsync() => _w = await World.CreateAsync();

        public Task DisposeAsync()
        {
            _w.Dispose();
            return Task.CompletedTask;
        }

        [Fact]
        public async Task Inventory_CannotGoNegative()
        {
            var item = await _w.AddItemAsync("Paper", 5);
            await using var db = _w.Db.CreateDbContext();
            (await db.Inventories.SingleAsync(i => i.ItemId == item)).QuantityOnHand = -1m;

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        [Fact]
        public async Task DuplicateSku_IsRefused_IgnoringCase()
        {
            await _w.AddItemAsync("One", 1, sku: "ABC-1");
            await using var db = _w.Db.CreateDbContext();
            db.Items.Add(new Item
            {
                ItemName = "Two", SKU = "abc-1", CategoryId = _w.CategoryId, UnitOfMeasure = "pc",
                Inventory = new Inventory { QuantityOnHand = 0 }
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        [Fact]
        public async Task DuplicateUsername_IsRefused_IgnoringCase()
        {
            await using var db = _w.Db.CreateDbContext();
            var role = await db.Roles.FirstAsync();
            db.Users.Add(new User { Username = "ADMIN", FullName = "Copy", PasswordHash = "x", RoleId = role.RoleId });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        [Fact]
        public async Task EachItem_CanHaveOnlyOneInventoryRow()
        {
            var item = await _w.AddItemAsync("Paper", 5);
            await using var db = _w.Db.CreateDbContext();
            db.Inventories.Add(new Inventory { ItemId = item, QuantityOnHand = 1 });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        [Fact]
        public async Task ZeroQuantityTransactions_AreRefused()
        {
            var item = await _w.AddItemAsync("Paper", 5);
            await using var db = _w.Db.CreateDbContext();
            db.StockIns.Add(new StockIn { ItemId = item, Quantity = 0, ReceivedByUserId = _w.AdminId });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        [Fact]
        public async Task NegativeUnitCost_IsRefused()
        {
            await using var db = _w.Db.CreateDbContext();
            db.Items.Add(new Item
            {
                ItemName = "Bad", SKU = "BAD-1", CategoryId = _w.CategoryId, UnitOfMeasure = "pc", UnitCost = -1m,
                Inventory = new Inventory { QuantityOnHand = 0 }
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        [Fact]
        public async Task AnItem_CannotBeItsOwnSubstitute()
        {
            var item = await _w.AddItemAsync("Paper", 5);
            await using var db = _w.Db.CreateDbContext();
            db.ItemSubstitutes.Add(new ItemSubstitute { ItemId = item, SubstituteItemId = item });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    // =====================================================================================
    // The whole pipeline on the real seed data (10 items, ~90 days of history)
    // =====================================================================================
    public class SeededPipelineTests : IAsyncLifetime
    {
        private TestDb _db = null!;
        private FakeAuth _auth = null!;
        private AuditService _audit = null!;
        private int _adminId;

        public async Task InitializeAsync()
        {
            _db = new TestDb();
            _auth = new FakeAuth();
            _audit = new AuditService(_db);

            await using var db = _db.CreateDbContext();
            await DatabaseSeeder.SeedAsync(db);
            _adminId = (await db.Users.SingleAsync(u => u.Username == "admin")).UserId;
            _auth.SignInAs(RoleNames.Administrator, _adminId);
        }

        public Task DisposeAsync()
        {
            _db.Dispose();
            return Task.CompletedTask;
        }

        [Fact]
        public async Task Seed_CreatesTheExpectedStartingData()
        {
            await using var db = _db.CreateDbContext();

            Assert.Equal(3, await db.Roles.CountAsync());
            Assert.Equal(3, await db.Users.CountAsync());
            Assert.Equal(7, await db.Categories.CountAsync());
            Assert.Equal(10, await db.Items.CountAsync());
            Assert.Equal(14, await db.SystemSettings.CountAsync());
            Assert.All(await db.Users.ToListAsync(), u => Assert.NotEqual("Admin@123", u.PasswordHash));
        }

        [Fact]
        public async Task Seed_IsSafeToRunTwice()
        {
            await using var db = _db.CreateDbContext();
            await DatabaseSeeder.SeedAsync(db);

            Assert.Equal(3, await db.Users.CountAsync());
            Assert.Equal(10, await db.Items.CountAsync());
            Assert.Equal(14, await db.SystemSettings.CountAsync());
        }

        [Fact]
        public async Task EveryItem_StockEqualsStockInMinusStockOut_AndUsageMatchesStockOut()
        {
            await using var db = _db.CreateDbContext();
            var items = await db.Items.Include(i => i.Inventory).ToListAsync();
            var ins = await db.StockIns.ToListAsync();
            var outs = await db.StockOuts.ToListAsync();
            var usage = await db.UsageTransactions.ToListAsync();

            Assert.NotEmpty(usage);
            foreach (var item in items)
            {
                var received = ins.Where(s => s.ItemId == item.ItemId).Sum(s => s.Quantity);
                var released = outs.Where(s => s.ItemId == item.ItemId).Sum(s => s.Quantity);
                var used = usage.Where(u => u.ItemId == item.ItemId).Sum(u => u.QuantityUsed);

                Assert.True(Math.Abs(received - released - item.Inventory!.QuantityOnHand) < 0.01m, $"{item.ItemName}: stock does not match its history");
                Assert.True(Math.Abs(released - used) < 0.01m, $"{item.ItemName}: usage does not match stock-out");
                Assert.True(item.Inventory.QuantityOnHand >= 0);
            }
        }

        [Fact]
        public async Task MovementAnalysis_ClassifiesEveryItem_WithConsumptionThatMatchesTheRecords()
        {
            var movement = new MovementService(_db, _audit);
            var from = DateTime.Today.AddDays(-89);

            var run = await movement.RunAsync(from, DateTime.Today, _adminId);
            Assert.True(run.Success, run.Error);

            var snapshot = await movement.GetLatestAsync();
            Assert.Equal(10, snapshot.Rows.Count);

            await using var db = _db.CreateDbContext();
            var usage = await db.UsageTransactions.ToListAsync();
            foreach (var row in snapshot.Rows)
            {
                var expected = usage
                    .Where(u => u.ItemId == row.ItemId
                                && HistoryLoader.LocalDate(u.TransactionDate) >= from.Date
                                && HistoryLoader.LocalDate(u.TransactionDate) <= DateTime.Today)
                    .Sum(u => u.QuantityUsed);

                Assert.True(Math.Abs(expected - row.TotalConsumption) < 0.01m, $"{row.ItemName}: consumption differs from the usage records");
                Assert.True(row.TurnoverRate >= 0);
            }
        }

        [Fact]
        public async Task MovementAnalysis_RerunningTheSameRange_ReplacesTheEarlierResult()
        {
            var movement = new MovementService(_db, _audit);
            var from = DateTime.Today.AddDays(-89);

            await movement.RunAsync(from, DateTime.Today, _adminId);
            await movement.RunAsync(from, DateTime.Today, _adminId);

            await using var db = _db.CreateDbContext();
            Assert.Equal(10, await db.MovementClassifications.CountAsync());
        }

        [Fact]
        public async Task Trends_EveryPeriod_BalancesBeginningPlusInMinusOutToEnding()
        {
            var trends = new TrendService(_db, _audit);

            var run = await trends.GenerateAsync(DateTime.Today.AddDays(-89), DateTime.Today, _adminId);
            Assert.True(run.Success, run.Error);

            var rows = await trends.GetAsync();
            Assert.Equal(10 * 12, rows.Count);  // 10 items x 12 whole weeks
            foreach (var r in rows)
            {
                Assert.True(Math.Abs(r.BeginningStock + r.StockIn - r.StockOut - r.EndingStock) < 0.02m,
                    $"{r.ItemName} {r.Period}: stock does not balance");
            }
        }

        [Fact]
        public async Task Forecasts_BackTest_ComparesWithActualUsage_AndCanBeReproducedByHand()
        {
            var forecast = new ForecastService(_db, _audit, _auth);

            var run = await forecast.BacktestAsync(ForecastMethod.WeightedMovingAverage, 4, _adminId);
            Assert.True(run.Success, run.Error);

            var rows = await forecast.GetAsync();
            Assert.Equal(40, rows.Count(r => r.Actual is not null));    // 10 items x 4 finished periods

            var summary = ForecastCalculator.Summarize(rows);
            Assert.NotNull(summary.Mape);

            // Recalculate one forecast by hand from the history stored with it: 0.5 / 0.3 / 0.2
            var sample = rows.First();
            Assert.Equal(3, sample.History.Count);
            var expected = Math.Round(0.5m * sample.History[0] + 0.3m * sample.History[1] + 0.2m * sample.History[2], 2);
            Assert.True(Math.Abs(expected - sample.ForecastValue) < 0.011m);
        }

        [Fact]
        public async Task Forecasts_NextPeriod_CreatesOneForecastPerItem()
        {
            var forecast = new ForecastService(_db, _audit, _auth);

            var run = await forecast.GenerateAsync(ForecastMethod.SimpleExponentialSmoothing, _adminId);
            Assert.True(run.Success, run.Error);

            Assert.Equal(10, (await forecast.GetAsync()).Count);
        }

        [Fact]
        public async Task ReorderEvaluation_RunsOnRealData_AndOnlySavesItemsThatNeedAttention()
        {
            var reorder = new ReorderService(_db, _audit, _auth);

            var run = await reorder.EvaluateAsync(_adminId);
            Assert.True(run.Success, run.Error);

            var recs = await reorder.ListAsync();
            Assert.All(recs, r =>
            {
                Assert.NotEqual(UrgencyLevel.NotYetNecessary, r.Urgency);
                Assert.True(r.RecommendedQuantity >= 0);
                Assert.Equal(RecommendationStatus.Pending, r.Status);
            });

            // running it again must not multiply the suggestions
            await reorder.EvaluateAsync(_adminId);
            Assert.Equal(recs.Count, (await reorder.ListAsync()).Count);
        }

        [Fact]
        public async Task Alerts_AfterAllAnalyses_HaveNoDuplicates()
        {
            await new MovementService(_db, _audit).RunAsync(DateTime.Today.AddDays(-89), DateTime.Today, _adminId);
            await new ForecastService(_db, _audit, _auth).BacktestAsync(ForecastMethod.WeightedMovingAverage, 4, _adminId);
            await new ReorderService(_db, _audit, _auth).EvaluateAsync(_adminId);

            var alerts = new AlertService(_db, _audit);
            await alerts.RefreshAsync();
            await alerts.RefreshAsync();

            var open = (await alerts.ListAsync()).Where(a => !a.IsAcknowledged).ToList();
            Assert.Equal(open.Count, open.Select(a => (a.ItemId, a.Type)).Distinct().Count());
            Assert.All(open, a => Assert.False(string.IsNullOrWhiteSpace(a.Message)));
        }

        [Fact]
        public async Task Dashboard_ReflectsTheSeededData()
        {
            var dashboard = await new DashboardService(_db).GetAsync();

            Assert.Equal(10, dashboard.TotalItems);
            Assert.Equal(10, dashboard.StatusCounts.Values.Sum());
            Assert.Equal(30, dashboard.DailyUsage.Count);
            Assert.True(dashboard.TotalValue > 0);
        }

        [Fact]
        public async Task Reports_InventoryStatus_ListsEveryActiveItem_AndIsHiddenFromCashiers()
        {
            var inventory = new InventoryService(_db);
            var reports = new ReportService(_db, _audit, _auth, inventory,
                new MovementService(_db, _audit), new ForecastService(_db, _audit, _auth),
                new TrendService(_db, _audit), new ReorderService(_db, _audit, _auth));

            var ok = await reports.BuildAsync(new ReportRequest(ReportType.InventoryStatus, 0, null, null), _adminId);
            Assert.True(ok.Success, ok.Error);
            Assert.Equal(10, ok.Table!.Rows.Count);
            Assert.Contains("Item", ok.Table.Columns);

            _auth.SignInAs(RoleNames.SalesCashier, 3);
            var denied = await reports.BuildAsync(new ReportRequest(ReportType.InventoryStatus, 0, null, null), 3);
            Assert.False(denied.Success);
        }
    }
}
