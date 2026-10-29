using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;

namespace SmartInventorySystem.Services.Core
{
    public interface IInventoryService
    {
        /// <summary>Active items with current stock, movement class and stock status.</summary>
        Task<List<InventoryRow>> GetMonitoringAsync();

        /// <summary>Active items for the Stock-In / Stock-Out drop-downs.</summary>
        Task<List<StockItemOption>> GetItemOptionsAsync();
    }

    public sealed class InventoryService : IInventoryService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;

        public InventoryService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

        public async Task<List<InventoryRow>> GetMonitoringAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();

            var overstockMultiplier = await SettingsReader.GetDecimalAsync(db, SettingKeys.ReorderOverstockMultiplier, 3m);

            var items = await db.Items.AsNoTracking()
                .Include(i => i.Category)
                .Include(i => i.Inventory)
                .Where(i => i.Status == RecordStatus.Active)
                .OrderBy(i => i.ItemName)
                .ToListAsync();

            // Movement classification is calculated in Phase 6. Until then every item shows "Not analyzed yet".
            var classifications = await db.MovementClassifications.AsNoTracking().ToListAsync();
            var latestByItem = classifications
                .GroupBy(c => c.ItemId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.CalculatedAt).First().Classification);

            return items.Select(i =>
            {
                var qty = i.Inventory?.QuantityOnHand ?? 0m;
                MovementClass? movement = latestByItem.TryGetValue(i.ItemId, out var m) ? m : null;
                return new InventoryRow(
                    i.ItemId, i.ItemName, i.SKU, i.CategoryId, i.Category.CategoryName, i.UnitOfMeasure,
                    qty, i.ReorderPoint, i.SafetyStock, i.UnitCost, movement,
                    StockStatusCalculator.Calculate(qty, i.SafetyStock, i.ReorderPoint, overstockMultiplier));
            }).ToList();
        }

        public async Task<List<StockItemOption>> GetItemOptionsAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var items = await db.Items.AsNoTracking()
                .Include(i => i.Inventory)
                .Where(i => i.Status == RecordStatus.Active)
                .OrderBy(i => i.ItemName)
                .ToListAsync();

            return items.Select(i => new StockItemOption(
                i.ItemId, i.ItemName, i.SKU, i.UnitOfMeasure, i.Inventory?.QuantityOnHand ?? 0m, i.UnitCost, i.SupplierId)).ToList();
        }
    }
}
