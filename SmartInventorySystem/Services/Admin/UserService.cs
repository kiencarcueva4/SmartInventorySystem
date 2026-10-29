using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Core;
using SmartInventorySystem.Services.Security;

namespace SmartInventorySystem.Services.Admin
{
    public sealed record UserRow(int UserId, string Username, string FullName, int RoleId, string RoleName,
        RecordStatus Status, bool MustChangePassword, DateTime? LastLoginAt, DateTime CreatedAt);

    public sealed record RoleOption(int RoleId, string Name);

    public class UserInput
    {
        public int UserId { get; set; }

        [Required(ErrorMessage = "Username is required.")]
        [MaxLength(50, ErrorMessage = "Username is too long (maximum 50 characters).")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Full name is required.")]
        [MaxLength(150, ErrorMessage = "Full name is too long (maximum 150 characters).")]
        public string FullName { get; set; } = string.Empty;

        [Range(1, int.MaxValue, ErrorMessage = "Please select a role.")]
        public int RoleId { get; set; }

        /// <summary>Temporary password. Only used when creating a user.</summary>
        public string? Password { get; set; }
    }

    public interface IUserService
    {
        Task<List<UserRow>> ListAsync();
        Task<List<RoleOption>> RolesAsync();
        Task<ServiceResult> SaveAsync(UserInput input, int adminId);
        Task<ServiceResult> ResetPasswordAsync(int userId, string temporaryPassword, int adminId);
        Task<ServiceResult> SetActiveAsync(int userId, bool active, int adminId);
        /// <summary>Deletes an account that has never done anything. Accounts with history must be deactivated (the audit trail is kept).</summary>
        Task<ServiceResult> DeleteAsync(int userId, int adminId);
    }

    public sealed class UserService : IUserService
    {
        private static readonly Regex UsernamePattern = new("^[A-Za-z0-9._-]{3,50}$", RegexOptions.Compiled);

        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;
        private readonly IAuthService _auth;

        public UserService(IDbContextFactory<AppDbContext> factory, IAuditService audit, IAuthService auth)
        {
            _factory = factory;
            _audit = audit;
            _auth = auth;
        }

        private ServiceResult? RequirePermission() =>
            _auth.Can(Permissions.UsersManage) ? null : ServiceResult.Fail("Only the administrator can manage users.");

        public async Task<List<UserRow>> ListAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var users = await db.Users.AsNoTracking().Include(u => u.Role).OrderBy(u => u.Username).ToListAsync();
            return users.Select(u => new UserRow(u.UserId, u.Username, u.FullName, u.RoleId, u.Role.RoleName,
                u.Status, u.MustChangePassword, u.LastLoginAt, u.CreatedAt)).ToList();
        }

        public async Task<List<RoleOption>> RolesAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var roles = await db.Roles.AsNoTracking().OrderBy(r => r.RoleName).ToListAsync();
            return roles.Select(r => new RoleOption(r.RoleId, r.RoleName)).ToList();
        }

        public async Task<ServiceResult> SaveAsync(UserInput input, int adminId)
        {
            var denied = RequirePermission();
            if (denied is not null) return denied;

            var username = Text.Clean(input.Username);
            var fullName = Text.Clean(input.FullName);
            if (fullName.Length == 0) return ServiceResult.Fail("Full name is required.");
            if (fullName.Length > 150) return ServiceResult.Fail("Full name is too long (maximum 150 characters).");
            if (input.RoleId <= 0) return ServiceResult.Fail("Please select a role.");

            var isNew = input.UserId == 0;
            if (isNew)
            {
                if (!UsernamePattern.IsMatch(username))
                    return ServiceResult.Fail("Username must be 3-50 characters: letters, numbers, dot, dash or underscore.");
                var policyError = PasswordPolicy.Validate(input.Password);
                if (policyError is not null) return ServiceResult.Fail(policyError);
            }

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var role = await db.Roles.FirstOrDefaultAsync(r => r.RoleId == input.RoleId);
                if (role is null) return ServiceResult.Fail("The selected role does not exist.");

                User user;
                if (isNew)
                {
                    // Username uses NOCASE collation, so "Maria" and "maria" are the same account.
                    if (await db.Users.AnyAsync(u => u.Username == username))
                        return ServiceResult.Fail($"The username '{username}' is already taken.");

                    var hash = await Task.Run(() => PasswordHasher.Hash(input.Password!));
                    user = new User
                    {
                        Username = username,
                        PasswordHash = hash,
                        MustChangePassword = true,   // the person must choose their own password at first sign-in
                        Status = RecordStatus.Active
                    };
                    db.Users.Add(user);
                }
                else
                {
                    var existing = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == input.UserId);
                    if (existing is null) return ServiceResult.Fail("The user no longer exists.");
                    user = existing;

                    if (user.RoleId != input.RoleId)
                    {
                        if (user.UserId == adminId) return ServiceResult.Fail("You cannot change your own role.");
                        if (user.Role.RoleName == RoleNames.Administrator && user.Status == RecordStatus.Active
                            && await ActiveAdminCountAsync(db) <= 1)
                            return ServiceResult.Fail("This is the only active administrator. Create another administrator first.");
                    }
                }

                user.FullName = fullName;
                user.RoleId = input.RoleId;
                user.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                await _audit.LogAsync(adminId, isNew ? "Add User" : "Edit User", "User", user.UserId.ToString(),
                    $"{(isNew ? "Created" : "Updated")} user '{user.Username}' ({role.RoleName}).");

                return ServiceResult.Ok(user.UserId, isNew
                    ? $"User '{username}' created. They must choose a new password when they first sign in."
                    : $"User '{user.Username}' updated.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Saving the user failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }

        public async Task<ServiceResult> ResetPasswordAsync(int userId, string temporaryPassword, int adminId)
        {
            var denied = RequirePermission();
            if (denied is not null) return denied;

            var policyError = PasswordPolicy.Validate(temporaryPassword);
            if (policyError is not null) return ServiceResult.Fail(policyError);

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
                if (user is null) return ServiceResult.Fail("The user no longer exists.");

                user.PasswordHash = await Task.Run(() => PasswordHasher.Hash(temporaryPassword));
                user.MustChangePassword = true;
                user.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                // The password itself is never written to the audit log.
                await _audit.LogAsync(adminId, "Reset Password", "User", userId.ToString(),
                    $"Password reset for '{user.Username}'. The user must choose a new one at next sign-in.");

                return ServiceResult.Ok(userId, $"Password reset for '{user.Username}'. Give them the temporary password; they must change it when they sign in.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Resetting the password failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }

        public async Task<ServiceResult> SetActiveAsync(int userId, bool active, int adminId)
        {
            var denied = RequirePermission();
            if (denied is not null) return denied;

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == userId);
                if (user is null) return ServiceResult.Fail("The user no longer exists.");

                if (!active)
                {
                    if (user.UserId == adminId) return ServiceResult.Fail("You cannot deactivate your own account.");
                    if (user.Role.RoleName == RoleNames.Administrator && user.Status == RecordStatus.Active
                        && await ActiveAdminCountAsync(db) <= 1)
                        return ServiceResult.Fail("This is the only active administrator and cannot be deactivated.");
                }

                user.Status = active ? RecordStatus.Active : RecordStatus.Inactive;
                user.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                await _audit.LogAsync(adminId, active ? "Activate User" : "Deactivate User", "User", userId.ToString(),
                    $"{(active ? "Activated" : "Deactivated")} user '{user.Username}'.");

                return ServiceResult.Ok(userId, $"User '{user.Username}' {(active ? "activated" : "deactivated")}.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Changing the user status failed.", ex);
                return ServiceResult.Fail(ServiceMessages.SaveFailed);
            }
        }

        public async Task<ServiceResult> DeleteAsync(int userId, int adminId)
        {
            var denied = RequirePermission();
            if (denied is not null) return denied;

            if (userId == adminId) return ServiceResult.Fail("You cannot delete your own account.");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == userId);
                if (user is null) return ServiceResult.Fail("The user no longer exists.");

                if (user.Role.RoleName == RoleNames.Administrator && user.Status == RecordStatus.Active
                    && await ActiveAdminCountAsync(db) <= 1)
                    return ServiceResult.Fail("This is the only active administrator and cannot be deleted.");

                // An account that appears in the history cannot be erased without damaging the audit trail.
                var history = new List<string>();
                if (await db.AuditLogs.AnyAsync(a => a.UserId == userId)) history.Add("audit log entries (such as signing in)");
                if (await db.StockIns.AnyAsync(s => s.ReceivedByUserId == userId)) history.Add("stock-in records");
                if (await db.StockOuts.AnyAsync(s => s.ReleasedByUserId == userId)) history.Add("stock-out records");
                if (await db.UsageTransactions.AnyAsync(u => u.RecordedByUserId == userId)) history.Add("usage records");
                if (await db.ReorderRecommendations.AnyAsync(r => r.DecidedByUserId == userId)) history.Add("reorder decisions");
                if (await db.Alerts.AnyAsync(a => a.AcknowledgedByUserId == userId)) history.Add("acknowledged alerts");
                if (await db.Reports.AnyAsync(r => r.GeneratedByUserId == userId)) history.Add("generated reports");

                if (history.Count > 0)
                    return ServiceResult.Fail($"'{user.Username}' cannot be deleted because the account has {string.Join(", ", history)}. That history must be kept, so deactivate the account instead.");

                db.Users.Remove(user);
                await db.SaveChangesAsync();

                await _audit.LogAsync(adminId, "Delete User", "User", userId.ToString(), $"Deleted user '{user.Username}'.");
                return ServiceResult.Ok(userId, $"User '{user.Username}' deleted.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Deleting the user failed.", ex);
                return ServiceResult.Fail("Unable to delete the user. Deactivate the account instead.");
            }
        }

        private static async Task<int> ActiveAdminCountAsync(AppDbContext db) =>
            await db.Users.CountAsync(u => u.Status == RecordStatus.Active && u.Role.RoleName == RoleNames.Administrator);
    }
}
