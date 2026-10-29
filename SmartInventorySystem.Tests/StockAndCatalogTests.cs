using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Services;
using SmartInventorySystem.Services.Core;
using Xunit;

namespace SmartInventorySystem.Tests
{
    // =====================================================================================
    // Stock-In / Stock-Out
    // =====================================================================================
    public class StockServiceTests : IAsyncLifetime
    {
        private World _w = null!;
        private StockService _stock = null!;

        public async Task InitializeAsync()
        {
            _w = await World.CreateAsync();
            _stock = new StockService(_w.Db, _w.Audit);
        }

        public Task DisposeAsync()
        {
            _w.Dispose();
            return Task.CompletedTask;
        }

        private StockOutInput Out(int itemId, decimal qty, string job = "Business Card Printing - Customer #001") =>
            new() { ItemId = itemId, Quantity = qty, JobReference = job };

        [Fact]
        public async Task StockIn_IncreasesStock_SavesHistory_AndWritesAudit()
        {
            var item = await _w.AddItemAsync("A4 Bond Paper", quantity: 0);

            var r = await _stock.RecordStockInAsync(new StockInInput
            {
                ItemId = item, SupplierId = _w.SupplierId, Quantity = 100, UnitCost = 2m
            }, _w.AdminId);

            Assert.True(r.Success, r.Error);
            Assert.Equal(100m, await _w.QuantityAsync(item));

            await using var db = _w.Db.CreateDbContext();
            var row = await db.StockIns.SingleAsync();
            Assert.Equal(200m, row.TotalCost);
            Assert.StartsWith("SI-", row.ReferenceNumber);
            Assert.Equal(_w.AdminId, row.ReceivedByUserId);
            Assert.True(await _w.AuditContainsAsync("Stock-In"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task StockIn_QuantityMustBePositive(double qty)
        {
            var item = await _w.AddItemAsync("Paper", 10);

            var r = await _stock.RecordStockInAsync(new StockInInput { ItemId = item, Quantity = (decimal)qty }, _w.AdminId);

            Assert.False(r.Success);
            Assert.Equal(10m, await _w.QuantityAsync(item));
        }

        [Fact]
        public async Task StockIn_NegativeCost_IsRejected()
        {
            var item = await _w.AddItemAsync("Paper", 10);
            var r = await _stock.RecordStockInAsync(new StockInInput { ItemId = item, Quantity = 5, UnitCost = -1m }, _w.AdminId);

            Assert.False(r.Success);
            Assert.Equal(10m, await _w.QuantityAsync(item));
        }

        [Fact]
        public async Task StockIn_FutureDate_IsRejected()
        {
            var item = await _w.AddItemAsync("Paper", 10);
            var r = await _stock.RecordStockInAsync(
                new StockInInput { ItemId = item, Quantity = 5, Date = DateTime.Today.AddDays(1) }, _w.AdminId);

            Assert.False(r.Success);
            Assert.Equal(10m, await _w.QuantityAsync(item));
        }

        [Fact]
        public async Task StockIn_InactiveItem_IsRejected()
        {
            var item = await _w.AddItemAsync("Old stock", 10);
            await _w.SetItemStatusAsync(item, RecordStatus.Inactive);

            var r = await _stock.RecordStockInAsync(new StockInInput { ItemId = item, Quantity = 5 }, _w.AdminId);

            Assert.False(r.Success);
            Assert.Equal(10m, await _w.QuantityAsync(item));
        }

        [Fact]
        public async Task StockOut_DecreasesStock_AndRecordsUsageLinkedToTheStockOut()
        {
            var item = await _w.AddItemAsync("A4 Bond Paper", 100);

            var r = await _stock.RecordStockOutAsync(Out(item, 30, "Photo Print Order - Customer #002"), _w.CashierId);

            Assert.True(r.Success, r.Error);
            Assert.Equal(70m, await _w.QuantityAsync(item));

            await using var db = _w.Db.CreateDbContext();
            var stockOut = await db.StockOuts.SingleAsync();
            var usage = await db.UsageTransactions.SingleAsync();
            Assert.Equal(30m, stockOut.Quantity);
            Assert.Equal(30m, usage.QuantityUsed);
            Assert.Equal(stockOut.StockOutId, usage.StockOutId);
            Assert.Equal("Photo Print Order - Customer #002", usage.JobReference);
            Assert.Equal(_w.CashierId, usage.RecordedByUserId);
            Assert.True(await _w.AuditContainsAsync("Stock-Out"));
        }

        [Fact]
        public async Task StockOut_MoreThanAvailable_IsRejected_AndNothingChanges()
        {
            var item = await _w.AddItemAsync("A4 Bond Paper", 10);

            var r = await _stock.RecordStockOutAsync(Out(item, 11), _w.CashierId);

            Assert.False(r.Success);
            Assert.Contains("Insufficient stock", r.Error);
            Assert.Equal(10m, await _w.QuantityAsync(item));

            await using var db = _w.Db.CreateDbContext();
            Assert.Empty(await db.StockOuts.ToListAsync());
            Assert.Empty(await db.UsageTransactions.ToListAsync());
        }

        [Fact]
        public async Task StockOut_ExactlyAllStock_IsAllowed()
        {
            var item = await _w.AddItemAsync("A4 Bond Paper", 10);

            var r = await _stock.RecordStockOutAsync(Out(item, 10), _w.CashierId);

            Assert.True(r.Success, r.Error);
            Assert.Equal(0m, await _w.QuantityAsync(item));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task StockOut_RequiresACustomerOrJobReference(string job)
        {
            var item = await _w.AddItemAsync("Paper", 10);
            var r = await _stock.RecordStockOutAsync(Out(item, 1, job), _w.CashierId);

            Assert.False(r.Success);
            Assert.Equal(10m, await _w.QuantityAsync(item));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public async Task StockOut_QuantityMustBePositive(double qty)
        {
            var item = await _w.AddItemAsync("Paper", 10);
            var r = await _stock.RecordStockOutAsync(Out(item, (decimal)qty), _w.CashierId);

            Assert.False(r.Success);
            Assert.Equal(10m, await _w.QuantityAsync(item));
        }

        [Fact]
        public async Task StockOut_InactiveItem_IsRejected()
        {
            var item = await _w.AddItemAsync("Old stock", 10);
            await _w.SetItemStatusAsync(item, RecordStatus.Inactive);

            var r = await _stock.RecordStockOutAsync(Out(item, 1), _w.CashierId);

            Assert.False(r.Success);
        }

        [Fact]
        public async Task SeveralTransactions_KeepInventoryEqualToInMinusOut()
        {
            var item = await _w.AddItemAsync("Paper", 0);

            await _stock.RecordStockInAsync(new StockInInput { ItemId = item, Quantity = 100 }, _w.AdminId);
            await _stock.RecordStockOutAsync(Out(item, 30), _w.CashierId);
            await _stock.RecordStockOutAsync(Out(item, 20), _w.CashierId);
            await _stock.RecordStockInAsync(new StockInInput { ItemId = item, Quantity = 5 }, _w.AdminId);
            await _stock.RecordStockOutAsync(Out(item, 999), _w.CashierId); // rejected, must not count

            await using var db = _w.Db.CreateDbContext();
            var stockIn = (await db.StockIns.ToListAsync()).Sum(s => s.Quantity);
            var stockOut = (await db.StockOuts.ToListAsync()).Sum(s => s.Quantity);
            var usage = (await db.UsageTransactions.ToListAsync()).Sum(u => u.QuantityUsed);

            Assert.Equal(55m, await _w.QuantityAsync(item));
            Assert.Equal(stockIn - stockOut, await _w.QuantityAsync(item));
            Assert.Equal(stockOut, usage);
        }
    }

    // =====================================================================================
    // Items, categories, suppliers
    // =====================================================================================
    public class CatalogServiceTests : IAsyncLifetime
    {
        private World _w = null!;
        private ItemService _items = null!;
        private CategoryService _categories = null!;
        private SupplierService _suppliers = null!;

        public async Task InitializeAsync()
        {
            _w = await World.CreateAsync();
            _items = new ItemService(_w.Db, _w.Audit);
            _categories = new CategoryService(_w.Db, _w.Audit);
            _suppliers = new SupplierService(_w.Db, _w.Audit);
        }

        public Task DisposeAsync()
        {
            _w.Dispose();
            return Task.CompletedTask;
        }

        private ItemInput NewItem(string name = "A4 Bond Paper", string sku = "PAP-A4") => new()
        {
            ItemName = name, SKU = sku, CategoryId = _w.CategoryId, SupplierId = _w.SupplierId,
            UnitOfMeasure = "sheet", UnitCost = 0.6m, SafetyStock = 100, ReorderPoint = 300, ReorderQuantity = 500
        };

        [Fact]
        public async Task NewItem_GetsAnInventoryRowWithZeroStock()
        {
            var r = await _items.SaveAsync(NewItem(), _w.AdminId);

            Assert.True(r.Success, r.Error);
            Assert.Equal(0m, await _w.QuantityAsync(r.Id!.Value));
            Assert.True(await _w.AuditContainsAsync("Add Item"));
        }

        [Fact]
        public async Task DuplicateSku_IsRejected_EvenWithDifferentCase()
        {
            await _items.SaveAsync(NewItem(sku: "PAP-A4"), _w.AdminId);

            var r = await _items.SaveAsync(NewItem("Other paper", "pap-a4"), _w.AdminId);

            Assert.False(r.Success);
            Assert.Contains("already", r.Error);
        }

        [Fact]
        public async Task EditingAnItem_KeepsItsOwnSku()
        {
            var created = await _items.SaveAsync(NewItem(), _w.AdminId);

            var edit = NewItem("A4 Bond Paper (renamed)");
            edit.ItemId = created.Id!.Value;
            var r = await _items.SaveAsync(edit, _w.AdminId);

            Assert.True(r.Success, r.Error);
        }

        [Fact]
        public async Task ItemWithInactiveCategory_IsRejected()
        {
            await using (var db = _w.Db.CreateDbContext())
            {
                (await db.Categories.SingleAsync()).Status = RecordStatus.Inactive;
                await db.SaveChangesAsync();
            }

            var r = await _items.SaveAsync(NewItem(), _w.AdminId);

            Assert.False(r.Success);
        }

        [Theory]
        [InlineData("unit")]
        [InlineData("safety")]
        [InlineData("reorderpoint")]
        [InlineData("reorderqty")]
        public async Task NegativeNumbers_AreRejected(string field)
        {
            var input = NewItem();
            switch (field)
            {
                case "unit": input.UnitCost = -1; break;
                case "safety": input.SafetyStock = -1; break;
                case "reorderpoint": input.ReorderPoint = -1; break;
                default: input.ReorderQuantity = -1; break;
            }

            var r = await _items.SaveAsync(input, _w.AdminId);

            Assert.False(r.Success);
            Assert.Contains("negative", r.Error);
        }

        [Fact]
        public async Task MissingNameOrUnit_IsRejected()
        {
            var noName = NewItem();
            noName.ItemName = "  ";
            var noUnit = NewItem();
            noUnit.UnitOfMeasure = string.Empty;

            Assert.False((await _items.SaveAsync(noName, _w.AdminId)).Success);
            Assert.False((await _items.SaveAsync(noUnit, _w.AdminId)).Success);
        }

        [Fact]
        public async Task InactiveItem_CannotBeReactivated_WhileItsCategoryIsInactive()
        {
            var created = await _items.SaveAsync(NewItem(), _w.AdminId);
            var id = created.Id!.Value;

            Assert.True((await _items.SetActiveAsync(id, false, _w.AdminId)).Success);
            Assert.True((await _categories.SetActiveAsync(_w.CategoryId, false, _w.AdminId)).Success);

            var r = await _items.SetActiveAsync(id, true, _w.AdminId);

            Assert.False(r.Success);
        }

        [Fact]
        public async Task Category_DuplicateName_IsRejected_IgnoringCase()
        {
            var r = await _categories.SaveAsync(new CategoryInput { CategoryName = "PAPER" }, _w.AdminId);

            Assert.False(r.Success);
        }

        [Fact]
        public async Task Category_WithActiveItems_CannotBeDeactivated()
        {
            var created = await _items.SaveAsync(NewItem(), _w.AdminId);

            var blocked = await _categories.SetActiveAsync(_w.CategoryId, false, _w.AdminId);
            Assert.False(blocked.Success);

            await _items.SetActiveAsync(created.Id!.Value, false, _w.AdminId);
            var allowed = await _categories.SetActiveAsync(_w.CategoryId, false, _w.AdminId);
            Assert.True(allowed.Success, allowed.Error);
        }

        [Fact]
        public async Task Category_ListShowsItemCount()
        {
            await _items.SaveAsync(NewItem(), _w.AdminId);

            var row = Assert.Single(await _categories.ListAsync());

            Assert.Equal(1, row.ItemCount);
        }

        [Fact]
        public async Task Supplier_WithActiveItems_CannotBeDeactivated()
        {
            await _items.SaveAsync(NewItem(), _w.AdminId);

            var r = await _suppliers.SetActiveAsync(_w.SupplierId, false, _w.AdminId);

            Assert.False(r.Success);
        }

        [Fact]
        public async Task Supplier_InvalidEmail_IsRejected()
        {
            var r = await _suppliers.SaveAsync(new SupplierInput { SupplierName = "New Supplier", Email = "not-an-email", LeadTimeDays = 3 }, _w.AdminId);

            Assert.False(r.Success);
        }

        [Fact]
        public async Task Supplier_LeadTimeOutOfRange_IsRejected()
        {
            var r = await _suppliers.SaveAsync(new SupplierInput { SupplierName = "New Supplier", LeadTimeDays = 400 }, _w.AdminId);

            Assert.False(r.Success);
        }

        [Fact]
        public async Task Supplier_DuplicateName_IsRejected()
        {
            var r = await _suppliers.SaveAsync(new SupplierInput { SupplierName = "test supplier", LeadTimeDays = 3 }, _w.AdminId);

            Assert.False(r.Success);
        }
    }

    public class DeleteTests : IAsyncLifetime
    {
        private World _w = null!;
        private ItemService _items = null!;
        private CategoryService _categories = null!;

        public async Task InitializeAsync()
        {
            _w = await World.CreateAsync();
            _items = new ItemService(_w.Db, _w.Audit);
            _categories = new CategoryService(_w.Db, _w.Audit);
        }

        public Task DisposeAsync()
        {
            _w.Dispose();
            return Task.CompletedTask;
        }

        [Fact]
        public async Task ItemWithoutHistory_CanBeDeleted_TogetherWithItsStockRow()
        {
            var id = await _w.AddItemAsync("Never used", quantity: 0);

            var r = await _items.DeleteAsync(id, _w.AdminId);

            Assert.True(r.Success, r.Error);
            await using var db = _w.Db.CreateDbContext();
            Assert.Empty(await db.Items.ToListAsync());
            Assert.Empty(await db.Inventories.ToListAsync());
            Assert.True(await _w.AuditContainsAsync("Delete Item"));
        }

        [Fact]
        public async Task ItemWithStockHistory_CannotBeDeleted()
        {
            var id = await _w.AddItemAsync("Has history", quantity: 0);
            var stock = new StockService(_w.Db, _w.Audit);
            await stock.RecordStockInAsync(new StockInInput { ItemId = id, Quantity = 10 }, _w.AdminId);
            await stock.RecordStockOutAsync(new StockOutInput { ItemId = id, Quantity = 10, JobReference = "Job" }, _w.CashierId); // stock back to 0

            var r = await _items.DeleteAsync(id, _w.AdminId);

            Assert.False(r.Success);
            Assert.Contains("deactivate", r.Error);
            await using var db = _w.Db.CreateDbContext();
            Assert.Single(await db.Items.ToListAsync());
        }

        [Fact]
        public async Task ItemWithStockOnHand_CannotBeDeleted()
        {
            var id = await _w.AddItemAsync("Has stock", quantity: 25);

            Assert.False((await _items.DeleteAsync(id, _w.AdminId)).Success);
        }

        [Fact]
        public async Task DeletingAnItem_AlsoRemovesItsSubstituteLinks()
        {
            var a = await _w.AddItemAsync("A", 0);
            var b = await _w.AddItemAsync("B", 0);
            var subs = new SubstituteService(_w.Db, _w.Audit, _w.Auth);
            await subs.AddAsync(a, b, null, _w.AdminId);

            var r = await _items.DeleteAsync(b, _w.AdminId);

            Assert.True(r.Success, r.Error);
            Assert.Empty(await subs.GetLinksAsync(a));
        }

        [Fact]
        public async Task CategoryWithItems_CannotBeDeleted_ButAnEmptyOneCan()
        {
            await _w.AddItemAsync("In Paper", 0);
            Assert.False((await _categories.DeleteAsync(_w.CategoryId, _w.AdminId)).Success);

            var empty = await _categories.SaveAsync(new CategoryInput { CategoryName = "Empty" }, _w.AdminId);
            var r = await _categories.DeleteAsync(empty.Id!.Value, _w.AdminId);

            Assert.True(r.Success, r.Error);
            Assert.True(await _w.AuditContainsAsync("Delete Category"));
        }
    }

    // =====================================================================================
    // Material substitution
    // =====================================================================================
    public class SubstituteServiceTests : IAsyncLifetime
    {
        private World _w = null!;
        private SubstituteService _subs = null!;

        public async Task InitializeAsync()
        {
            _w = await World.CreateAsync();
            _subs = new SubstituteService(_w.Db, _w.Audit, _w.Auth);
        }

        public Task DisposeAsync()
        {
            _w.Dispose();
            return Task.CompletedTask;
        }

        private async Task<int> AddCategoryAsync(string name)
        {
            await using var db = _w.Db.CreateDbContext();
            var c = new Category { CategoryName = name };
            db.Categories.Add(c);
            await db.SaveChangesAsync();
            return c.CategoryId;
        }

        [Fact]
        public async Task Suggestions_ListConfiguredFirst_ThenSameCategory_AndSkipEmptyStock()
        {
            var wanted = await _w.AddItemAsync("Glossy Photo Paper", quantity: 0);
            var sameCategory = await _w.AddItemAsync("Premium Photo Paper", quantity: 50);
            var sameCategoryEmpty = await _w.AddItemAsync("Matte Photo Paper", quantity: 0);
            var inkCategory = await AddCategoryAsync("Ink");
            var linked = await _w.AddItemAsync("Special Sheet", quantity: 70, categoryId: inkCategory);

            Assert.True((await _subs.AddAsync(wanted, linked, "works for flyers", _w.AdminId)).Success);

            var names = (await _subs.GetSuggestionsAsync(wanted)).Select(s => s.Name).ToList();

            Assert.Equal(new[] { "Special Sheet", "Premium Photo Paper" }, names);
        }

        [Fact]
        public async Task AnItem_CannotBeItsOwnSubstitute()
        {
            var a = await _w.AddItemAsync("A", 10);

            Assert.False((await _subs.AddAsync(a, a, null, _w.AdminId)).Success);
        }

        [Fact]
        public async Task DuplicateLink_IsRejected()
        {
            var a = await _w.AddItemAsync("A", 10);
            var b = await _w.AddItemAsync("B", 10);

            Assert.True((await _subs.AddAsync(a, b, null, _w.AdminId)).Success);
            Assert.False((await _subs.AddAsync(a, b, null, _w.AdminId)).Success);
        }

        [Fact]
        public async Task OnlyPeopleWhoManageItems_CanChangeLinks()
        {
            var a = await _w.AddItemAsync("A", 10);
            var b = await _w.AddItemAsync("B", 10);
            _w.Auth.SignInAs(Services.RoleNames.SalesCashier, _w.CashierId);

            Assert.False((await _subs.AddAsync(a, b, null, _w.CashierId)).Success);
        }

        [Fact]
        public async Task RemovedLink_NoLongerListed()
        {
            var a = await _w.AddItemAsync("A", 10);
            var b = await _w.AddItemAsync("B", 10);
            await _subs.AddAsync(a, b, null, _w.AdminId);

            var link = Assert.Single(await _subs.GetLinksAsync(a));
            Assert.True((await _subs.RemoveAsync(link.LinkId, _w.AdminId)).Success);

            Assert.Empty(await _subs.GetLinksAsync(a));
        }
    }
}
