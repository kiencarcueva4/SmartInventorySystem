using System;
using System.ComponentModel.DataAnnotations;
using SmartInventorySystem.Domain.Enums;

namespace SmartInventorySystem.Services.Core
{
    // ----------------------------- form inputs (what the user types) -----------------------------

    public class CategoryInput
    {
        public int CategoryId { get; set; }

        [Required(ErrorMessage = "Category name is required.")]
        [MaxLength(100, ErrorMessage = "Name is too long (maximum 100 characters).")]
        public string CategoryName { get; set; } = string.Empty;

        [MaxLength(250, ErrorMessage = "Description is too long (maximum 250 characters).")]
        public string? Description { get; set; }
    }

    public class SupplierInput
    {
        public int SupplierId { get; set; }

        [Required(ErrorMessage = "Supplier name is required.")]
        [MaxLength(150, ErrorMessage = "Name is too long (maximum 150 characters).")]
        public string SupplierName { get; set; } = string.Empty;

        [MaxLength(150)] public string? ContactPerson { get; set; }
        [MaxLength(50)] public string? Phone { get; set; }
        [MaxLength(150)] public string? Email { get; set; }
        [MaxLength(300)] public string? Address { get; set; }

        [Range(0, 365, ErrorMessage = "Lead time must be between 0 and 365 days.")]
        public int LeadTimeDays { get; set; } = 7;
    }

    public class ItemInput
    {
        public int ItemId { get; set; }

        [Required(ErrorMessage = "Item name is required.")]
        [MaxLength(150, ErrorMessage = "Name is too long (maximum 150 characters).")]
        public string ItemName { get; set; } = string.Empty;

        [Required(ErrorMessage = "SKU / item code is required.")]
        [MaxLength(50, ErrorMessage = "SKU is too long (maximum 50 characters).")]
        public string SKU { get; set; } = string.Empty;

        [MaxLength(500)] public string? Description { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Please select a category.")]
        public int CategoryId { get; set; }

        public int? SupplierId { get; set; }

        [Required(ErrorMessage = "Unit of measure is required.")]
        [MaxLength(30, ErrorMessage = "Unit is too long (maximum 30 characters).")]
        public string UnitOfMeasure { get; set; } = string.Empty;

        [Range(0.0, 1e9, ErrorMessage = "Unit cost cannot be negative.")]
        public decimal UnitCost { get; set; }

        [Range(0.0, 1e9, ErrorMessage = "Safety stock cannot be negative.")]
        public decimal SafetyStock { get; set; }

        [Range(0.0, 1e9, ErrorMessage = "Reorder point cannot be negative.")]
        public decimal ReorderPoint { get; set; }

        [Range(0.0, 1e9, ErrorMessage = "Reorder quantity cannot be negative.")]
        public decimal ReorderQuantity { get; set; }
    }

    public class StockInInput
    {
        [Range(1, int.MaxValue, ErrorMessage = "Please select an item.")]
        public int ItemId { get; set; }

        public int? SupplierId { get; set; }

        [Range(0.01, 1e9, ErrorMessage = "Quantity must be greater than zero.")]
        public decimal Quantity { get; set; }

        [Range(0.0, 1e9, ErrorMessage = "Unit cost cannot be negative.")]
        public decimal UnitCost { get; set; }

        [MaxLength(50, ErrorMessage = "Reference number is too long (maximum 50 characters).")]
        public string? ReferenceNumber { get; set; }

        public DateTime Date { get; set; } = DateTime.Today;
    }

    public class StockOutInput
    {
        [Range(1, int.MaxValue, ErrorMessage = "Please select an item.")]
        public int ItemId { get; set; }

        [Range(0.01, 1e9, ErrorMessage = "Quantity must be greater than zero.")]
        public decimal Quantity { get; set; }

        /// <summary>Why the material was used, e.g. "Business Card Printing - Customer #001".</summary>
        [Required(ErrorMessage = "Customer / job reference is required.")]
        [MaxLength(250, ErrorMessage = "Reference is too long (maximum 250 characters).")]
        public string JobReference { get; set; } = string.Empty;

        [MaxLength(50, ErrorMessage = "Reference number is too long (maximum 50 characters).")]
        public string? ReferenceNumber { get; set; }
    }

    // ----------------------------- rows (what the screens show) -----------------------------

    public sealed record CategoryRow(int CategoryId, string Name, string? Description, RecordStatus Status, int ItemCount);

    public sealed record SupplierRow(int SupplierId, string Name, string? ContactPerson, string? Phone, string? Email,
        string? Address, int LeadTimeDays, RecordStatus Status, int ItemCount);

    public sealed record ItemRow(int ItemId, string Name, string Sku, string? Description, int CategoryId, string CategoryName,
        int? SupplierId, string? SupplierName, string Unit, decimal UnitCost, decimal SafetyStock, decimal ReorderPoint,
        decimal ReorderQuantity, RecordStatus Status, decimal QuantityOnHand);

    public sealed record InventoryRow(int ItemId, string ItemName, string Sku, int CategoryId, string CategoryName, string Unit,
        decimal QuantityOnHand, decimal ReorderPoint, decimal SafetyStock, decimal UnitCost,
        MovementClass? Movement, StockStatus Status);

    /// <summary>Lightweight item entry for the Stock-In / Stock-Out drop-downs.</summary>
    public sealed record StockItemOption(int ItemId, string Name, string Sku, string Unit, decimal QuantityOnHand,
        decimal UnitCost, int? SupplierId);

    public sealed record StockInRow(int Id, DateTime Date, string ItemName, string Unit, string? Supplier,
        decimal Quantity, decimal UnitCost, decimal TotalCost, string? Reference, string ReceivedBy);

    public sealed record StockOutRow(int Id, DateTime Date, string ItemName, string Unit, decimal Quantity,
        string? JobReference, string? Reference, string ReleasedBy);
}
