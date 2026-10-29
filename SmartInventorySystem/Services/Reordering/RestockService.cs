using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Core;

namespace SmartInventorySystem.Services.Reordering
{
    public interface IRestockService
    {
        Task<List<RestockRow>> ListAsync();
        Task<ServiceResult> UpdateAsync(RestockUpdateInput input, int userId);
    }

    /// <summary>
    /// Tracks an approved restock: Planned -> Ordered -> Received (or Cancelled).
    /// The system never contacts a supplier. "Ordered" is set by a person after they placed the order.
    /// Marking a restock Received does NOT change stock - the delivery is recorded on the Stock-In page.
    /// </summary>
    public sealed class RestockService : IRestockService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;
        private readonly IAuthService _auth;

        public RestockService(IDbContextFactory<AppDbContext> factory, IAuditService audit, IAuthService auth)
        {
            _factory = factory;
            _audit = audit;
            _auth = auth;
        }

        public async Task<List<RestockRow>> ListAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var list = await db.PurchaseRestockRecords.AsNoTracking()
                .Include(p => p.Item)
                .Include(p => p.Supplier)
                .Include(p => p.Recommendation)
                .ToListAsync();

            return list
                .OrderBy(p => p.Status == RestockStatus.Received || p.Status == RestockStatus.Cancelled ? 1 : 0)
                .ThenBy(p => p.ExpectedDeliveryDate ?? DateTime.MaxValue)
                .Select(p => new RestockRow(p.PurchaseRestockId, p.RecommendationId, p.ItemId, p.Item.ItemName,
                    p.Item.UnitOfMeasure, p.Supplier?.SupplierName, p.ApprovedQuantity, p.PurchaseDate,
                    p.ExpectedDeliveryDate, p.Status, p.Notes, p.Recommendation.Urgency))
                .ToList();
        }

        public async Task<ServiceResult> UpdateAsync(RestockUpdateInput input, int userId)
        {
            if (!_auth.Can(Permissions.RestockManage))
                return ServiceResult.Fail("You do not have permission to update restock records.");

            var notes = Text.NullIfEmpty(input.Notes);
            if (notes is not null && notes.Length > 500) return ServiceResult.Fail("The notes are too long (maximum 500 characters).");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var restock = await db.PurchaseRestockRecords
                    .Include(p => p.Item)
                    .Include(p => p.Recommendation)
                    .FirstOrDefaultAsync(p => p.PurchaseRestockId == input.RestockId);
                if (restock is null) return ServiceResult.Fail("The restock record no longer exists.");

                var allowed = restock.Status switch
                {
                    RestockStatus.Planned => new[] { RestockStatus.Ordered, RestockStatus.Cancelled },
                    RestockStatus.Ordered => new[] { RestockStatus.Received, RestockStatus.Cancelled },
                    _ => Array.Empty<RestockStatus>()
                };
                if (!allowed.Contains(input.NewStatus))
                    return ServiceResult.Fail($"A restock that is {restock.Status.ToString().ToLowerInvariant()} cannot be changed to {input.NewStatus.ToString().ToLowerInvariant()}.");

                string message;
                switch (input.NewStatus)
                {
                    case RestockStatus.Ordered:
                        if (input.PurchaseDate.Date > DateTime.Today) return ServiceResult.Fail("The purchase date cannot be in the future.");
                        if (input.ExpectedDelivery is DateTime expected && expected.Date < input.PurchaseDate.Date)
                            return ServiceResult.Fail("The expected delivery cannot be before the purchase date.");
                        restock.PurchaseDate = input.PurchaseDate.Date;
                        if (input.ExpectedDelivery is not null) restock.ExpectedDeliveryDate = input.ExpectedDelivery.Value.Date;
                        message = "Marked as ordered.";
                        break;

                    case RestockStatus.Received:
                        restock.Recommendation.Status = RecommendationStatus.Completed;
                        message = "Marked as received. Remember to record the delivery on the Stock-In page so your stock levels update.";
                        break;

                    default: // Cancelled
                        restock.Recommendation.Status = RecommendationStatus.Dismissed;
                        message = "Restock cancelled.";
                        break;
                }

                restock.Status = input.NewStatus;
                if (notes is not null) restock.Notes = notes;
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "Restock Update", "PurchaseRestockRecord", restock.PurchaseRestockId.ToString(),
                    $"{restock.Item.ItemName}: restock {input.NewStatus.ToString().ToLowerInvariant()}.");

                return ServiceResult.Ok(restock.PurchaseRestockId, message);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Updating the restock record failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }
    }
}
