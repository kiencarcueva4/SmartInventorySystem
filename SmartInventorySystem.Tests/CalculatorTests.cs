using System;
using System.Collections.Generic;
using System.Linq;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Services;
using SmartInventorySystem.Services.Analytics;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Core;
using SmartInventorySystem.Services.Reordering;
using SmartInventorySystem.Services.Reports;
using SmartInventorySystem.Services.Security;
using Xunit;

namespace SmartInventorySystem.Tests
{
    // =====================================================================================
    // Reorder rules  (Lead-time demand + safety stock - current stock)
    // =====================================================================================
    public class ReorderCalculatorTests
    {
        [Fact]
        public void SpecExample_A4BondPaper_IsUrgent_With450Sheets()
        {
            // Stock 250, safety stock 100, lead time 5 days, demand 120/day -> lead-time demand 600.
            var r = ReorderCalculator.Evaluate(stock: 250, safetyStock: 100, reorderPoint: 300,
                dailyDemand: 120, leadTimeDays: 5, minimumOrderQuantity: 0, unit: "sheet");

            Assert.Equal(UrgencyLevel.Urgent, r.Urgency);
            Assert.Equal(600m, r.LeadTimeDemand);
            Assert.Equal(700m, r.TargetLevel);      // 600 + 100
            Assert.Equal(450m, r.RecommendedQuantity); // 700 - 250
        }

        [Fact]
        public void OutOfStock_IsUrgent()
        {
            var r = ReorderCalculator.Evaluate(0, 10, 30, 5, 5, 0, "pc");
            Assert.Equal(UrgencyLevel.Urgent, r.Urgency);
        }

        [Fact]
        public void AtSafetyStock_IsUrgent()
        {
            var r = ReorderCalculator.Evaluate(100, 100, 300, 1, 5, 0, "pc");
            Assert.Equal(UrgencyLevel.Urgent, r.Urgency);
        }

        [Fact]
        public void BelowLeadTimeDemand_IsUrgent()
        {
            // 150 in stock but 40/day x 5 days = 200 needed before a delivery can arrive.
            var r = ReorderCalculator.Evaluate(150, 100, 120, 40, 5, 0, "pc");
            Assert.Equal(UrgencyLevel.Urgent, r.Urgency);
        }

        [Fact]
        public void AtReorderPoint_IsRecommended()
        {
            var r = ReorderCalculator.Evaluate(300, 100, 300, 10, 5, 0, "pc");
            Assert.Equal(UrgencyLevel.Recommended, r.Urgency);
        }

        [Fact]
        public void BelowTargetLevel_IsRecommended()
        {
            // Stock 500 = lead-time demand 500, but the target (500 + 100 safety = 600) is higher.
            var r = ReorderCalculator.Evaluate(500, 100, 300, 100, 5, 0, "pc");
            Assert.Equal(UrgencyLevel.Recommended, r.Urgency);
            Assert.Equal(100m, r.RecommendedQuantity);
        }

        [Fact]
        public void PlentyOfStock_IsNotYetNecessary_WithZeroQuantity()
        {
            var r = ReorderCalculator.Evaluate(1000, 100, 300, 10, 5, 0, "pc");
            Assert.Equal(UrgencyLevel.NotYetNecessary, r.Urgency);
            Assert.Equal(0m, r.RecommendedQuantity);
        }

        [Fact]
        public void MinimumOrderQuantity_IsUsed_WhenSuggestionIsSmaller()
        {
            // Needed = 105 - 100 = 5, but the item is always ordered in lots of at least 50.
            var r = ReorderCalculator.Evaluate(100, 100, 120, 1, 5, 50, "pc");
            Assert.Equal(50m, r.RecommendedQuantity);
        }

        [Fact]
        public void Quantity_IsRoundedUpToWholeUnits()
        {
            // Target 7.5, stock 4 -> needs 3.5 -> 4.
            var r = ReorderCalculator.Evaluate(4, 0, 0, 1.5m, 5, 0, "pc");
            Assert.Equal(4m, r.RecommendedQuantity);
        }

        [Theory]
        [InlineData(0, 0, 0, 0, 0)]
        [InlineData(50, 10, 20, 3, 7)]
        [InlineData(5000, 100, 300, 2, 14)]
        public void Quantity_IsNeverNegative(double stock, double safety, double rp, double daily, int lead)
        {
            var r = ReorderCalculator.Evaluate((decimal)stock, (decimal)safety, (decimal)rp, (decimal)daily, lead, 0, "pc");
            Assert.True(r.RecommendedQuantity >= 0);
        }
    }

    // =====================================================================================
    // Stock status
    // =====================================================================================
    public class StockStatusTests
    {
        [Theory]
        [InlineData(0, 10, 30, 3, StockStatus.OutOfStock)]
        [InlineData(-5, 10, 30, 3, StockStatus.OutOfStock)]
        [InlineData(10, 10, 30, 3, StockStatus.Critical)]   // at safety stock
        [InlineData(25, 10, 30, 3, StockStatus.LowStock)]   // above safety, below reorder point
        [InlineData(30, 10, 30, 3, StockStatus.LowStock)]   // at the reorder point
        [InlineData(60, 10, 30, 3, StockStatus.InStock)]
        [InlineData(91, 10, 30, 3, StockStatus.Overstock)]  // above 30 x 3
        public void Calculate_FollowsTheRules(double qty, double safety, double rp, double multiplier, StockStatus expected)
        {
            Assert.Equal(expected, StockStatusCalculator.Calculate((decimal)qty, (decimal)safety, (decimal)rp, (decimal)multiplier));
        }

        [Fact]
        public void ItemWithoutReorderPoint_IsNeverOverstock()
        {
            Assert.Equal(StockStatus.InStock, StockStatusCalculator.Calculate(5000, 0, 0, 3));
        }
    }

    // =====================================================================================
    // Forecasting
    // =====================================================================================
    public class ForecastCalculatorTests
    {
        [Fact]
        public void WeightedMovingAverage_AppliesWeightsToMostRecentFirst()
        {
            // 100 x 0.5 + 80 x 0.3 + 60 x 0.2 = 50 + 24 + 12
            var f = ForecastCalculator.WeightedMovingAverage(new[] { 100m, 80m, 60m }, new[] { 0.5m, 0.3m, 0.2m });
            Assert.Equal(86m, f);
        }

        [Fact]
        public void WeightedMovingAverage_Throws_WhenWeightCountDiffers()
        {
            Assert.Throws<ArgumentException>(() =>
                ForecastCalculator.WeightedMovingAverage(new[] { 1m, 2m }, new[] { 0.5m, 0.3m, 0.2m }));
        }

        [Fact]
        public void SimpleExponentialSmoothing_MatchesHandCalculation()
        {
            // level: 10 -> 0.5 x 20 + 0.5 x 10 = 15 -> 0.5 x 30 + 0.5 x 15 = 22.5
            Assert.Equal(22.5m, ForecastCalculator.SimpleExponentialSmoothing(new[] { 10m, 20m, 30m }, 0.5m));
        }

        [Fact]
        public void SimpleExponentialSmoothing_EmptyHistory_IsZero()
        {
            Assert.Equal(0m, ForecastCalculator.SimpleExponentialSmoothing(Array.Empty<decimal>(), 0.3m));
        }

        [Theory]
        [InlineData(0.5, 0.3, 0.2, true)]
        [InlineData(0.6, 0.3, 0.1, true)]
        [InlineData(0.5, 0.3, 0.3, false)]   // totals 1.10
        [InlineData(0.4, 0.3, 0.2, false)]   // totals 0.90
        [InlineData(1.2, -0.1, -0.1, false)] // negative weights
        public void Weights_MustTotalOne(double a, double b, double c, bool expected)
        {
            Assert.Equal(expected, ForecastCalculator.WeightsAreValid((decimal)a, (decimal)b, (decimal)c));
        }

        [Fact]
        public void Error_ComputesAbsoluteAndPercentage()
        {
            var (abs, pct) = ForecastCalculator.Error(forecast: 90m, actual: 100m);
            Assert.Equal(10m, abs);
            Assert.Equal(10m, pct);
        }

        [Fact]
        public void Error_ZeroActual_ZeroForecast_IsZeroPercent()
        {
            Assert.Equal(0m, ForecastCalculator.Error(0m, 0m).Percentage);
        }

        [Fact]
        public void Error_ZeroActual_WithForecast_PercentageIsUndefined()
        {
            Assert.Null(ForecastCalculator.Error(5m, 0m).Percentage);
        }

        [Fact]
        public void Summarize_ComputesMaeAndMape()
        {
            ForecastRow Row(int id, decimal forecast, decimal actual)
            {
                var (abs, pct) = ForecastCalculator.Error(forecast, actual);
                return new ForecastRow(id, 1, "Item", 1, "Cat", "pc", "p", DateTime.Today, DateTime.Today,
                    ForecastMethod.WeightedMovingAverage, forecast, actual, abs, pct, new List<decimal>(), false, DateTime.UtcNow);
            }

            // errors: |90-100| = 10 (10%)  and  |130-100| = 30 (30%)  -> MAE 20, MAPE 20%
            var summary = ForecastCalculator.Summarize(new[] { Row(1, 90m, 100m), Row(2, 130m, 100m) });

            Assert.Equal(2, summary.Evaluated);
            Assert.Equal(20m, summary.Mape);
            Assert.Equal(20m, Assert.Single(summary.PerItem).Mae);
        }
    }

    // =====================================================================================
    // Trend direction
    // =====================================================================================
    public class TrendCalculatorTests
    {
        [Fact] public void RisingConsumption_IsIncreasing() =>
            Assert.Equal(TrendDirection.Increasing, TrendCalculator.Direction(new[] { 10m, 20m, 30m, 40m }));

        [Fact] public void FallingConsumption_IsDecreasing() =>
            Assert.Equal(TrendDirection.Decreasing, TrendCalculator.Direction(new[] { 40m, 30m, 20m, 10m }));

        [Fact] public void SteadyConsumption_IsStable() =>
            Assert.Equal(TrendDirection.Stable, TrendCalculator.Direction(new[] { 100m, 102m, 98m, 101m }));

        [Fact] public void WildSwingsWithNoDirection_AreIrregular() =>
            Assert.Equal(TrendDirection.Irregular, TrendCalculator.Direction(new[] { 0m, 100m, 100m, 0m }));

        [Fact] public void NoConsumption_IsStable() =>
            Assert.Equal(TrendDirection.Stable, TrendCalculator.Direction(new[] { 0m, 0m, 0m }));

        [Fact] public void SinglePeriod_IsStable() =>
            Assert.Equal(TrendDirection.Stable, TrendCalculator.Direction(new[] { 50m }));
    }

    // =====================================================================================
    // Movement classification, stock reconstruction
    // =====================================================================================
    public class MovementCalculatorTests
    {
        [Theory]
        [InlineData(2.0, MovementClass.FastMoving)]      // exactly at the fast limit
        [InlineData(5.0, MovementClass.FastMoving)]
        [InlineData(1.0, MovementClass.ModerateMoving)]
        [InlineData(0.5, MovementClass.SlowMoving)]      // exactly at the slow limit
        [InlineData(0.0, MovementClass.SlowMoving)]
        public void Classify_UsesTheThresholds(double turnover, MovementClass expected)
        {
            Assert.Equal(expected, MovementCalculator.Classify((decimal)turnover, fastMin: 2m, slowMax: 0.5m));
        }

        [Fact]
        public void ObsolescenceRisk_NeverUsedItemWithStock_IsHigh()
        {
            var risk = MovementCalculator.ObsolescenceRisk(null, 60, 100m, 0m, 0m, 0m, MovementClass.SlowMoving);
            Assert.Equal(RiskLevel.High, risk);
        }

        [Fact]
        public void ObsolescenceRisk_ActiveItem_IsLow()
        {
            var risk = MovementCalculator.ObsolescenceRisk(3, 60, 100m, 50m, 100m, 110m, MovementClass.FastMoving);
            Assert.Equal(RiskLevel.Low, risk);
        }

        [Fact]
        public void ObsolescenceRisk_ManyMonthsOfStock_AndFadingUse_IsMedium()
        {
            // 40 days since use (+1), 1000 / 5 = 200 days of cover (+2) = 3 points
            var risk = MovementCalculator.ObsolescenceRisk(40, 60, 1000m, 5m, 50m, 50m, MovementClass.ModerateMoving);
            Assert.Equal(RiskLevel.Medium, risk);
        }

        [Fact]
        public void EndOfDayStock_IsRebuiltBackwardsFromToday()
        {
            // today: +50 stock-in, ending at 100.  yesterday: -30 stock-out.
            var h = new ItemHistory { ItemId = 1, Name = "X", CurrentQuantity = 100m };
            h.In[DateTime.Today] = 50m;
            h.Out[DateTime.Today.AddDays(-1)] = 30m;

            var stock = h.EndOfDayStock(DateTime.Today.AddDays(-2), DateTime.Today);

            Assert.Equal(new[] { 80m, 50m, 100m }, stock);
        }

        [Fact]
        public void Measure_ComputesConsumptionFrequencyAndTurnover()
        {
            var h = new ItemHistory { ItemId = 1, Name = "X", CurrentQuantity = 100m };
            h.Usage[DateTime.Today.AddDays(-1)] = 10m;
            h.Usage[DateTime.Today.AddDays(-3)] = 10m;
            h.Usage[DateTime.Today.AddDays(-5)] = 10m;

            // 10-day range, stock constant at 100 (no stock movements)
            var m = MovementCalculator.Measure(h, DateTime.Today.AddDays(-9), DateTime.Today);

            Assert.Equal(30m, m.TotalConsumption);
            Assert.Equal(3, m.Frequency);
            Assert.Equal(3m, m.AverageDailyUsage);
            Assert.Equal(100m, m.AverageInventory);
            Assert.Equal(0.3m, m.TurnoverRate);
        }
    }

    // =====================================================================================
    // Passwords, permissions, navigation, CSV
    // =====================================================================================
    public class SecurityTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("short1")]
        [InlineData("alllettersnodigit")]
        [InlineData("12345678")]
        public void WeakPasswords_AreRejected(string? password) =>
            Assert.NotNull(PasswordPolicy.Validate(password));

        [Theory]
        [InlineData("Passw0rdOK")]
        [InlineData("abcdefg1")]
        public void StrongPasswords_AreAccepted(string password) =>
            Assert.Null(PasswordPolicy.Validate(password));

        [Fact]
        public void Hash_IsSalted_AndNeverContainsThePassword()
        {
            var a = PasswordHasher.Hash("Secret123");
            var b = PasswordHasher.Hash("Secret123");

            Assert.NotEqual(a, b);
            Assert.DoesNotContain("Secret123", a);
        }

        [Fact]
        public void Verify_AcceptsTheRightPassword_AndRejectsOthers()
        {
            var hash = PasswordHasher.Hash("Secret123");

            Assert.True(PasswordHasher.Verify("Secret123", hash));
            Assert.False(PasswordHasher.Verify("secret123", hash));
            Assert.False(PasswordHasher.Verify("Secret123", "not-a-hash"));
            Assert.False(PasswordHasher.Verify("Secret123", string.Empty));
        }

        [Fact]
        public void Administrator_HasEveryPermission()
        {
            var admin = Permissions.ForRole(RoleNames.Administrator);
            Assert.All(Permissions.All, p => Assert.Contains(p, admin));
        }

        [Fact]
        public void InventoryPersonnel_CannotDecideManageUsersSettingsOrReadAudit()
        {
            var inv = Permissions.ForRole(RoleNames.InventoryPersonnel);

            Assert.DoesNotContain(Permissions.RecommendationsDecide, inv);
            Assert.DoesNotContain(Permissions.UsersManage, inv);
            Assert.DoesNotContain(Permissions.SettingsManage, inv);
            Assert.DoesNotContain(Permissions.AuditView, inv);

            Assert.Contains(Permissions.StockInRecord, inv);
            Assert.Contains(Permissions.ItemsManage, inv);
            Assert.Contains(Permissions.AnalyticsView, inv);
            Assert.Contains(Permissions.RecommendationsView, inv);
        }

        [Fact]
        public void Cashier_CanOnlyViewStockAndRecordUsage()
        {
            var cashier = Permissions.ForRole(RoleNames.SalesCashier);

            Assert.Equal(new[] { Permissions.DashboardView, Permissions.InventoryView, Permissions.StockOutRecord }.OrderBy(x => x),
                         cashier.OrderBy(x => x));
        }

        [Fact]
        public void UnknownRole_HasNoPermissions() =>
            Assert.Empty(Permissions.ForRole("Intruder"));

        [Fact]
        public void EveryMenuItem_UsesAKnownPermission_AndHasAUniqueAddress()
        {
            var items = NavCatalog.Groups.SelectMany(g => g.Items).ToList();

            Assert.All(items, i => Assert.Contains(i.Permission, Permissions.All));
            Assert.Equal(items.Count, items.Select(i => i.Href).Distinct().Count());
        }
    }

    public class ReportCsvTests
    {
        private static ReportService NewService() => new(null!, null!, null!, null!, null!, null!, null!, null!);

        [Fact]
        public void Csv_QuotesCommasAndQuotes_AndNeutralisesFormulas()
        {
            var table = new ReportTable("T", "", new[] { "Item", "Note" }, new List<IReadOnlyList<string>>
            {
                new[] { "Tape, 3in", "He said \"hi\"" },
                new[] { "=1+1", "-5" },
                new[] { "-abc", "plain" },
            }, DateTime.Now);

            var lines = NewService().ToCsv(table).Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal("Item,Note", lines[0]);
            Assert.Equal("\"Tape, 3in\",\"He said \"\"hi\"\"\"", lines[1]);
            Assert.Equal("'=1+1,-5", lines[2]);          // formula is defused, a negative number is left alone
            Assert.Equal("'-abc,plain", lines[3]);
        }
    }
}
