using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services.Auth;

namespace SmartInventorySystem.Services.Core
{
    public interface ISupplierService
    {
        Task<List<SupplierRow>> ListAsync();
        Task<ServiceResult> SaveAsync(SupplierInput input, int userId);
        Task<ServiceResult> SetActiveAsync(int supplierId, bool active, int userId);
    }

    public sealed class SupplierService : ISupplierService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;

        public SupplierService(IDbContextFactory<AppDbContext> factory, IAuditService audit)
        {
            _factory = factory;
            _audit = audit;
        }

        public async Task<List<SupplierRow>> ListAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var rows = await db.Suppliers.AsNoTracking()
                .OrderBy(s => s.SupplierName)
                .Select(s => new { s.SupplierId, s.SupplierName, s.ContactPerson, s.Phone, s.Email, s.Address, s.LeadTimeDays, s.Status, Count = s.Items.Count() })
                .ToListAsync();

            return rows.Select(r => new SupplierRow(r.SupplierId, r.SupplierName, r.ContactPerson, r.Phone, r.Email,
                r.Address, r.LeadTimeDays, r.Status, r.Count)).ToList();
        }

        public async Task<ServiceResult> SaveAsync(SupplierInput input, int userId)
        {
            var name = Text.Clean(input.SupplierName);
            var contact = Text.NullIfEmpty(input.ContactPerson);
            var phone = Text.NullIfEmpty(input.Phone);
            var email = Text.NullIfEmpty(input.Email);
            var address = Text.NullIfEmpty(input.Address);

            if (name.Length == 0) return ServiceResult.Fail("Supplier name is required.");
            if (name.Length > 150) return ServiceResult.Fail("Supplier name is too long (maximum 150 characters).");
            if (input.LeadTimeDays < 0 || input.LeadTimeDays > 365)
                return ServiceResult.Fail("Lead time must be between 0 and 365 days.");
            if (email is not null && !IsValidEmail(email)) return ServiceResult.Fail("Please enter a valid email address.");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();

                var duplicate = await db.Suppliers.AnyAsync(s => s.SupplierName == name && s.SupplierId != input.SupplierId);
                if (duplicate) return ServiceResult.Fail($"A supplier named '{name}' already exists.");

                var isNew = input.SupplierId == 0;
                Supplier supplier;
                if (isNew)
                {
                    supplier = new Supplier();
                    db.Suppliers.Add(supplier);
                }
                else
                {
                    var existing = await db.Suppliers.FirstOrDefaultAsync(s => s.SupplierId == input.SupplierId);
                    if (existing is null) return ServiceResult.Fail("The supplier no longer exists.");
                    supplier = existing;
                }

                supplier.SupplierName = name;
                supplier.ContactPerson = contact;
                supplier.Phone = phone;
                supplier.Email = email;
                supplier.Address = address;
                supplier.LeadTimeDays = input.LeadTimeDays;
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, isNew ? "Add Supplier" : "Edit Supplier", "Supplier",
                    supplier.SupplierId.ToString(), $"{(isNew ? "Added" : "Updated")} supplier '{name}' (lead time {input.LeadTimeDays} days).");

                return ServiceResult.Ok(supplier.SupplierId, isNew ? $"Supplier '{name}' added." : $"Supplier '{name}' updated.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Saving supplier failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }

        public async Task<ServiceResult> SetActiveAsync(int supplierId, bool active, int userId)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.SupplierId == supplierId);
                if (supplier is null) return ServiceResult.Fail("The supplier no longer exists.");

                if (!active)
                {
                    var activeItems = await db.Items.CountAsync(i => i.SupplierId == supplierId && i.Status == RecordStatus.Active);
                    if (activeItems > 0)
                        return ServiceResult.Fail($"Cannot deactivate '{supplier.SupplierName}': {activeItems} active item(s) still use this supplier. Reassign those items first.");
                }

                supplier.Status = active ? RecordStatus.Active : RecordStatus.Inactive;
                await db.SaveChangesAsync();

                await _audit.LogAsync(userId, active ? "Activate Supplier" : "Deactivate Supplier", "Supplier",
                    supplierId.ToString(), $"{(active ? "Activated" : "Deactivated")} supplier '{supplier.SupplierName}'.");

                return ServiceResult.Ok(supplierId, $"Supplier '{supplier.SupplierName}' {(active ? "activated" : "deactivated")}.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Changing supplier status failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }

        private static bool IsValidEmail(string email)
        {
            try
            {
                var address = new MailAddress(email);
                return address.Address == email;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
