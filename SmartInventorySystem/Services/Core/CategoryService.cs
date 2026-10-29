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
    public interface ICategoryService
    {
        Task<List<CategoryRow>> ListAsync();
        Task<ServiceResult> SaveAsync(CategoryInput input, int userId);
        Task<ServiceResult> SetActiveAsync(int categoryId, bool active, int userId);
        /// <summary>Deletes a category that has no items at all.</summary>
        Task<ServiceResult> DeleteAsync(int categoryId, int userId);
    }

    public sealed class CategoryService : ICategoryService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;

        public CategoryService(IDbContextFactory<AppDbContext> factory, IAuditService audit)
        {
            _factory = factory;
            _audit = audit;
        }

        public async Task<List<CategoryRow>> ListAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var rows = await db.Categories.AsNoTracking()
                .OrderBy(c => c.CategoryName)
                .Select(c => new { c.CategoryId, c.CategoryName, c.Description, c.Status, Count = c.Items.Count() })
                .ToListAsync();

            return rows.Select(r => new CategoryRow(r.CategoryId, r.CategoryName, r.Description, r.Status, r.Count)).ToList();
        }

        public async Task<ServiceResult> SaveAsync(CategoryInput input, int userId)
        {
            var name = Text.Clean(input.CategoryName);
            var description = Text.NullIfEmpty(input.Description);

            if (name.Length == 0) return ServiceResult.Fail("Category name is required.");
            if (name.Length > 100) return ServiceResult.Fail("Category name is too long (maximum 100 characters).");
            if (description is not null && description.Length > 250)
                return ServiceResult.Fail("Description is too long (maximum 250 characters).");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();

                // CategoryName uses NOCASE collation, so this check ignores upper/lower case.
                var duplicate = await db.Categories.AnyAsync(c => c.CategoryName == name && c.CategoryId != input.CategoryId);
                if (duplicate) return ServiceResult.Fail($"A category named '{name}' already exists.");

                var isNew = input.CategoryId == 0;
                Category category;
                if (isNew)
                {
                    category = new Category();
                    db.Categories.Add(category);
                }
                else
                {
                    var existing = await db.Categories.FirstOrDefaultAsync(c => c.CategoryId == input.CategoryId);
                    if (existing is null) return ServiceResult.Fail("The category no longer exists.");
                    category = existing;
                }

                category.CategoryName = name;
                category.Description = description;
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, isNew ? "Add Category" : "Edit Category", "Category",
                    category.CategoryId.ToString(), $"{(isNew ? "Added" : "Updated")} category '{name}'.");

                return ServiceResult.Ok(category.CategoryId, isNew ? $"Category '{name}' added." : $"Category '{name}' updated.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Saving category failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }

        public async Task<ServiceResult> DeleteAsync(int categoryId, int userId)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var category = await db.Categories.FirstOrDefaultAsync(c => c.CategoryId == categoryId);
                if (category is null) return ServiceResult.Fail("The category no longer exists.");

                var itemCount = await db.Items.CountAsync(i => i.CategoryId == categoryId);
                if (itemCount > 0)
                    return ServiceResult.Fail($"Cannot delete '{category.CategoryName}': it still has {itemCount} item(s). Delete or move those items first, or deactivate the category.");

                db.Categories.Remove(category);
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, "Delete Category", "Category", categoryId.ToString(),
                    $"Deleted category '{category.CategoryName}'.");

                return ServiceResult.Ok(categoryId, $"Category '{category.CategoryName}' deleted.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Deleting the category failed.", ex);
                return ServiceResult.Fail("Unable to delete the category. Deactivate it instead.");
            }
        }

        public async Task<ServiceResult> SetActiveAsync(int categoryId, bool active, int userId)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var category = await db.Categories.FirstOrDefaultAsync(c => c.CategoryId == categoryId);
                if (category is null) return ServiceResult.Fail("The category no longer exists.");

                if (!active)
                {
                    var activeItems = await db.Items.CountAsync(i => i.CategoryId == categoryId && i.Status == RecordStatus.Active);
                    if (activeItems > 0)
                        return ServiceResult.Fail($"Cannot deactivate '{category.CategoryName}': it still has {activeItems} active item(s). Move or deactivate those items first.");
                }

                category.Status = active ? RecordStatus.Active : RecordStatus.Inactive;
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, active ? "Activate Category" : "Deactivate Category", "Category",
                    categoryId.ToString(), $"{(active ? "Activated" : "Deactivated")} category '{category.CategoryName}'.");

                return ServiceResult.Ok(categoryId, $"Category '{category.CategoryName}' {(active ? "activated" : "deactivated")}.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Changing category status failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }
    }
}
