using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;

namespace SmartInventorySystem.Infrastructure
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Role> Roles => Set<Role>();
        public DbSet<User> Users => Set<User>();
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<Supplier> Suppliers => Set<Supplier>();
        public DbSet<Item> Items => Set<Item>();
        public DbSet<Inventory> Inventories => Set<Inventory>();
        public DbSet<StockIn> StockIns => Set<StockIn>();
        public DbSet<StockOut> StockOuts => Set<StockOut>();
        public DbSet<UsageTransaction> UsageTransactions => Set<UsageTransaction>();
        public DbSet<MovementClassification> MovementClassifications => Set<MovementClassification>();
        public DbSet<Forecast> Forecasts => Set<Forecast>();
        public DbSet<InventoryTrend> InventoryTrends => Set<InventoryTrend>();
        public DbSet<ReorderRecommendation> ReorderRecommendations => Set<ReorderRecommendation>();
        public DbSet<PurchaseRestockRecord> PurchaseRestockRecords => Set<PurchaseRestockRecord>();
        public DbSet<Alert> Alerts => Set<Alert>();
        public DbSet<Report> Reports => Set<Report>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<ItemSubstitute> ItemSubstitutes => Set<ItemSubstitute>();
        public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            // SQLite has no real decimal type. EF stores decimal as TEXT, which breaks
            // SUM / AVG / ORDER BY. Store as REAL (double) instead.
            configurationBuilder.Properties<decimal>().HaveConversion<double>();

            // Enums are saved as readable text (e.g. "Urgent") instead of numbers.
            configurationBuilder.Properties<Enum>().HaveConversion<string>();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ---- Roles / Users ------------------------------------------------
            modelBuilder.Entity<Role>(e =>
            {
                e.HasIndex(x => x.RoleName).IsUnique();
            });

            modelBuilder.Entity<User>(e =>
            {
                e.Property(x => x.Username).UseCollation("NOCASE"); // "Admin" == "admin"
                e.HasIndex(x => x.Username).IsUnique();
                e.HasOne(x => x.Role).WithMany(r => r.Users).HasForeignKey(x => x.RoleId);
            });

            // ---- Categories / Suppliers ---------------------------------------
            modelBuilder.Entity<Category>(e =>
            {
                e.Property(x => x.CategoryName).UseCollation("NOCASE");
                e.HasIndex(x => x.CategoryName).IsUnique();
            });

            modelBuilder.Entity<Supplier>(e =>
            {
                e.Property(x => x.SupplierName).UseCollation("NOCASE");
                e.HasIndex(x => x.SupplierName).IsUnique();
                e.ToTable(t => t.HasCheckConstraint("CK_Suppliers_LeadTime", "[LeadTimeDays] >= 0"));
            });

            // ---- Items / Inventory --------------------------------------------
            modelBuilder.Entity<Item>(e =>
            {
                e.Property(x => x.SKU).UseCollation("NOCASE");
                e.HasIndex(x => x.SKU).IsUnique();
                e.HasIndex(x => x.ItemName);

                e.HasOne(x => x.Category).WithMany(c => c.Items).HasForeignKey(x => x.CategoryId);
                e.HasOne(x => x.Supplier).WithMany(s => s.Items).HasForeignKey(x => x.SupplierId);
                e.HasOne(x => x.Inventory).WithOne(i => i.Item).HasForeignKey<Inventory>(i => i.ItemId);

                e.ToTable(t =>
                {
                    t.HasCheckConstraint("CK_Items_UnitCost", "[UnitCost] >= 0");
                    t.HasCheckConstraint("CK_Items_SafetyStock", "[SafetyStock] >= 0");
                    t.HasCheckConstraint("CK_Items_ReorderPoint", "[ReorderPoint] >= 0");
                    t.HasCheckConstraint("CK_Items_ReorderQuantity", "[ReorderQuantity] >= 0");
                });
            });

            modelBuilder.Entity<Inventory>(e =>
            {
                e.HasIndex(x => x.ItemId).IsUnique(); // one stock row per item
                e.ToTable(t => t.HasCheckConstraint("CK_Inventory_Qty", "[QuantityOnHand] >= 0"));
            });

            // ---- Transactions -------------------------------------------------
            modelBuilder.Entity<StockIn>(e =>
            {
                e.HasIndex(x => new { x.ItemId, x.StockInDate });
                e.HasIndex(x => x.ReferenceNumber);
                e.HasOne(x => x.Item).WithMany(i => i.StockIns).HasForeignKey(x => x.ItemId);
                e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId);
                e.HasOne(x => x.ReceivedBy).WithMany().HasForeignKey(x => x.ReceivedByUserId);
                e.ToTable(t =>
                {
                    t.HasCheckConstraint("CK_StockIn_Qty", "[Quantity] > 0");
                    t.HasCheckConstraint("CK_StockIn_Cost", "[UnitCost] >= 0");
                });
            });

            modelBuilder.Entity<StockOut>(e =>
            {
                e.HasIndex(x => new { x.ItemId, x.StockOutDate });
                e.HasIndex(x => x.ReferenceNumber);
                e.HasOne(x => x.Item).WithMany(i => i.StockOuts).HasForeignKey(x => x.ItemId);
                e.HasOne(x => x.ReleasedBy).WithMany().HasForeignKey(x => x.ReleasedByUserId);
                e.ToTable(t => t.HasCheckConstraint("CK_StockOut_Qty", "[Quantity] > 0"));
            });

            modelBuilder.Entity<UsageTransaction>(e =>
            {
                e.HasIndex(x => new { x.ItemId, x.TransactionDate });
                e.HasOne(x => x.Item).WithMany(i => i.UsageTransactions).HasForeignKey(x => x.ItemId);
                e.HasOne(x => x.StockOut).WithMany().HasForeignKey(x => x.StockOutId);
                e.HasOne(x => x.RecordedBy).WithMany().HasForeignKey(x => x.RecordedByUserId);
                e.ToTable(t => t.HasCheckConstraint("CK_Usage_Qty", "[QuantityUsed] > 0"));
            });

            // ---- Analytics ----------------------------------------------------
            modelBuilder.Entity<MovementClassification>(e =>
            {
                e.HasIndex(x => new { x.ItemId, x.CalculatedAt });
                e.HasOne(x => x.Item).WithMany(i => i.MovementClassifications).HasForeignKey(x => x.ItemId);
            });

            modelBuilder.Entity<Forecast>(e =>
            {
                e.HasIndex(x => new { x.ItemId, x.PeriodStart });
                e.HasOne(x => x.Item).WithMany(i => i.Forecasts).HasForeignKey(x => x.ItemId);
                // Nullable decimals are not caught by the convention above, so set them here.
                e.Property(x => x.ActualValue).HasConversion<double?>();
                e.Property(x => x.ForecastError).HasConversion<double?>();
                e.Property(x => x.PercentageError).HasConversion<double?>();
            });

            modelBuilder.Entity<InventoryTrend>(e =>
            {
                e.HasIndex(x => new { x.ItemId, x.PeriodStart });
                e.HasOne(x => x.Item).WithMany(i => i.InventoryTrends).HasForeignKey(x => x.ItemId);
            });

            // ---- Reordering ---------------------------------------------------
            modelBuilder.Entity<ReorderRecommendation>(e =>
            {
                e.HasIndex(x => new { x.Status, x.Urgency });
                e.HasIndex(x => x.ItemId);
                e.HasOne(x => x.Item).WithMany(i => i.ReorderRecommendations).HasForeignKey(x => x.ItemId);
                e.HasOne(x => x.DecidedBy).WithMany().HasForeignKey(x => x.DecidedByUserId);
                e.ToTable(t => t.HasCheckConstraint("CK_Reorder_Qty", "[RecommendedQuantity] >= 0"));
            });

            modelBuilder.Entity<PurchaseRestockRecord>(e =>
            {
                e.HasIndex(x => x.RecommendationId).IsUnique(); // one restock per approved recommendation
                e.HasOne(x => x.Recommendation).WithOne(r => r.PurchaseRestockRecord)
                    .HasForeignKey<PurchaseRestockRecord>(x => x.RecommendationId);
                e.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId);
                e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId);
                e.ToTable(t => t.HasCheckConstraint("CK_Restock_Qty", "[ApprovedQuantity] > 0"));
            });

            // ---- Supporting ---------------------------------------------------
            modelBuilder.Entity<Alert>(e =>
            {
                e.HasIndex(x => new { x.IsAcknowledged, x.CreatedAt });
                e.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId);
                e.HasOne(x => x.AcknowledgedBy).WithMany().HasForeignKey(x => x.AcknowledgedByUserId);
            });

            modelBuilder.Entity<Report>(e =>
            {
                e.HasOne(x => x.GeneratedBy).WithMany().HasForeignKey(x => x.GeneratedByUserId);
            });

            modelBuilder.Entity<AuditLog>(e =>
            {
                e.HasIndex(x => x.Timestamp);
                e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            });

            modelBuilder.Entity<ItemSubstitute>(e =>
            {
                e.HasIndex(x => new { x.ItemId, x.SubstituteItemId }).IsUnique();
                e.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId);
                e.HasOne(x => x.SubstituteItem).WithMany().HasForeignKey(x => x.SubstituteItemId);
                e.ToTable(t => t.HasCheckConstraint("CK_Substitute_NotSelf", "[ItemId] <> [SubstituteItemId]"));
            });

            modelBuilder.Entity<SystemSetting>(e =>
            {
                e.HasIndex(x => x.SettingKey).IsUnique();
            });

            // Safety net: never cascade-delete business records. Items, users, suppliers
            // are deactivated, not deleted, so history stays intact.
            foreach (var fk in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
            {
                fk.DeleteBehavior = DeleteBehavior.Restrict;
            }
        }
    }

    /// <summary>Lets "Add-Migration" create the DbContext at design time (no app startup needed).</summary>
    public class AppDbContextDesignFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite("Data Source=smart_inventory_design.db")
                .Options;
            return new AppDbContext(options);
        }
    }
}
