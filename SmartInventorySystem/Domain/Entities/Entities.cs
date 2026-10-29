using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartInventorySystem.Domain.Enums;

namespace SmartInventorySystem.Domain.Entities
{
    public class Role
    {
        [Key] public int RoleId { get; set; }
        [Required, MaxLength(50)] public string RoleName { get; set; } = string.Empty;
        [MaxLength(250)] public string? Description { get; set; }

        public ICollection<User> Users { get; set; } = new List<User>();
    }

    public class User
    {
        [Key] public int UserId { get; set; }
        public int RoleId { get; set; }
        [Required, MaxLength(50)] public string Username { get; set; } = string.Empty;
        /// <summary>PBKDF2 hash. Never store plain text.</summary>
        [Required, MaxLength(500)] public string PasswordHash { get; set; } = string.Empty;
        [Required, MaxLength(150)] public string FullName { get; set; } = string.Empty;
        public RecordStatus Status { get; set; } = RecordStatus.Active;
        public bool MustChangePassword { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastLoginAt { get; set; }

        public Role Role { get; set; } = null!;
    }

    public class Category
    {
        [Key] public int CategoryId { get; set; }
        [Required, MaxLength(100)] public string CategoryName { get; set; } = string.Empty;
        [MaxLength(250)] public string? Description { get; set; }
        public RecordStatus Status { get; set; } = RecordStatus.Active;

        public ICollection<Item> Items { get; set; } = new List<Item>();
    }

    public class Supplier
    {
        [Key] public int SupplierId { get; set; }
        [Required, MaxLength(150)] public string SupplierName { get; set; } = string.Empty;
        [MaxLength(150)] public string? ContactPerson { get; set; }
        [MaxLength(50)] public string? Phone { get; set; }
        [MaxLength(150)] public string? Email { get; set; }
        [MaxLength(300)] public string? Address { get; set; }
        public int LeadTimeDays { get; set; }
        public RecordStatus Status { get; set; } = RecordStatus.Active;

        public ICollection<Item> Items { get; set; } = new List<Item>();
    }

    public class Item
    {
        [Key] public int ItemId { get; set; }
        public int CategoryId { get; set; }
        [Required, MaxLength(150)] public string ItemName { get; set; } = string.Empty;
        [MaxLength(500)] public string? Description { get; set; }
        [Required, MaxLength(50)] public string SKU { get; set; } = string.Empty;
        [Required, MaxLength(30)] public string UnitOfMeasure { get; set; } = string.Empty;
        public decimal UnitCost { get; set; }
        public decimal SafetyStock { get; set; }
        public decimal ReorderPoint { get; set; }
        /// <summary>Minimum / standard order size.</summary>
        public decimal ReorderQuantity { get; set; }
        /// <summary>Preferred supplier (optional). Lead time comes from here.</summary>
        public int? SupplierId { get; set; }
        public RecordStatus Status { get; set; } = RecordStatus.Active;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public Category Category { get; set; } = null!;
        public Supplier? Supplier { get; set; }
        public Inventory? Inventory { get; set; }
        public ICollection<StockIn> StockIns { get; set; } = new List<StockIn>();
        public ICollection<StockOut> StockOuts { get; set; } = new List<StockOut>();
        public ICollection<UsageTransaction> UsageTransactions { get; set; } = new List<UsageTransaction>();
        public ICollection<MovementClassification> MovementClassifications { get; set; } = new List<MovementClassification>();
        public ICollection<Forecast> Forecasts { get; set; } = new List<Forecast>();
        public ICollection<InventoryTrend> InventoryTrends { get; set; } = new List<InventoryTrend>();
        public ICollection<ReorderRecommendation> ReorderRecommendations { get; set; } = new List<ReorderRecommendation>();
    }

    /// <summary>Current stock level. Exactly one row per Item.</summary>
    public class Inventory
    {
        [Key] public int InventoryId { get; set; }
        public int ItemId { get; set; }
        public decimal QuantityOnHand { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        public Item Item { get; set; } = null!;
    }

    public class StockIn
    {
        [Key] public int StockInId { get; set; }
        public int ItemId { get; set; }
        public int? SupplierId { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitCost { get; set; }
        public decimal TotalCost { get; set; }
        [MaxLength(50)] public string? ReferenceNumber { get; set; }
        public DateTime StockInDate { get; set; } = DateTime.UtcNow;
        public int ReceivedByUserId { get; set; }

        public Item Item { get; set; } = null!;
        public Supplier? Supplier { get; set; }
        public User ReceivedBy { get; set; } = null!;
    }

    public class StockOut
    {
        [Key] public int StockOutId { get; set; }
        public int ItemId { get; set; }
        public decimal Quantity { get; set; }
        [MaxLength(250)] public string? Purpose { get; set; }
        [MaxLength(50)] public string? ReferenceNumber { get; set; }
        public DateTime StockOutDate { get; set; } = DateTime.UtcNow;
        public int ReleasedByUserId { get; set; }

        public Item Item { get; set; } = null!;
        public User ReleasedBy { get; set; } = null!;
    }

    /// <summary>Actual material consumption. Source data for all analytics.</summary>
    public class UsageTransaction
    {
        [Key] public int UsageTransactionId { get; set; }
        public int ItemId { get; set; }
        /// <summary>Links the usage to the stock-out that released the material.</summary>
        public int? StockOutId { get; set; }
        public decimal QuantityUsed { get; set; }
        /// <summary>Customer / job reference, e.g. "Business Card Printing - Customer #001".</summary>
        [MaxLength(250)] public string? JobReference { get; set; }
        public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
        public int RecordedByUserId { get; set; }

        public Item Item { get; set; } = null!;
        public StockOut? StockOut { get; set; }
        public User RecordedBy { get; set; } = null!;
    }

    public class MovementClassification
    {
        [Key] public int MovementClassificationId { get; set; }
        public int ItemId { get; set; }
        [Required, MaxLength(60)] public string EvaluationPeriod { get; set; } = string.Empty;
        public decimal TotalConsumption { get; set; }
        /// <summary>Number of days/transactions with consumption in the period.</summary>
        public int ConsumptionFrequency { get; set; }
        public decimal TurnoverRate { get; set; }
        public decimal AverageDailyUsage { get; set; }
        public MovementClass Classification { get; set; }
        /// <summary>Obsolescence / ageing indicator (Low / Medium / High).</summary>
        public RiskLevel ObsolescenceRisk { get; set; } = RiskLevel.Low;
        public int? DaysSinceLastUse { get; set; }
        public DateTime CalculatedAt { get; set; } = DateTime.UtcNow;

        public Item Item { get; set; } = null!;
    }

    public class Forecast
    {
        [Key] public int ForecastId { get; set; }
        public int ItemId { get; set; }
        /// <summary>Label such as "2026-W41".</summary>
        [Required, MaxLength(60)] public string ForecastPeriod { get; set; } = string.Empty;
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public decimal ForecastValue { get; set; }
        public ForecastMethod ForecastMethod { get; set; } = ForecastMethod.WeightedMovingAverage;
        /// <summary>JSON of the weights / alpha used, so the result is reproducible.</summary>
        [MaxLength(1000)] public string? ForecastParameters { get; set; }
        public decimal? ActualValue { get; set; }
        /// <summary>Absolute error |actual - forecast|.</summary>
        public decimal? ForecastError { get; set; }
        /// <summary>Percentage error (for MAPE).</summary>
        public decimal? PercentageError { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        public Item Item { get; set; } = null!;
    }

    public class InventoryTrend
    {
        [Key] public int TrendId { get; set; }
        public int ItemId { get; set; }
        [Required, MaxLength(60)] public string Period { get; set; } = string.Empty;
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public decimal BeginningStock { get; set; }
        public decimal StockInQuantity { get; set; }
        public decimal StockOutQuantity { get; set; }
        public decimal EndingStock { get; set; }
        public decimal Consumption { get; set; }
        public int StockoutOccurrences { get; set; }
        public bool OverstockIndicator { get; set; }
        public TrendDirection TrendDirection { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        public Item Item { get; set; } = null!;
    }

    public class ReorderRecommendation
    {
        [Key] public int RecommendationId { get; set; }
        public int ItemId { get; set; }
        public decimal CurrentStock { get; set; }
        public decimal AverageDailyUsage { get; set; }
        public decimal ForecastedDemand { get; set; }
        /// <summary>Forecasted daily demand x lead time (kept for explainability).</summary>
        public decimal LeadTimeDemand { get; set; }
        public int LeadTimeDays { get; set; }
        public decimal SafetyStock { get; set; }
        public decimal ReorderPoint { get; set; }
        public decimal RecommendedQuantity { get; set; }
        public UrgencyLevel Urgency { get; set; }
        [MaxLength(1000)] public string? Reason { get; set; }
        public RecommendationStatus Status { get; set; } = RecommendationStatus.Pending;
        public OwnerDecision OwnerDecision { get; set; } = OwnerDecision.Pending;
        public DateTime? DecisionDate { get; set; }
        public int? DecidedByUserId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public Item Item { get; set; } = null!;
        public User? DecidedBy { get; set; }
        public PurchaseRestockRecord? PurchaseRestockRecord { get; set; }
    }

    /// <summary>Created only after the owner approves. The system never orders anything itself.</summary>
    public class PurchaseRestockRecord
    {
        [Key] public int PurchaseRestockId { get; set; }
        public int RecommendationId { get; set; }
        public int ItemId { get; set; }
        public int? SupplierId { get; set; }
        public decimal ApprovedQuantity { get; set; }
        public DateTime? PurchaseDate { get; set; }
        public DateTime? ExpectedDeliveryDate { get; set; }
        public RestockStatus Status { get; set; } = RestockStatus.Planned;
        [MaxLength(500)] public string? Notes { get; set; }

        public ReorderRecommendation Recommendation { get; set; } = null!;
        public Item Item { get; set; } = null!;
        public Supplier? Supplier { get; set; }
    }

    public class Alert
    {
        [Key] public int AlertId { get; set; }
        public int? ItemId { get; set; }
        public AlertType AlertType { get; set; }
        public AlertSeverity Severity { get; set; }
        [Required, MaxLength(500)] public string Message { get; set; } = string.Empty;
        public bool IsAcknowledged { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? AcknowledgedAt { get; set; }
        public int? AcknowledgedByUserId { get; set; }

        public Item? Item { get; set; }
        public User? AcknowledgedBy { get; set; }
    }

    /// <summary>Metadata only. Report content is regenerated from live data on demand.</summary>
    public class Report
    {
        [Key] public int ReportId { get; set; }
        public ReportType ReportType { get; set; }
        public int GeneratedByUserId { get; set; }
        /// <summary>JSON of the filters used (date range, category, ...).</summary>
        [MaxLength(2000)] public string? Parameters { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        public User GeneratedBy { get; set; } = null!;
    }

    public class AuditLog
    {
        [Key] public int AuditLogId { get; set; }
        /// <summary>Null for events with no known user (e.g. failed login, system job).</summary>
        public int? UserId { get; set; }
        [Required, MaxLength(100)] public string Action { get; set; } = string.Empty;
        [MaxLength(100)] public string? EntityName { get; set; }
        [MaxLength(50)] public string? RecordId { get; set; }
        [MaxLength(1000)] public string? Description { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public User? User { get; set; }
    }

    /// <summary>Manually configured substitute materials (never auto-applied).</summary>
    public class ItemSubstitute
    {
        [Key] public int ItemSubstituteId { get; set; }
        public int ItemId { get; set; }
        public int SubstituteItemId { get; set; }
        [MaxLength(250)] public string? Notes { get; set; }

        public Item Item { get; set; } = null!;
        public Item SubstituteItem { get; set; } = null!;
    }

    /// <summary>Key/value business rules so nothing is hard-coded.</summary>
    public class SystemSetting
    {
        [Key] public int SettingId { get; set; }
        [Required, MaxLength(100)] public string SettingKey { get; set; } = string.Empty;
        [Required, MaxLength(500)] public string SettingValue { get; set; } = string.Empty;
        [MaxLength(50)] public string? SettingGroup { get; set; }
        [MaxLength(300)] public string? Description { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
