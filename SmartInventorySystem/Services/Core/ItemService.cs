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
    public interface IItemService
    {
        Task<List<ItemRow>> ListAsync();
        Task<ServiceResult> SaveAsync(ItemInput input, int userId);
        Task<ServiceResult> SetActiveAsync(int itemId, bool active, int userId);
        /// <summary>Permanently deletes an item that has no stock history. Items with history must be deactivated instead.</summary>
        Task<ServiceResult> DeleteAsync(int itemId, int userId);
    }

    public sealed class ItemService : IItemService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;

        public ItemService(IDbContextFactory<AppDbContext> factory, IAuditService audit)
        {
            _factory = factory;
            _audit = audit;
        }

        public async Task<List<ItemRow>> ListAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var items = await db.Items.AsNoTracking()
                .Include(i => i.Category)
                .Include(i => i.Supplier)
                .Include(i => i.Inventory)
                .OrderBy(i => i.ItemName)
                .ToListAsync();

            return items.Select(i => new ItemRow(
                i.ItemId, i.ItemName, i.SKU, i.Description, i.CategoryId, i.Category.CategoryName,
                i.SupplierId, i.Supplier?.SupplierName, i.UnitOfMeasure, i.UnitCost, i.SafetyStock,
                i.ReorderPoint, i.ReorderQuantity, i.Status, i.Inventory?.QuantityOnHand ?? 0m)).ToList();
        }

        public async Task<ServiceResult> SaveAsync(ItemInput input, int userId)
        {
            var name = Text.Clean(input.ItemName);
            var sku = Text.Clean(input.SKU).ToUpperInvariant();
            var unit = Text.Clean(input.UnitOfMeasure);
            var description = Text.NullIfEmpty(input.Description);

            if (name.Length == 0) return ServiceResult.Fail("Item name is required.");
            if (name.Length > 150) return ServiceResult.Fail("Item name is too long (maximum 150 characters).");
            if (sku.Length == 0) return ServiceResult.Fail("SKU / item code is required.");
            if (sku.Length > 50) return ServiceResult.Fail("SKU is too long (maximum 50 characters).");
            if (unit.Length == 0) return ServiceResult.Fail("Unit of measure is required.");
            if (unit.Length > 30) return ServiceResult.Fail("Unit of measure is too long (maximum 30 characters).");
            if (description is not null && description.Length > 500) return ServiceResult.Fail("Description is too long (maximum 500 characters).");
            if (input.CategoryId <= 0) return ServiceResult.Fail("Please select a category.");
            if (input.UnitCost < 0) return ServiceResult.Fail("Unit cost cannot be negative.");
            if (input.SafetyStock < 0) return ServiceResult.Fail("Safety stock cannot be negative.");
            if (input.ReorderPoint < 0) return ServiceResult.Fail("Reorder point cannot be negative.");
            if (input.ReorderQuantity < 0) return ServiceResult.Fail("Reorder quantity cannot be negative.");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();

                // SKU uses NOCASE collation, so "a4-001" and "A4-001" count as the same code.
                var duplicateSku = await db.Items.AnyAsync(i => i.SKU == sku && i.ItemId != input.ItemId);
                if (duplicateSku) return ServiceResult.Fail($"The SKU '{sku}' is already used by another item.");

                var isNew = input.ItemId == 0;
                Item item;
                if (isNew)
                {
                    item = new Item();
                }
                else
                {
                    var existing = await db.Items.FirstOrDefaultAsync(i => i.ItemId == input.ItemId);
                    if (existing is null) return ServiceResult.Fail("The item no longer exists.");
                    item = existing;
                }

                // Relationship rules: only ACTIVE categories / suppliers can be newly assigned.
                if (isNew || item.CategoryId != input.CategoryId)
                {
                    var category = await db.Categories.FirstOrDefaultAsync(c => c.CategoryId == input.CategoryId);
                    if (category is null || category.Status != RecordStatus.Active)
                        return ServiceResult.Fail("Please select an active category.");
                }

                if (input.SupplierId is int supplierId && (isNew || item.SupplierId != supplierId))
                {
                    var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.SupplierId == supplierId);
                    if (supplier is null || supplier.Status != RecordStatus.Active)
                        return ServiceResult.Fail("Please select an active supplier.");
                }

                item.ItemName = name;
                item.SKU = sku;
                item.Description = description;
                item.CategoryId = input.CategoryId;
                item.SupplierId = input.SupplierId;
                item.UnitOfMeasure = unit;
                item.UnitCost = Math.Round(input.UnitCost, 2);
                item.SafetyStock = Math.Round(input.SafetyStock, 2);
                item.ReorderPoint = Math.Round(input.ReorderPoint, 2);
                item.ReorderQuantity = Math.Round(input.ReorderQuantity, 2);
                item.UpdatedAt = DateTime.UtcNow;

                if (isNew)
                {
                    // Every item gets its stock row at creation. Stock starts at 0; add stock with Stock-In.
                    item.Inventory = new Inventory { QuantityOnHand = 0m, LastUpdated = DateTime.UtcNow };
                    db.Items.Add(item);
                }

                await db.SaveChangesAsync(); // item + inventory row saved together

                await _audit.LogAsync(userId, isNew ? "Add Item" : "Edit Item", "Item", item.ItemId.ToString(),
                    $"{(isNew ? "Added" : "Updated")} item '{name}' (SKU {sku}).");

                return ServiceResult.Ok(item.ItemId, isNew
                    ? $"Item '{name}' added. Its stock starts at 0 - use Stock-In to add stock."
                    : $"Item '{name}' updated.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Saving item failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }

        public async Task<ServiceResult> DeleteAsync(int itemId, int userId)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var item = await db.Items.Include(i => i.Inventory).FirstOrDefaultAsync(i => i.ItemId == itemId);
                if (item is null) return ServiceResult.Fail("The item no longer exists.");

                // Real business history must never be erased: transactions, recommendations and restocks.
                var blockers = new List<string>();
                if (await db.StockIns.AnyAsync(s => s.ItemId == itemId)) blockers.Add("stock-in records");
                if (await db.StockOuts.AnyAsync(s => s.ItemId == itemId)) blockers.Add("stock-out records");
                if (await db.UsageTransactions.AnyAsync(u => u.ItemId == itemId)) blockers.Add("usage records");
                if (await db.ReorderRecommendations.AnyAsync(r => r.ItemId == itemId)) blockers.Add("reorder recommendations");
                if (await db.PurchaseRestockRecords.AnyAsync(p => p.ItemId == itemId)) blockers.Add("restock records");

                if (blockers.Count > 0)
                    return ServiceResult.Fail($"'{item.ItemName}' cannot be deleted because it has {string.Join(", ", blockers)}. These records must be kept, so deactivate the item instead.");

                if ((item.Inventory?.QuantityOnHand ?? 0m) > 0)
                    return ServiceResult.Fail($"'{item.ItemName}' still has stock on hand. Deactivate it instead of deleting it.");

                // Calculated data (analysis results, alerts, substitute links) goes together with the item.
                db.MovementClassifications.RemoveRange(await db.MovementClassifications.Where(c => c.ItemId == itemId).ToListAsync());
                db.Forecasts.RemoveRange(await db.Forecasts.Where(f => f.ItemId == itemId).ToListAsync());
                db.InventoryTrends.RemoveRange(await db.InventoryTrends.Where(t => t.ItemId == itemId).ToListAsync());
                db.Alerts.RemoveRange(await db.Alerts.Where(a => a.ItemId == itemId).ToListAsync());
                db.ItemSubstitutes.RemoveRange(await db.ItemSubstitutes
                    .Where(l => l.ItemId == itemId || l.SubstituteItemId == itemId).ToListAsync());
                if (item.Inventory is not null) db.Inventories.Remove(item.Inventory);
                db.Items.Remove(item);

                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "Delete Item", "Item", itemId.ToString(),
                    $"Deleted item '{item.ItemName}' (SKU {item.SKU}).");

                return ServiceResult.Ok(itemId, $"Item '{item.ItemName}' deleted.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Deleting the item failed.", ex);
                return ServiceResult.Fail("Unable to delete the item. It may still be used by other records - deactivate it instead.");
            }
        }

        public async Task<ServiceResult> SetActiveAsync(int itemId, bool active, int userId)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var item = await db.Items.FirstOrDefaultAsync(i => i.ItemId == itemId);
                if (item is null) return ServiceResult.Fail("The item no longer exists.");

                if (active)
                {
                    // An item cannot be reactivated while its category or supplier is inactive.
                    var category = await db.Categories.FirstOrDefaultAsync(c => c.CategoryId == item.CategoryId);
                    if (category is null || category.Status != RecordStatus.Active)
                        return ServiceResult.Fail("Activate the item's category first.");

                    if (item.SupplierId is int supplierId)
                    {
                        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.SupplierId == supplierId);
                        if (supplier is null || supplier.Status != RecordStatus.Active)
                            return ServiceResult.Fail("Activate the item's supplier first, or assign a different supplier.");
                    }
                }

                item.Status = active ? RecordStatus.Active : RecordStatus.Inactive;
                item.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, active ? "Activate Item" : "Deactivate Item", "Item", itemId.ToString(),
                    $"{(active ? "Activated" : "Deactivated")} item '{item.ItemName}'.");

                return ServiceResult.Ok(itemId, $"Item '{item.ItemName}' {(active ? "activated" : "deactivated")}.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Changing item status failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }
    }
}
