using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Services;
using SmartInventorySystem.Services.Admin;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Security;
using Xunit;

namespace SmartInventorySystem.Tests
{
    // =====================================================================================
    // Login, logout, lockout, password change
    // =====================================================================================
    public class AuthServiceTests : IAsyncLifetime
    {
        private World _w = null!;
        private AuthService _auth = null!;

        public async Task InitializeAsync()
        {
            _w = await World.CreateAsync();
            _auth = new AuthService(_w.Db, _w.Audit, new AppAuthStateProvider());
        }

        public Task DisposeAsync()
        {
            _w.Dispose();
            return Task.CompletedTask;
        }

        [Fact]
        public async Task ValidLogin_SignsIn_WithTheRightRoleAndPermissions()
        {
            var r = await _auth.LoginAsync("admin", World.Password);

            Assert.True(r.Success, r.Error);
            Assert.Equal(RoleNames.Administrator, _auth.Current!.RoleName);
            Assert.True(_auth.Can(Permissions.UsersManage));
            Assert.True(await _w.AuditContainsAsync("Login"));
        }

        [Fact]
        public async Task Username_IsNotCaseSensitive()
        {
            Assert.True((await _auth.LoginAsync("ADMIN", World.Password)).Success);
        }

        [Fact]
        public async Task CashierLogin_HasOnlyCashierPermissions()
        {
            await _auth.LoginAsync("cashier", World.Password);

            Assert.True(_auth.Can(Permissions.StockOutRecord));
            Assert.False(_auth.Can(Permissions.StockInRecord));
            Assert.False(_auth.Can(Permissions.UsersManage));
        }

        [Fact]
        public async Task WrongPassword_IsRejected_WithAGenericMessage_AndAudited()
        {
            var r = await _auth.LoginAsync("admin", "WrongPass1");

            Assert.False(r.Success);
            Assert.Equal("Invalid username or password.", r.Error);
            Assert.Null(_auth.Current);
            Assert.True(await _w.AuditContainsAsync("Login Failed"));
        }

        [Fact]
        public async Task UnknownUser_GetsTheSameMessageAsAWrongPassword()
        {
            var unknown = await _auth.LoginAsync("nobody", "WrongPass1");
            var wrong = await _auth.LoginAsync("admin", "WrongPass1");

            Assert.Equal(wrong.Error, unknown.Error);
        }

        [Fact]
        public async Task EmptyFields_AreRejected()
        {
            Assert.False((await _auth.LoginAsync("", "")).Success);
            Assert.False((await _auth.LoginAsync("admin", "")).Success);
        }

        [Fact]
        public async Task InactiveAccount_CannotSignIn()
        {
            await using (var db = _w.Db.CreateDbContext())
            {
                (await db.Users.SingleAsync(u => u.Username == "cashier")).Status = RecordStatus.Inactive;
                await db.SaveChangesAsync();
            }

            var r = await _auth.LoginAsync("cashier", World.Password);

            Assert.False(r.Success);
            Assert.Contains("inactive", r.Error);
            Assert.Null(_auth.Current);
        }

        [Fact]
        public async Task FiveWrongPasswords_LockTheAccountTemporarily()
        {
            for (var i = 0; i < 5; i++) await _auth.LoginAsync("admin", "WrongPass1");

            var r = await _auth.LoginAsync("admin", World.Password); // even the right password is refused while locked

            Assert.False(r.Success);
            Assert.Contains("Too many", r.Error);
        }

        [Fact]
        public async Task Logout_ClearsTheSession_AndIsAudited()
        {
            await _auth.LoginAsync("admin", World.Password);

            await _auth.LogoutAsync();

            Assert.Null(_auth.Current);
            Assert.False(_auth.Can(Permissions.DashboardView));
            Assert.True(await _w.AuditContainsAsync("Logout"));
        }

        [Fact]
        public async Task FlaggedAccount_MustChangePasswordAtFirstLogin()
        {
            await using (var db = _w.Db.CreateDbContext())
            {
                (await db.Users.SingleAsync(u => u.Username == "cashier")).MustChangePassword = true;
                await db.SaveChangesAsync();
            }

            var r = await _auth.LoginAsync("cashier", World.Password);

            Assert.True(r.Success);
            Assert.True(r.MustChangePassword);
        }

        [Fact]
        public async Task ChangePassword_EnforcesThePolicy_AndTheCurrentPassword()
        {
            await _auth.LoginAsync("admin", World.Password);

            Assert.False((await _auth.ChangePasswordAsync(World.Password, "short1")).Success);              // too weak
            Assert.False((await _auth.ChangePasswordAsync("WrongCurrent1", "BrandNew123")).Success);         // wrong current
            Assert.False((await _auth.ChangePasswordAsync(World.Password, World.Password)).Success);         // same as current
        }

        [Fact]
        public async Task ChangePassword_Works_AndTheOldPasswordStopsWorking()
        {
            await _auth.LoginAsync("admin", World.Password);

            var change = await _auth.ChangePasswordAsync(World.Password, "BrandNew123");
            Assert.True(change.Success, change.Error);
            Assert.True(await _w.AuditContainsAsync("Change Password"));

            await _auth.LogoutAsync();
            Assert.False((await _auth.LoginAsync("admin", World.Password)).Success);
            Assert.True((await _auth.LoginAsync("admin", "BrandNew123")).Success);
        }

        [Fact]
        public async Task StoredPassword_IsAHash_NotThePassword()
        {
            await using var db = _w.Db.CreateDbContext();
            var user = await db.Users.SingleAsync(u => u.Username == "admin");

            Assert.NotEqual(World.Password, user.PasswordHash);
            Assert.StartsWith("PBKDF2$", user.PasswordHash);
        }
    }

    // =====================================================================================
    // User management
    // =====================================================================================
    public class UserServiceTests : IAsyncLifetime
    {
        private World _w = null!;
        private UserService _users = null!;
        private int _inventoryRoleId;
        private int _adminRoleId;

        public async Task InitializeAsync()
        {
            _w = await World.CreateAsync();
            _users = new UserService(_w.Db, _w.Audit, _w.Auth);

            var roles = await _users.RolesAsync();
            _inventoryRoleId = roles.Single(r => r.Name == RoleNames.InventoryPersonnel).RoleId;
            _adminRoleId = roles.Single(r => r.Name == RoleNames.Administrator).RoleId;
        }

        public Task DisposeAsync()
        {
            _w.Dispose();
            return Task.CompletedTask;
        }

        private UserInput NewUser(string username = "maria") => new()
        {
            Username = username, FullName = "Maria Santos", RoleId = _inventoryRoleId, Password = "Temp12345"
        };

        [Fact]
        public async Task NewUser_MustChangePassword_AndIsStoredHashed()
        {
            var r = await _users.SaveAsync(NewUser(), _w.AdminId);
            Assert.True(r.Success, r.Error);

            var row = (await _users.ListAsync()).Single(u => u.Username == "maria");
            Assert.True(row.MustChangePassword);
            Assert.Equal(RecordStatus.Active, row.Status);

            await using var db = _w.Db.CreateDbContext();
            var user = await db.Users.SingleAsync(u => u.Username == "maria");
            Assert.True(PasswordHasher.Verify("Temp12345", user.PasswordHash));
            Assert.True(await _w.AuditContainsAsync("Add User"));
        }

        [Fact]
        public async Task DuplicateUsername_IsRejected_IgnoringCase()
        {
            var r = await _users.SaveAsync(NewUser("ADMIN"), _w.AdminId);

            Assert.False(r.Success);
            Assert.Contains("taken", r.Error);
        }

        [Theory]
        [InlineData("ab")]
        [InlineData("has space")]
        [InlineData("bad/char")]
        public async Task InvalidUsernames_AreRejected(string username)
        {
            Assert.False((await _users.SaveAsync(NewUser(username), _w.AdminId)).Success);
        }

        [Fact]
        public async Task WeakTemporaryPassword_IsRejected()
        {
            var input = NewUser();
            input.Password = "weak";

            Assert.False((await _users.SaveAsync(input, _w.AdminId)).Success);
        }

        [Fact]
        public async Task OnlyAnAdministrator_CanManageUsers()
        {
            _w.Auth.SignInAs(RoleNames.InventoryPersonnel, _w.InventoryUserId);

            var r = await _users.SaveAsync(NewUser(), _w.InventoryUserId);

            Assert.False(r.Success);
            Assert.Contains("administrator", r.Error);
        }

        [Fact]
        public async Task AdministratorCannotDeactivateTheirOwnAccount()
        {
            var r = await _users.SetActiveAsync(_w.AdminId, false, _w.AdminId);

            Assert.False(r.Success);
        }

        [Fact]
        public async Task TheLastActiveAdministrator_CannotBeDeactivatedOrDemoted()
        {
            // acting "as someone else" so the self-protection rule is not what stops it
            var deactivate = await _users.SetActiveAsync(_w.AdminId, false, adminId: 999);
            Assert.False(deactivate.Success);
            Assert.Contains("only active administrator", deactivate.Error);

            var demote = await _users.SaveAsync(new UserInput
            {
                UserId = _w.AdminId, Username = "admin", FullName = "Admin", RoleId = _inventoryRoleId
            }, adminId: 999);
            Assert.False(demote.Success);
        }

        [Fact]
        public async Task WithASecondAdministrator_TheFirstCanBeDeactivated()
        {
            var second = NewUser("second.admin");
            second.RoleId = _adminRoleId;
            Assert.True((await _users.SaveAsync(second, _w.AdminId)).Success);

            var r = await _users.SetActiveAsync(_w.AdminId, false, adminId: 999);

            Assert.True(r.Success, r.Error);
        }

        [Fact]
        public async Task AdministratorCannotChangeTheirOwnRole()
        {
            var r = await _users.SaveAsync(new UserInput
            {
                UserId = _w.AdminId, Username = "admin", FullName = "Admin", RoleId = _inventoryRoleId
            }, _w.AdminId);

            Assert.False(r.Success);
        }

        [Fact]
        public async Task AUserWhoNeverDidAnything_CanBeDeleted()
        {
            var created = await _users.SaveAsync(NewUser(), _w.AdminId);

            var r = await _users.DeleteAsync(created.Id!.Value, _w.AdminId);

            Assert.True(r.Success, r.Error);
            Assert.DoesNotContain(await _users.ListAsync(), u => u.Username == "maria");
            Assert.True(await _w.AuditContainsAsync("Delete User"));
        }

        [Fact]
        public async Task AUserWithHistory_CannotBeDeleted()
        {
            await _w.Audit.LogAsync(_w.CashierId, "Login"); // the cashier has signed in once

            var r = await _users.DeleteAsync(_w.CashierId, _w.AdminId);

            Assert.False(r.Success);
            Assert.Contains("deactivate", r.Error);
            Assert.Contains(await _users.ListAsync(), u => u.UserId == _w.CashierId);
        }

        [Fact]
        public async Task AdministratorCannotDeleteTheirOwnAccount()
        {
            Assert.False((await _users.DeleteAsync(_w.AdminId, _w.AdminId)).Success);
        }

        [Fact]
        public async Task TheLastActiveAdministrator_CannotBeDeleted()
        {
            var r = await _users.DeleteAsync(_w.AdminId, adminId: 999);

            Assert.False(r.Success);
            Assert.Contains("only active administrator", r.Error);
        }

        [Fact]
        public async Task OnlyAnAdministrator_CanDeleteUsers()
        {
            _w.Auth.SignInAs(RoleNames.InventoryPersonnel, _w.InventoryUserId);

            Assert.False((await _users.DeleteAsync(_w.CashierId, _w.InventoryUserId)).Success);
        }

        [Fact]
        public async Task ResetPassword_SetsANewPassword_AndForcesAChange()
        {
            var r = await _users.ResetPasswordAsync(_w.CashierId, "Another123", _w.AdminId);
            Assert.True(r.Success, r.Error);

            await using var db = _w.Db.CreateDbContext();
            var user = await db.Users.SingleAsync(u => u.UserId == _w.CashierId);
            Assert.True(user.MustChangePassword);
            Assert.True(PasswordHasher.Verify("Another123", user.PasswordHash));

            // the password itself must never appear in the audit trail
            Assert.DoesNotContain(await db.AuditLogs.ToListAsync(), a => (a.Description ?? string.Empty).Contains("Another123"));
        }
    }
}
