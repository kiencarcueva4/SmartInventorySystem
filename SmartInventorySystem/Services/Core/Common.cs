using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;

namespace SmartInventorySystem.Services.Core
{
    /// <summary>Outcome of a service call. Error is always a friendly message, never a raw exception.</summary>
    public sealed record ServiceResult(bool Success, string? Error = null, int? Id = null, string? Message = null)
    {
        public static ServiceResult Ok(int? id = null, string? message = null) => new(true, null, id, message);
        public static ServiceResult Fail(string error) => new(false, error);
    }

    public static class ServiceMessages
    {
        public const string SaveFailed = "Unable to save. Please check the entered information and try again.";
        public const string TransactionFailed = "Unable to save the transaction. Please check the entered information and try again.";
        public const string LoadFailed = "Unable to load the data. Please try again.";
    }

    public enum StockStatus { InStock, LowStock, Critical, OutOfStock, Overstock }

    /// <summary>
    /// Stock status rules (the same rules the reorder logic in Phase 7 builds on):
    ///   Out of Stock : quantity is zero or less
    ///   Critical     : quantity is at or below the item's safety stock
    ///   Low Stock    : quantity is at or below the item's reorder point
    ///   Overstock    : quantity is above (reorder point x overstock multiplier from settings)
    ///   In Stock     : everything else
    /// </summary>
    public static class StockStatusCalculator
    {
        public static StockStatus Calculate(decimal quantity, decimal safetyStock, decimal reorderPoint, decimal overstockMultiplier)
        {
            if (quantity <= 0) return StockStatus.OutOfStock;
            if (quantity <= safetyStock) return StockStatus.Critical;
            if (quantity <= reorderPoint) return StockStatus.LowStock;
            if (reorderPoint > 0 && overstockMultiplier > 0 && quantity > reorderPoint * overstockMultiplier)
                return StockStatus.Overstock;
            return StockStatus.InStock;
        }
    }

    /// <summary>Display helpers (labels, badge colours, sort order).</summary>
    public static class StockStatusUi
    {
        public static string Label(StockStatus s) => s switch
        {
            StockStatus.InStock => "In Stock",
            StockStatus.LowStock => "Low Stock",
            StockStatus.Critical => "Critical",
            StockStatus.OutOfStock => "Out of Stock",
            StockStatus.Overstock => "Overstock",
            _ => s.ToString()
        };

        public static string Kind(StockStatus s) => s switch
        {
            StockStatus.InStock => "ok",
            StockStatus.LowStock => "warn",
            StockStatus.Critical => "danger",
            StockStatus.OutOfStock => "danger",
            StockStatus.Overstock => "info",
            _ => "muted"
        };

        /// <summary>Lower = needs attention sooner. Used when sorting by status.</summary>
        public static int Severity(StockStatus s) => s switch
        {
            StockStatus.OutOfStock => 0,
            StockStatus.Critical => 1,
            StockStatus.LowStock => 2,
            StockStatus.InStock => 3,
            StockStatus.Overstock => 4,
            _ => 5
        };

        public static string Movement(MovementClass? m) => m switch
        {
            MovementClass.FastMoving => "Fast-Moving",
            MovementClass.ModerateMoving => "Moderate-Moving",
            MovementClass.SlowMoving => "Slow-Moving",
            _ => "Not analyzed yet"
        };
    }

    public static class Fmt
    {
        public static string Qty(decimal value) => value.ToString("#,##0.##", CultureInfo.CurrentCulture);
        public static string Money(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);

        /// <summary>Dates are stored in UTC; show them in the computer's local time.</summary>
        public static string Local(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    internal static class Text
    {
        public static string Clean(string? value) => (value ?? string.Empty).Trim();
        public static string? NullIfEmpty(string? value)
        {
            var v = (value ?? string.Empty).Trim();
            return v.Length == 0 ? null : v;
        }
    }

    public static class SettingsReader
    {
        public static async Task<decimal> GetDecimalAsync(AppDbContext db, string key, decimal fallback)
        {
            var raw = await db.SystemSettings.AsNoTracking()
                .Where(s => s.SettingKey == key)
                .Select(s => s.SettingValue)
                .FirstOrDefaultAsync();

            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        }
    }
}
