using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services.Security;

namespace SmartInventorySystem.Services.Auth
{
    public sealed record CurrentUser(int UserId, string Username, string FullName, string RoleName, bool MustChangePassword);

    public sealed record LoginResult(bool Success, string? Error = null, bool MustChangePassword = false);

    public sealed record OperationResult(bool Success, string? Error = null);

    public interface IAuthService
    {
        /// <summary>The signed-in user, or null when nobody is signed in.</summary>
        CurrentUser? Current { get; }
        /// <summary>Raised after sign-in, sign-out or a password change.</summary>
        event Action? Changed;
        bool Can(string permission);
        Task<LoginResult> LoginAsync(string username, string password);
        Task LogoutAsync();
        Task<OperationResult> ChangePasswordAsync(string currentPassword, string newPassword);
    }

    public sealed class AuthService : IAuthService
    {
        private const int MaxFailedAttempts = 5;
        private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(60);

        // Used so a wrong username takes as long as a wrong password (no username guessing by timing).
        private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash("dummy-password-for-timing"));

        private sealed class Attempt { public int Fails; public DateTime? LockedUntil; }

        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;
        private readonly AppAuthStateProvider _state;
        private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public AuthService(IDbContextFactory<AppDbContext> factory, IAuditService audit, AppAuthStateProvider state)
        {
            _factory = factory;
            _audit = audit;
            _state = state;
        }

        public CurrentUser? Current { get; private set; }

        public event Action? Changed;

        public bool Can(string permission) =>
            Current is not null && Permissions.ForRole(Current.RoleName).Contains(permission);

        public async Task<LoginResult> LoginAsync(string username, string password)
        {
            username = (username ?? string.Empty).Trim();
            if (username.Length == 0 || string.IsNullOrEmpty(password))
                return new LoginResult(false, "Please enter your username and password.");

            var wait = SecondsLocked(username);
            if (wait > 0)
                return new LoginResult(false, $"Too many failed attempts. Please try again in {wait} seconds.");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();

                // Username column uses NOCASE collation, so "Admin" matches "admin".
                var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Username == username);

                var hash = user?.PasswordHash ?? DummyHash.Value;
                var passwordOk = await Task.Run(() => PasswordHasher.Verify(password, hash)); // keeps the UI responsive

                if (user is null || !passwordOk)
                {
                    RegisterFailure(username);
                    await _audit.LogAsync(user?.UserId, "Login Failed", "User", user?.UserId.ToString(),
                        $"Failed sign-in attempt for username '{username}'.");
                    return new LoginResult(false, "Invalid username or password.");
                }

                if (user.Status != RecordStatus.Active)
                {
                    await _audit.LogAsync(user.UserId, "Login Blocked", "User", user.UserId.ToString(),
                        "Sign-in refused: account is inactive.");
                    return new LoginResult(false, "This account is inactive. Please contact the administrator.");
                }

                ClearFailures(username);

                user.LastLoginAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                Current = new CurrentUser(user.UserId, user.Username, user.FullName, user.Role.RoleName, user.MustChangePassword);
                _state.SignIn(Current);

                await _audit.LogAsync(user.UserId, "Login", "User", user.UserId.ToString(),
                    $"{user.Username} signed in as {user.Role.RoleName}.");

                Changed?.Invoke();
                return new LoginResult(true, MustChangePassword: user.MustChangePassword);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Login failed unexpectedly.", ex);
                return new LoginResult(false, "Unable to sign in right now. Please try again.");
            }
        }

        public async Task LogoutAsync()
        {
            var user = Current;
            if (user is not null)
            {
                await _audit.LogAsync(user.UserId, "Logout", "User", user.UserId.ToString(),
                    $"{user.Username} signed out.");
            }

            Current = null;
            _state.SignOut();
            Changed?.Invoke();
        }

        public async Task<OperationResult> ChangePasswordAsync(string currentPassword, string newPassword)
        {
            var me = Current;
            if (me is null) return new OperationResult(false, "You are not signed in.");

            var policyError = PasswordPolicy.Validate(newPassword);
            if (policyError is not null) return new OperationResult(false, policyError);
            if (newPassword == currentPassword)
                return new OperationResult(false, "The new password must be different from the current password.");

            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == me.UserId);
                if (user is null) return new OperationResult(false, "Account not found.");

                var ok = await Task.Run(() => PasswordHasher.Verify(currentPassword, user.PasswordHash));
                if (!ok) return new OperationResult(false, "The current password is incorrect.");

                user.PasswordHash = await Task.Run(() => PasswordHasher.Hash(newPassword));
                user.MustChangePassword = false;
                user.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                await _audit.LogAsync(user.UserId, "Change Password", "User", user.UserId.ToString(),
                    $"{user.Username} changed their password.");

                Current = me with { MustChangePassword = false };
                _state.SignIn(Current);
                Changed?.Invoke();
                return new OperationResult(true);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Change password failed unexpectedly.", ex);
                return new OperationResult(false, "Unable to change the password. Please try again.");
            }
        }

        // ---- simple brute-force protection (in memory, resets when the app closes) ----

        private int SecondsLocked(string username)
        {
            lock (_gate)
            {
                if (_attempts.TryGetValue(username, out var a) && a.LockedUntil is DateTime until && until > DateTime.UtcNow)
                    return (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds);
                return 0;
            }
        }

        private void RegisterFailure(string username)
        {
            lock (_gate)
            {
                if (!_attempts.TryGetValue(username, out var a)) _attempts[username] = a = new Attempt();
                a.Fails++;
                if (a.Fails >= MaxFailedAttempts)
                {
                    a.Fails = 0;
                    a.LockedUntil = DateTime.UtcNow.Add(LockDuration);
                }
            }
        }

        private void ClearFailures(string username)
        {
            lock (_gate) { _attempts.Remove(username); }
        }
    }
}
