using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Security;

namespace SmartInventorySystem.Tests
{
    /// <summary>
    /// A throw-away SQLite database that lives in memory. It is created with the SAME model, constraints
    /// and collations as the real database, so the tests exercise the real rules.
    /// </summary>
    public sealed class TestDb : IDbContextFactory<AppDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDb()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open(); // the in-memory database lives as long as this connection stays open

            _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
            using var db = CreateDbContext();
            db.Database.EnsureCreated();
        }

        public AppDbContext CreateDbContext() => new AppDbContext(_options);

        public void Dispose() => _connection.Dispose();
    }

    /// <summary>A stand-in for the signed-in user so services that check permissions can be tested.</summary>
    public sealed class FakeAuth : IAuthService
    {
        public CurrentUser? Current { get; private set; }

#pragma warning disable CS0067 // never raised in tests
        public event Action? Changed;
#pragma warning restore CS0067

        public void SignInAs(string roleName, int userId = 1) =>
            Current = new CurrentUser(userId, "tester", "Test User", roleName, false);

        public void SignOut() => Current = null;

        public bool Can(string permission) =>
            Current is not null && Permissions.ForRole(Current.RoleName).Contains(permission);

        public Task<LoginResult> LoginAsync(string username, string password) => throw new NotSupportedException();
        public Task LogoutAsync() => throw new NotSupportedException();
        public Task<OperationResult> ChangePasswordAsync(string currentPassword, string newPassword) => throw new NotSupportedException();
    }

    /// <summary>A small, predictable shop: 3 users, 1 category, 1 supplier (5-day lead time) and helpers to add data.</summary>
    public sealed class World : IDisposable
    {
        // Hashing is slow on purpose, so every test user shares one hash of this password.
        public const string Password = "Test1234!";
        private static readonly string SharedHash = PasswordHasher.Hash(Password);

        public TestDb Db { get; } = new();
        public FakeAuth Auth { get; } = new();
        public AuditService Audit { get; }

        public int AdminId { get; private set; }
        public int InventoryUserId { get; private set; }
        public int CashierId { get; private set; }
        public int CategoryId { get; private set; }
        public int SupplierId { get; private set; }

        private int _skuCounter;

        public World() => Audit = new AuditService(Db);

        public static async Task<World> CreateAsync()
        {
            var w = new World();
            await using var db = w.Db.CreateDbContext();

            var admin = new Role { RoleName = RoleNames.Administrator };
            var inventory = new Role { RoleName = RoleNames.InventoryPersonnel };
            var cashier = new Role { RoleName = RoleNames.SalesCashier };
            db.Roles.AddRange(admin, inventory, cashier);

            var u1 = NewUser("admin", admin);
            var u2 = NewUser("inventory", inventory);
            var u3 = NewUser("cashier", cashier);
            db.Users.AddRange(u1, u2, u3);

            var category = new Category { CategoryName = "Paper" };
            var supplier = new Supplier { SupplierName = "Test Supplier", LeadTimeDays = 5 };
            db.Categories.Add(category);
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();

            w.AdminId = u1.UserId;
            w.InventoryUserId = u2.UserId;
            w.CashierId = u3.UserId;
            w.CategoryId = category.CategoryId;
            w.SupplierId = supplier.SupplierId;
            w.Auth.SignInAs(RoleNames.Administrator, w.AdminId);
            return w;
        }

        private static User NewUser(string username, Role role) => new()
        {
            Username = username, FullName = username + " user", PasswordHash = SharedHash, Role = role
        };

        public async Task<int> AddItemAsync(string name, decimal quantity, decimal safetyStock = 0, decimal reorderPoint = 0,
            decimal minimumOrder = 0, bool withSupplier = true, int? categoryId = null, string? sku = null)
        {
            await using var db = Db.CreateDbContext();
            var item = new Item
            {
                ItemName = name,
                SKU = sku ?? $"SKU-{++_skuCounter}",
                CategoryId = categoryId ?? CategoryId,
                SupplierId = withSupplier ? SupplierId : null,
                UnitOfMeasure = "sheet",
                UnitCost = 1m,
                SafetyStock = safetyStock,
                ReorderPoint = reorderPoint,
                ReorderQuantity = minimumOrder,
                Inventory = new Inventory { QuantityOnHand = quantity }
            };
            db.Items.Add(item);
            await db.SaveChangesAsync();
            return item.ItemId;
        }

        /// <summary>Adds a usage record at noon, <paramref name="daysAgo"/> days before today (local time).</summary>
        public async Task AddUsageAsync(int itemId, decimal quantity, int daysAgo)
        {
            await using var db = Db.CreateDbContext();
            db.UsageTransactions.Add(new UsageTransaction
            {
                ItemId = itemId,
                QuantityUsed = quantity,
                JobReference = "Test job",
                TransactionDate = DateTime.Today.AddDays(-daysAgo).AddHours(12).ToUniversalTime(),
                RecordedByUserId = CashierId
            });
            await db.SaveChangesAsync();
        }

        public async Task SetQuantityAsync(int itemId, decimal quantity)
        {
            await using var db = Db.CreateDbContext();
            var inv = await db.Inventories.SingleAsync(i => i.ItemId == itemId);
            inv.QuantityOnHand = quantity;
            await db.SaveChangesAsync();
        }

        public async Task SetItemStatusAsync(int itemId, RecordStatus status)
        {
            await using var db = Db.CreateDbContext();
            var item = await db.Items.SingleAsync(i => i.ItemId == itemId);
            item.Status = status;
            await db.SaveChangesAsync();
        }

        public async Task<decimal> QuantityAsync(int itemId)
        {
            await using var db = Db.CreateDbContext();
            return (await db.Inventories.AsNoTracking().SingleAsync(i => i.ItemId == itemId)).QuantityOnHand;
        }

        public async Task<bool> AuditContainsAsync(string action)
        {
            await using var db = Db.CreateDbContext();
            return await db.AuditLogs.AnyAsync(a => a.Action == action);
        }

        public void Dispose() => Db.Dispose();
    }
}
