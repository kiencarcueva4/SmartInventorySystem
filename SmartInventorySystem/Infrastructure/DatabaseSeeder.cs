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
    // =========================================================================
    // SEED DATA (development / testing, fictional values only)
    // =========================================================================
    public static class DatabaseSeeder
    {
        /// <summary>
        /// true  = also generate ~90 days of fake stock-in / stock-out / usage history so the
        ///         analytics phases (classification, forecast, trends) have data to work on.
        /// false = only seed roles, users, categories, suppliers, items and settings.
        /// Set to false before giving the system to a real shop.
        /// </summary>
        private static readonly bool SeedDemoHistory = true;
        private const int DemoDays = 90;

        private record ItemSeed(
            string Name, string Sku, string Category, string Unit, decimal Cost,
            decimal Safety, decimal ReorderPoint, decimal ReorderQty, int SupplierIndex,
            double AvgDaily, double UseProbability, decimal InitialStock);

        public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
        {
            await SeedSettingsAsync(db, ct);

            // Core data is seeded only once (empty database).
            if (await db.Roles.AnyAsync(ct)) return;

            // ---- Roles ----
            var roles = new[]
            {
                new Role { RoleName = SmartInventorySystem.Services.RoleNames.Administrator,      Description = "Owner / full access" },
                new Role { RoleName = SmartInventorySystem.Services.RoleNames.InventoryPersonnel, Description = "Manages inventory and views analytics" },
                new Role { RoleName = SmartInventorySystem.Services.RoleNames.SalesCashier,       Description = "Records material usage and checks availability" },
            };
            db.Roles.AddRange(roles);

            // ---- Users (dev passwords, must be changed) ----
            var admin = NewUser("admin", "Admin@123", "Shop Owner", roles[0]);
            var inventoryUser = NewUser("inventory", "Inventory@123", "Inventory Staff", roles[1]);
            var cashier = NewUser("cashier", "Cashier@123", "Cashier Staff", roles[2]);
            db.Users.AddRange(admin, inventoryUser, cashier);

            // ---- Categories ----
            var categories = new[] { "Paper", "Ink", "Toner", "Vinyl", "Tarpaulin", "Laminating", "Binding" }
                .Select(n => new Category { CategoryName = n, Description = $"{n} materials" })
                .ToDictionary(c => c.CategoryName);
            db.Categories.AddRange(categories.Values);

            // ---- Suppliers (fictional) ----
            var suppliers = new[]
            {
                new Supplier { SupplierName = "Sample Paper Trading",      ContactPerson = "Test Contact A", Phone = "(000) 000-0001", Email = "paper@example.test",  Address = "1 Sample Street",   LeadTimeDays = 3 },
                new Supplier { SupplierName = "Demo Ink & Toner Supply",   ContactPerson = "Test Contact B", Phone = "(000) 000-0002", Email = "ink@example.test",    Address = "2 Demo Avenue",     LeadTimeDays = 5 },
                new Supplier { SupplierName = "Sample Signage Materials",  ContactPerson = "Test Contact C", Phone = "(000) 000-0003", Email = "signs@example.test",  Address = "3 Test Road",       LeadTimeDays = 7 },
                new Supplier { SupplierName = "Test Finishing Supplies",   ContactPerson = "Test Contact D", Phone = "(000) 000-0004", Email = "finish@example.test", Address = "4 Fictional Lane",  LeadTimeDays = 4 },
            };
            db.Suppliers.AddRange(suppliers);

            // ---- Items ----
            var seeds = new[]
            {
                new ItemSeed("A4 Bond Paper",        "PAP-A4-001", "Paper",      "sheet",  0.60m, 500, 1500, 5000, 0, 350, 0.95, 6000),
                new ItemSeed("A3 Bond Paper",        "PAP-A3-001", "Paper",      "sheet",  1.20m, 200,  600, 2000, 0,  90, 0.80, 2500),
                new ItemSeed("Glossy Photo Paper",   "PAP-GLS-001","Paper",      "sheet",  6.50m, 100,  300, 1000, 0,  40, 0.60,  800),
                new ItemSeed("Tarpaulin Material",   "TAR-001",    "Tarpaulin",  "sq ft", 12.00m,  50,  150,  500, 2,  30, 0.50,  400),
                new ItemSeed("Black Ink",            "INK-BLK-001","Ink",        "bottle",350m,    3,    8,   20, 1,   1, 0.35,   15),
                new ItemSeed("Color Ink",            "INK-CLR-001","Ink",        "bottle",380m,    4,   10,   24, 1,   1, 0.40,   18),
                new ItemSeed("Black Toner",          "TON-BLK-001","Toner",      "cartridge",2200m, 1,    2,    4, 1,   1, 0.06,    4),
                new ItemSeed("Vinyl Sticker Roll",   "VIN-001",    "Vinyl",      "meter", 85.00m,  10,   30,  100, 2,   5, 0.30,   80),
                new ItemSeed("Laminating Film",      "LAM-001",    "Laminating", "meter", 25.00m,  20,   60,  200, 3,   8, 0.40,  150),
                new ItemSeed("Binding Comb",         "BND-001",    "Binding",    "piece",  3.50m,  50,  150,  500, 3,   6, 0.25,  400),
            };

            var items = new List<(Item item, ItemSeed seed)>();
            var start = DateTime.UtcNow.Date.AddDays(-DemoDays);
            foreach (var s in seeds)
            {
                var item = new Item
                {
                    ItemName = s.Name, SKU = s.Sku, Description = $"{s.Name} (sample item)",
                    Category = categories[s.Category], Supplier = suppliers[s.SupplierIndex],
                    UnitOfMeasure = s.Unit, UnitCost = s.Cost,
                    SafetyStock = s.Safety, ReorderPoint = s.ReorderPoint, ReorderQuantity = s.ReorderQty,
                    CreatedAt = start, UpdatedAt = start,
                };
                db.Items.Add(item);
                items.Add((item, s));
            }

            // Manual substitution example: Glossy Photo Paper <-> A4 Bond Paper is NOT sensible,
            // so only link same-purpose materials. Add real links through the UI in Phase 8.

            if (SeedDemoHistory)
            {
                GenerateDemoHistory(db, items, start, inventoryUser, cashier);
            }
            else
            {
                foreach (var (item, _) in items)
                    db.Inventories.Add(new Inventory { Item = item, QuantityOnHand = 0 });
            }

            db.AuditLogs.Add(new AuditLog
            {
                Action = "Database Seeded", EntityName = "System",
                Description = "Initial development data created.", Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);
        }

        private static User NewUser(string username, string password, string fullName, Role role) => new()
        {
            Username = username,
            PasswordHash = SmartInventorySystem.Services.Security.PasswordHasher.Hash(password),
            FullName = fullName,
            Role = role,
            Status = RecordStatus.Active,
            MustChangePassword = true,
        };

        /// <summary>
        /// Simulates daily operation so history is internally consistent:
        /// opening balance (Stock-In) -> daily Stock-Out + Usage rows -> automatic restock
        /// Stock-In the next day whenever stock falls to the reorder point.
        /// The final Inventory.QuantityOnHand equals opening + all stock-in - all stock-out.
        /// </summary>
        private static void GenerateDemoHistory(
            AppDbContext db, List<(Item item, ItemSeed seed)> items,
            DateTime start, User receiver, User releaser)
        {
            var rng = new Random(42); // fixed seed = same demo data every time
            string[] jobTypes =
            {
                "Business Card Printing", "Document Printing", "Photo Print Order",
                "Banner Order", "Laminating Job", "Book Binding"
            };
            int soCounter = 1, poCounter = 1, jobCounter = 1;

            foreach (var (item, s) in items)
            {
                decimal stock = s.InitialStock;
                bool restockPending = false;

                // Opening balance recorded as a normal stock-in.
                db.StockIns.Add(new StockIn
                {
                    Item = item, Supplier = item.Supplier, Quantity = stock, UnitCost = item.UnitCost,
                    TotalCost = stock * item.UnitCost, ReferenceNumber = "OPENING-BAL",
                    StockInDate = start.AddHours(8), ReceivedBy = receiver
                });

                for (int day = 0; day < DemoDays; day++)
                {
                    var date = start.AddDays(day);

                    if (day > 0 && restockPending)
                    {
                        db.StockIns.Add(new StockIn
                        {
                            Item = item, Supplier = item.Supplier, Quantity = s.ReorderQty,
                            UnitCost = item.UnitCost, TotalCost = s.ReorderQty * item.UnitCost,
                            ReferenceNumber = $"PO-SEED-{poCounter++:D4}",
                            StockInDate = date.AddHours(8), ReceivedBy = receiver
                        });
                        stock += s.ReorderQty;
                        restockPending = false;
                    }

                    if (rng.NextDouble() < s.UseProbability)
                    {
                        var qty = Math.Max(1m, Math.Round((decimal)(s.AvgDaily * (0.4 + rng.NextDouble() * 1.2))));
                        qty = Math.Min(qty, stock); // never consume more than is on hand
                        if (qty > 0)
                        {
                            var when = date.AddHours(9 + rng.Next(0, 9));
                            var job = $"{jobTypes[rng.Next(jobTypes.Length)]} - Customer #{rng.Next(1, 200):D3}";
                            var so = new StockOut
                            {
                                Item = item, Quantity = qty, Purpose = job,
                                ReferenceNumber = $"SO-{soCounter++:D5}",
                                StockOutDate = when, ReleasedBy = releaser
                            };
                            db.StockOuts.Add(so);
                            db.UsageTransactions.Add(new UsageTransaction
                            {
                                Item = item, StockOut = so, QuantityUsed = qty,
                                JobReference = $"{job} (Job #{1000 + jobCounter++})",
                                TransactionDate = when, RecordedBy = releaser
                            });
                            stock -= qty;
                        }
                    }

                    if (stock <= s.ReorderPoint) restockPending = true;
                }

                db.Inventories.Add(new Inventory { Item = item, QuantityOnHand = stock, LastUpdated = DateTime.UtcNow });
            }
        }

        // ---- Default business rules (editable later on the Settings page) ----
        private static async Task SeedSettingsAsync(AppDbContext db, CancellationToken ct)
        {
            var defaults = new (string Key, string Value, string Group, string Description)[]
            {
                (SmartInventorySystem.Services.SettingKeys.MovementEvaluationDays,   "90",  "Movement", "Days of history used to classify movement"),
                (SmartInventorySystem.Services.SettingKeys.MovementFastMinTurnover,  "2.0", "Movement", "Turnover >= this value = Fast-Moving"),
                (SmartInventorySystem.Services.SettingKeys.MovementSlowMaxTurnover,  "0.5", "Movement", "Turnover <= this value = Slow-Moving"),

                (SmartInventorySystem.Services.SettingKeys.ForecastWeightRecent,     "0.50", "Forecast", "Weight of the most recent period (all weights must total 1.00)"),
                (SmartInventorySystem.Services.SettingKeys.ForecastWeightPrevious,   "0.30", "Forecast", "Weight of the previous period"),
                (SmartInventorySystem.Services.SettingKeys.ForecastWeightOlder,      "0.20", "Forecast", "Weight of the older period"),
                (SmartInventorySystem.Services.SettingKeys.ForecastPeriodDays,       "7",    "Forecast", "Length of one forecast period in days"),
                (SmartInventorySystem.Services.SettingKeys.ForecastAlpha,            "0.30", "Forecast", "Smoothing factor for Simple Exponential Smoothing (0-1)"),

                (SmartInventorySystem.Services.SettingKeys.ReorderDefaultLeadTimeDays,   "7",   "Reorder", "Lead time used when an item has no supplier"),
                (SmartInventorySystem.Services.SettingKeys.ReorderSafetyStockDays,       "3",   "Reorder", "Suggested safety stock = average daily usage x this many days"),
                (SmartInventorySystem.Services.SettingKeys.ReorderOverstockMultiplier,   "3.0", "Reorder", "Overstock when stock > reorder point x this value"),

                (SmartInventorySystem.Services.SettingKeys.AlertCriticalStockPercent,    "50",  "Alerts", "Critical when stock <= this % of safety stock"),
                (SmartInventorySystem.Services.SettingKeys.AlertObsolescenceNoUseDays,   "60",  "Alerts", "Days without use before obsolescence risk rises"),
                (SmartInventorySystem.Services.SettingKeys.AlertForecastErrorPercent,    "30",  "Alerts", "Warn when forecast error exceeds this %"),
            };

            var existing = await db.SystemSettings.Select(s => s.SettingKey).ToListAsync(ct);
            foreach (var d in defaults.Where(d => !existing.Contains(d.Key)))
            {
                db.SystemSettings.Add(new SystemSetting
                {
                    SettingKey = d.Key, SettingValue = d.Value,
                    SettingGroup = d.Group, Description = d.Description
                });
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
