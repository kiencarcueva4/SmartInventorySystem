using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services.Auth;

namespace SmartInventorySystem.Services.Core
{
    public interface IStockService
    {
        Task<ServiceResult> RecordStockInAsync(StockInInput input, int userId);
        Task<ServiceResult> RecordStockOutAsync(StockOutInput input, int userId);
        Task<List<StockInRow>> RecentStockInsAsync(int take = 25);
        Task<List<StockOutRow>> RecentStockOutsAsync(int take = 25);
    }

    /// <summary>
    /// Every stock change saves the transaction row AND the new quantity in ONE SaveChanges call,
    /// which EF Core runs as a single database transaction. Either everything is saved or nothing is,
    /// so Inventory and the Stock-In / Stock-Out / Usage history can never disagree.
    /// </summary>
    public sealed class StockService : IStockService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;

        public StockService(IDbContextFactory<AppDbContext> factory, IAuditService audit)
        {
            _factory = factory;
            _audit = audit;
        }

        // ------------------------------------------------------------------ STOCK-IN
        public async Task<ServiceResult> RecordStockInAsync(StockInInput input, int userId)
        {
            var qty = Math.Round(input.Quantity, 2);
            var unitCost = Math.Round(input.UnitCost, 2);
            var reference = Text.Clean(input.ReferenceNumber);

            if (input.ItemId <= 0) return ServiceResult.Fail("Please select an item.");
            if (qty <= 0) return ServiceResult.Fail("Quantity must be greater than zero.");
            if (unitCost < 0) return ServiceResult.Fail("Unit cost cannot be negative.");
            if (input.Date.Date > DateTime.Today) return ServiceResult.Fail("The stock-in date cannot be in the future.");
            if (reference.Length > 50) return ServiceResult.Fail("Reference number is too long (maximum 50 characters).");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();

                var item = await db.Items.Include(i => i.Inventory).FirstOrDefaultAsync(i => i.ItemId == input.ItemId);
                if (item is null) return ServiceResult.Fail("The selected item was not found.");
                if (item.Status != RecordStatus.Active)
                    return ServiceResult.Fail("This item is inactive. Activate it before recording stock-in.");

                if (input.SupplierId is int supplierId)
                {
                    var supplierOk = await db.Suppliers.AnyAsync(s => s.SupplierId == supplierId && s.Status == RecordStatus.Active);
                    if (!supplierOk) return ServiceResult.Fail("Please select an active supplier.");
                }

                if (reference.Length == 0)
                {
                    var prefix = $"SI-{input.Date:yyyyMMdd}-";
                    var count = await db.StockIns.CountAsync(s => s.ReferenceNumber != null && s.ReferenceNumber.StartsWith(prefix));
                    reference = $"{prefix}{count + 1:D4}";
                }

                // The user picks a day; we keep the current time of day, stored as UTC.
                var when = DateTime.SpecifyKind(input.Date.Date + DateTime.Now.TimeOfDay, DateTimeKind.Local).ToUniversalTime();

                var inventory = item.Inventory;
                if (inventory is null)
                {
                    inventory = new Inventory { QuantityOnHand = 0m };
                    item.Inventory = inventory;
                }

                inventory.QuantityOnHand = Math.Round(inventory.QuantityOnHand + qty, 2);
                inventory.LastUpdated = DateTime.UtcNow;

                db.StockIns.Add(new StockIn
                {
                    ItemId = item.ItemId,
                    SupplierId = input.SupplierId,
                    Quantity = qty,
                    UnitCost = unitCost,
                    TotalCost = Math.Round(qty * unitCost, 2),
                    ReferenceNumber = reference,
                    StockInDate = when,
                    ReceivedByUserId = userId
                });

                await db.SaveChangesAsync();

                var message = $"Stock-in saved: {Fmt.Qty(qty)} {item.UnitOfMeasure} of {item.ItemName}. New stock on hand: {Fmt.Qty(inventory.QuantityOnHand)} {item.UnitOfMeasure}.";
                await _audit.LogAsync(userId, "Stock-In", "Item", item.ItemId.ToString(),
                    $"Received {Fmt.Qty(qty)} {item.UnitOfMeasure} of '{item.ItemName}' (ref {reference}). New stock {Fmt.Qty(inventory.QuantityOnHand)}.");

                return ServiceResult.Ok(item.ItemId, message);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Recording stock-in failed.", ex);
                return ServiceResult.Fail(ServiceMessages.TransactionFailed);
            }
        }

        // ------------------------------------------------------------------ STOCK-OUT
        public async Task<ServiceResult> RecordStockOutAsync(StockOutInput input, int userId)
        {
            var qty = Math.Round(input.Quantity, 2);
            var job = Text.Clean(input.JobReference);
            var reference = Text.Clean(input.ReferenceNumber);

            if (input.ItemId <= 0) return ServiceResult.Fail("Please select an item.");
            if (qty <= 0) return ServiceResult.Fail("Quantity must be greater than zero.");
            if (job.Length == 0) return ServiceResult.Fail("Customer / job reference is required.");
            if (job.Length > 250) return ServiceResult.Fail("Customer / job reference is too long (maximum 250 characters).");
            if (reference.Length > 50) return ServiceResult.Fail("Reference number is too long (maximum 50 characters).");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();

                var item = await db.Items.Include(i => i.Inventory).FirstOrDefaultAsync(i => i.ItemId == input.ItemId);
                if (item is null) return ServiceResult.Fail("The selected item was not found.");
                if (item.Status != RecordStatus.Active)
                    return ServiceResult.Fail("This item is inactive and cannot be used.");

                var inventory = item.Inventory;
                var available = inventory?.QuantityOnHand ?? 0m;

                // Rule: requested quantity must not exceed current stock.
                if (inventory is null || qty > available)
                {
                    return ServiceResult.Fail(
                        $"Insufficient stock for {item.ItemName}. Available: {Fmt.Qty(available)} {item.UnitOfMeasure}, requested: {Fmt.Qty(qty)} {item.UnitOfMeasure}.");
                }

                var now = DateTime.UtcNow;

                if (reference.Length == 0)
                {
                    var prefix = $"SO-{DateTime.Now:yyyyMMdd}-";
                    var count = await db.StockOuts.CountAsync(s => s.ReferenceNumber != null && s.ReferenceNumber.StartsWith(prefix));
                    reference = $"{prefix}{count + 1:D4}";
                }

                inventory.QuantityOnHand = Math.Round(available - qty, 2);
                inventory.LastUpdated = now;

                var stockOut = new StockOut
                {
                    ItemId = item.ItemId,
                    Quantity = qty,
                    Purpose = job,
                    ReferenceNumber = reference,
                    StockOutDate = now,
                    ReleasedByUserId = userId
                };
                db.StockOuts.Add(stockOut);

                // The consumption record that forecasting and movement analysis read later.
                db.UsageTransactions.Add(new UsageTransaction
                {
                    ItemId = item.ItemId,
                    StockOut = stockOut,
                    QuantityUsed = qty,
                    JobReference = job,
                    TransactionDate = now,
                    RecordedByUserId = userId
                });

                await db.SaveChangesAsync();

                var message = $"Stock-out saved: {Fmt.Qty(qty)} {item.UnitOfMeasure} of {item.ItemName} for \"{job}\". Remaining stock: {Fmt.Qty(inventory.QuantityOnHand)} {item.UnitOfMeasure}.";
                await _audit.LogAsync(userId, "Stock-Out", "Item", item.ItemId.ToString(),
                    $"Used {Fmt.Qty(qty)} {item.UnitOfMeasure} of '{item.ItemName}' for '{job}' (ref {reference}). Remaining {Fmt.Qty(inventory.QuantityOnHand)}.");

                return ServiceResult.Ok(item.ItemId, message);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Recording stock-out failed.", ex);
                return ServiceResult.Fail(ServiceMessages.TransactionFailed);
            }
        }

        // ------------------------------------------------------------------ HISTORY
        public async Task<List<StockInRow>> RecentStockInsAsync(int take = 25)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var list = await db.StockIns.AsNoTracking()
                .Include(s => s.Item)
                .Include(s => s.Supplier)
                .Include(s => s.ReceivedBy)
                .OrderByDescending(s => s.StockInDate)
                .Take(take)
                .ToListAsync();

            return list.Select(s => new StockInRow(s.StockInId, s.StockInDate, s.Item.ItemName, s.Item.UnitOfMeasure,
                s.Supplier?.SupplierName, s.Quantity, s.UnitCost, s.TotalCost, s.ReferenceNumber, s.ReceivedBy.FullName)).ToList();
        }

        public async Task<List<StockOutRow>> RecentStockOutsAsync(int take = 25)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var list = await db.StockOuts.AsNoTracking()
                .Include(s => s.Item)
                .Include(s => s.ReleasedBy)
                .OrderByDescending(s => s.StockOutDate)
                .Take(take)
                .ToListAsync();

            return list.Select(s => new StockOutRow(s.StockOutId, s.StockOutDate, s.Item.ItemName, s.Item.UnitOfMeasure,
                s.Quantity, s.Purpose, s.ReferenceNumber, s.ReleasedBy.FullName)).ToList();
        }
    }
}
