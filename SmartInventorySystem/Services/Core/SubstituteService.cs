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
    public sealed record SubstituteSuggestion(int ItemId, string Name, string Sku, string Unit, string CategoryName,
        decimal Available, bool Configured, string? Notes);

    public sealed record SubstituteLink(int LinkId, int SubstituteItemId, string Name, string Sku, string Unit,
        decimal Available, string? Notes);

    public interface ISubstituteService
    {
        /// <summary>Alternatives that have stock: manually linked items first, then other items of the same category.</summary>
        Task<List<SubstituteSuggestion>> GetSuggestionsAsync(int itemId);
        Task<List<SubstituteLink>> GetLinksAsync(int itemId);
        Task<ServiceResult> AddAsync(int itemId, int substituteItemId, string? notes, int userId);
        Task<ServiceResult> RemoveAsync(int linkId, int userId);
    }

    /// <summary>The system only SUGGESTS substitutes. It never swaps a material by itself.</summary>
    public sealed class SubstituteService : ISubstituteService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;
        private readonly IAuthService _auth;

        public SubstituteService(IDbContextFactory<AppDbContext> factory, IAuditService audit, IAuthService auth)
        {
            _factory = factory;
            _audit = audit;
            _auth = auth;
        }

        public async Task<List<SubstituteSuggestion>> GetSuggestionsAsync(int itemId)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ItemId == itemId);
            if (item is null) return new List<SubstituteSuggestion>();

            var links = await db.ItemSubstitutes.AsNoTracking().Where(l => l.ItemId == itemId).ToListAsync();
            var linkedIds = links.Select(l => l.SubstituteItemId).ToHashSet();

            var candidates = await db.Items.AsNoTracking()
                .Include(i => i.Category)
                .Include(i => i.Inventory)
                .Where(i => i.Status == RecordStatus.Active && i.ItemId != itemId
                            && (i.CategoryId == item.CategoryId || linkedIds.Contains(i.ItemId)))
                .ToListAsync();

            return candidates
                .Where(i => (i.Inventory?.QuantityOnHand ?? 0m) > 0)
                .Select(i => new SubstituteSuggestion(i.ItemId, i.ItemName, i.SKU, i.UnitOfMeasure, i.Category.CategoryName,
                    i.Inventory!.QuantityOnHand, linkedIds.Contains(i.ItemId),
                    links.FirstOrDefault(l => l.SubstituteItemId == i.ItemId)?.Notes))
                .OrderByDescending(s => s.Configured)
                .ThenByDescending(s => s.Available)
                .ToList();
        }

        public async Task<List<SubstituteLink>> GetLinksAsync(int itemId)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var links = await db.ItemSubstitutes.AsNoTracking()
                .Include(l => l.SubstituteItem).ThenInclude(i => i.Inventory)
                .Where(l => l.ItemId == itemId)
                .ToListAsync();

            return links
                .OrderBy(l => l.SubstituteItem.ItemName)
                .Select(l => new SubstituteLink(l.ItemSubstituteId, l.SubstituteItemId, l.SubstituteItem.ItemName,
                    l.SubstituteItem.SKU, l.SubstituteItem.UnitOfMeasure,
                    l.SubstituteItem.Inventory?.QuantityOnHand ?? 0m, l.Notes))
                .ToList();
        }

        public async Task<ServiceResult> AddAsync(int itemId, int substituteItemId, string? notes, int userId)
        {
            if (!_auth.Can(Permissions.ItemsManage)) return ServiceResult.Fail("You do not have permission to manage substitutes.");
            if (itemId == substituteItemId) return ServiceResult.Fail("An item cannot be its own substitute.");
            if (substituteItemId <= 0) return ServiceResult.Fail("Please select a substitute item.");

            var note = Text.NullIfEmpty(notes);
            if (note is not null && note.Length > 250) return ServiceResult.Fail("The note is too long (maximum 250 characters).");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var item = await db.Items.FirstOrDefaultAsync(i => i.ItemId == itemId);
                var substitute = await db.Items.FirstOrDefaultAsync(i => i.ItemId == substituteItemId);
                if (item is null || substitute is null) return ServiceResult.Fail("The item no longer exists.");
                if (substitute.Status != RecordStatus.Active) return ServiceResult.Fail("The substitute item is inactive.");

                if (await db.ItemSubstitutes.AnyAsync(l => l.ItemId == itemId && l.SubstituteItemId == substituteItemId))
                    return ServiceResult.Fail($"'{substitute.ItemName}' is already a substitute for '{item.ItemName}'.");

                db.ItemSubstitutes.Add(new ItemSubstitute { ItemId = itemId, SubstituteItemId = substituteItemId, Notes = note });
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "Add Substitute", "Item", itemId.ToString(),
                    $"'{substitute.ItemName}' can substitute '{item.ItemName}'.");
                return ServiceResult.Ok(itemId, $"'{substitute.ItemName}' added as a substitute.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Adding a substitute failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }

        public async Task<ServiceResult> RemoveAsync(int linkId, int userId)
        {
            if (!_auth.Can(Permissions.ItemsManage)) return ServiceResult.Fail("You do not have permission to manage substitutes.");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var link = await db.ItemSubstitutes
                    .Include(l => l.Item).Include(l => l.SubstituteItem)
                    .FirstOrDefaultAsync(l => l.ItemSubstituteId == linkId);
                if (link is null) return ServiceResult.Fail("The substitute link no longer exists.");

                db.ItemSubstitutes.Remove(link);
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "Remove Substitute", "Item", link.ItemId.ToString(),
                    $"'{link.SubstituteItem.ItemName}' is no longer a substitute for '{link.Item.ItemName}'.");
                return ServiceResult.Ok(link.ItemId, "Substitute removed.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Removing a substitute failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }
    }
}
