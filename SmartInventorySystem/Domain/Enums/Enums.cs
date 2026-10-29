using System;

namespace SmartInventorySystem.Domain.Enums
{
    public enum RecordStatus { Active, Inactive }

    public enum MovementClass { FastMoving, ModerateMoving, SlowMoving }

    public enum RiskLevel { Low, Medium, High }

    public enum ForecastMethod { WeightedMovingAverage, SimpleExponentialSmoothing }

    public enum TrendDirection { Increasing, Stable, Decreasing, Irregular }

    public enum UrgencyLevel { NotYetNecessary, Recommended, Urgent }

    /// <summary>What the owner chose.</summary>
    public enum OwnerDecision { Pending, Approved, Deferred, Dismissed }

    /// <summary>Lifecycle of a recommendation. Completed = restock received.</summary>
    public enum RecommendationStatus { Pending, Approved, Deferred, Dismissed, Completed }

    public enum RestockStatus { Planned, Ordered, Received, Cancelled }

    public enum AlertType
    {
        LowStock, UrgentReorder, StockoutRisk, Overstock,
        SlowMovingItem, ObsolescenceRisk, ForecastWarning
    }

    public enum AlertSeverity { Info, Warning, Critical }

    public enum ReportType
    {
        InventoryStatus, MovementClassification, DemandForecast,
        InventoryTrends, ReorderHistory
    }
}
