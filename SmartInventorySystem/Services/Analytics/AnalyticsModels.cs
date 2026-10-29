using System;
using System.Collections.Generic;
using SmartInventorySystem.Domain.Enums;

namespace SmartInventorySystem.Services.Analytics
{
    // ------------------------------------------------------------ movement
    public sealed record MovementRow(int ItemId, string ItemName, string Sku, int CategoryId, string CategoryName,
        string Unit, string Period, decimal TotalConsumption, int Frequency, decimal AverageDailyUsage,
        decimal TurnoverRate, MovementClass Classification, RiskLevel Risk, int? DaysSinceLastUse,
        decimal CurrentQuantity, DateTime CalculatedAt);

    public sealed record MovementSnapshot(List<MovementRow> Rows, string? Period, DateTime? CalculatedAt);

    public sealed record MovementSettingsInfo(int EvaluationDays, decimal FastMinTurnover, decimal SlowMaxTurnover,
        int ObsolescenceNoUseDays);

    // ------------------------------------------------------------ forecasting
    public sealed record ForecastRow(int ForecastId, int ItemId, string ItemName, int CategoryId, string CategoryName,
        string Unit, string Period, DateTime PeriodStart, DateTime PeriodEnd, ForecastMethod Method,
        decimal ForecastValue, decimal? Actual, decimal? AbsoluteError, decimal? PercentageError,
        List<decimal> History, bool IsBacktest, DateTime GeneratedAt);

    public sealed record ForecastAccuracyRow(string ItemName, string Unit, int Periods, decimal Mae, decimal? Mape);

    public sealed record ForecastAccuracy(int Evaluated, decimal? Mape, List<ForecastAccuracyRow> PerItem);

    public class ForecastSettingsInput
    {
        public decimal WeightRecent { get; set; } = 0.50m;
        public decimal WeightPrevious { get; set; } = 0.30m;
        public decimal WeightOlder { get; set; } = 0.20m;
        public int PeriodDays { get; set; } = 7;
        public decimal Alpha { get; set; } = 0.30m;

        public decimal WeightTotal => WeightRecent + WeightPrevious + WeightOlder;
    }

    // ------------------------------------------------------------ trends
    public sealed record TrendRow(int TrendId, int ItemId, string ItemName, int CategoryId, string CategoryName,
        string Unit, string Period, DateTime PeriodStart, DateTime PeriodEnd, decimal BeginningStock,
        decimal StockIn, decimal StockOut, decimal EndingStock, decimal Consumption, int StockoutOccurrences,
        bool Overstock, TrendDirection Direction, DateTime GeneratedAt);

    // ------------------------------------------------------------ display helpers
    public static class AnalyticsUi
    {
        public static string Method(ForecastMethod m) => m switch
        {
            ForecastMethod.WeightedMovingAverage => "Weighted Moving Average",
            ForecastMethod.SimpleExponentialSmoothing => "Simple Exponential Smoothing",
            _ => m.ToString()
        };

        public static string Movement(MovementClass c) => c switch
        {
            MovementClass.FastMoving => "FAST-MOVING",
            MovementClass.ModerateMoving => "MODERATE-MOVING",
            _ => "SLOW-MOVING"
        };

        public static string MovementKind(MovementClass c) => c switch
        {
            MovementClass.FastMoving => "ok",
            MovementClass.ModerateMoving => "info",
            _ => "warn"
        };

        public static string RiskKind(RiskLevel r) => r switch
        {
            RiskLevel.High => "danger",
            RiskLevel.Medium => "warn",
            _ => "ok"
        };

        public static string TrendKind(TrendDirection d) => d switch
        {
            TrendDirection.Increasing => "info",
            TrendDirection.Stable => "ok",
            TrendDirection.Decreasing => "warn",
            _ => "muted"
        };
    }
}
