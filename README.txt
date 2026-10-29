SMART INVENTORY SYSTEM - Phase 1 + 2

1. Unzip this folder anywhere (e.g. C:\Projects).
2. Double-click SmartInventorySystem.sln (opens in Visual Studio 2022).
   Needs the ".NET desktop development" workload.
3. Press F5. First run restores NuGet packages (needs internet), creates the
   database and seeds the data automatically.

Database: Documents\SmartInventorySystem\smart_inventory.db (C:\Users\<you>\Documents\SmartInventorySystem)
Logs:     Documents\SmartInventorySystem\logs

Dev logins (for Phase 3 login page):
  admin / Admin@123    inventory / Inventory@123    cashier / Cashier@123

LATER - to switch to EF Core migrations (recommended before Phase 3):
  1. Close the app and delete smart_inventory.db (path above).
  2. Tools > NuGet Package Manager > Package Manager Console:
         Add-Migration InitialCreate
  3. Press F5 again. The app now applies the migration instead.

NUGET PACKAGES
  They are not stored in this folder. Visual Studio downloads them automatically
  the first time you Build / Rebuild (internet needed once) and keeps them in
  %UserProfile%\.nuget\packages. nuget.config in this folder points to nuget.org.
  If a restore error appears: right-click the solution > "Restore NuGet Packages",
  then Build > Rebuild Solution.

PHASE 3 - LOGIN AND ROLES
  Start screen is now the Login page. Dev accounts:
    admin / Admin@123   inventory / Inventory@123   cashier / Cashier@123
  Each account must choose a new password at first sign-in (remember it!).
  New password rules: 8+ characters, at least one letter and one number.
  5 wrong passwords = 60-second lockout for that username.
  Role access is defined in one place: Services/Auth/Permissions.cs
  Sidebar pages are defined in:        Services/Auth/NavCatalog.cs
  Login, logout, failed login and password change are written to Audit_Logs.

PHASE 4 - CORE INVENTORY
  Pages: Inventory Monitoring, Items, Categories, Suppliers, Stock-In, Stock-Out.
  - New items start with 0 stock; add stock with Stock-In.
  - Stock-Out needs a customer/job reference and cannot exceed available stock.
  - Every stock change saves the transaction and the new quantity together (all or nothing).
  - No entity changes in this phase, so no new migration is needed.
  - Demo data may show some items as "Overstock": the seeded restock sizes are large.

PHASE 5 - ANALYTICAL DASHBOARD
  The Dashboard now shows real data: stock status, low-stock list, stockout risk
  (days of stock left vs supplier lead time), usage per day, top materials by value,
  and recent stock-in/stock-out. What each role sees depends on its permissions.
  Movement summary, trend summary and recommendations show "not available yet"
  until Phases 6 and 7 fill their tables. No database changes (no new migration).

PHASE 6 - ANALYTICS
  Fast/Slow-Moving Analysis, Demand Forecasting (Weighted Moving Average, optional
  Simple Exponential Smoothing) and Inventory Trends. Click the "Run / Generate" buttons;
  results are stored in the Movement_Classifications, Forecasts and Inventory_Trends tables
  and automatically appear on the Dashboard. Use "Back-test" on the forecasting page to see
  forecast accuracy (MAE / MAPE) right away. No new migration is needed.

PHASE 7 - REORDERING
  Recommendations: click "Evaluate inventory" to check every item. Urgent / Recommended items
  are saved for the owner; "Not yet necessary" items are not. The owner (admin) opens a
  recommendation and chooses Approve, Defer or Dismiss. Approve creates a Restock record.
  Restock Records: mark Ordered / Received / Cancelled yourself - the system never orders.
  Receiving does not change stock; record the delivery on the Stock-In page.
  No new migration is needed.

PHASE 8 - SUPPORTING FEATURES
  Alerts Center (low stock, urgent reorder, stockout risk, overstock, slow-moving, ageing risk,
  forecast warning) - refreshed whenever the Dashboard or Alerts page opens.
  Reports (5 types) with Print and Export to CSV. Users (admin): add, edit, reset password,
  activate/deactivate. System Settings (admin): all business rules. Audit Logs (admin):
  filter and page through the trail. Material substitution: manage per item on the Items page;
  suggestions appear on Stock-Out when stock is short (never applied automatically).
  No new migration is needed.

PHASE 9 - TESTING
  Automated tests: Test > Run All Tests (project SmartInventorySystem.Tests).
  Manual checklist and hand-calculation worksheets: docs\TEST_PLAN.md

BUTTONS AND ERRORS
  All buttons were checked: every one has a working action. Dashboard cards are now clickable
  (Low / critical and Out of stock open Inventory Monitoring already filtered). If anything
  unexpected happens on a page, a friendly "Something went wrong - Try again" panel is shown
  instead of the app closing, and the details are written to Documents\SmartInventorySystem\logs.

DELETE
  Items, Categories and Users now have a Delete button (with a confirmation). Only records that
  have no history can be deleted: an item with stock transactions, a category with items, or a
  user who has ever signed in / recorded anything must be Deactivated instead, so the history
  and audit trail are never damaged.
