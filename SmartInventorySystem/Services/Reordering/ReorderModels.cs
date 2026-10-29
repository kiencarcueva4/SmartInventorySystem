using System;
using SmartInventorySystem.Domain.Enums;

namespace SmartInventorySystem.Services.Reordering
{
    public sealed record RecommendationRow(
        int Id, int ItemId, string ItemName, string Sku, string CategoryName, string Unit, string? SupplierName,
        decimal CurrentStock, decimal AverageDailyUsage, decimal ForecastedDemand, decimal LeadTimeDemand,
        int LeadTimeDays, decimal SafetyStock, decimal ReorderPoint, decimal MinimumOrderQuantity,
        decimal RecommendedQuantity, UrgencyLevel Urgency, string? Reason,
        RecommendationStatus Status, OwnerDecision Decision, DateTime? DecisionDate, string? DecidedBy,
        DateTime CreatedAt, int? RestockId, RestockStatus? RestockState, decimal? ApprovedQuantity,
        DateTime? ExpectedDelivery);

    public sealed record RestockRow(
        int Id, int RecommendationId, int ItemId, string ItemName, string Unit, string? SupplierName,
        decimal ApprovedQuantity, DateTime? PurchaseDate, DateTime? ExpectedDelivery, RestockStatus Status,
        string? Notes, UrgencyLevel Urgency);

    public class DecisionInput
    {
        public int RecommendationId { get; set; }
        public OwnerDecision Decision { get; set; }
        public decimal ApprovedQuantity { get; set; }
        public DateTime ExpectedDelivery { get; set; } = DateTime.Today;
        public string? Note { get; set; }
    }

    public class RestockUpdateInput
    {
        public int RestockId { get; set; }
        public RestockStatus NewStatus { get; set; }
        public DateTime PurchaseDate { get; set; } = DateTime.Today;
        public DateTime? ExpectedDelivery { get; set; }
        public string? Notes { get; set; }
    }

    public static class ReorderUi
    {
        public static string StatusKind(RecommendationStatus s) => s switch
        {
            RecommendationStatus.Pending => "warn",
            RecommendationStatus.Approved => "info",
            RecommendationStatus.Completed => "ok",
            _ => "muted"
        };

        public static string RestockKind(RestockStatus s) => s switch
        {
            RestockStatus.Planned => "warn",
            RestockStatus.Ordered => "info",
            RestockStatus.Received => "ok",
            _ => "muted"
        };
    }
}
