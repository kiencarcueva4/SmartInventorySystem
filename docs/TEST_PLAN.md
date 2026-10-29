# Smart Inventory System - Test Plan (Phase 9)

This document has three parts:

* **Part A** - the automated tests (run them with one click).
* **Part B** - the manual test checklist (things a person must see with their own eyes).
* **Part C** - hand-calculation worksheets (to prove the formulas by hand) and known design notes.

---

## Part A - Automated tests

**How to run:** open `SmartInventorySystem.sln` in Visual Studio 2022, then **Test > Run All Tests**
(or open **Test > Test Explorer** and press the green *Run All* button).
The tests use a throw-away in-memory SQLite database built from the same model, constraints and
collations as the real database. They never touch your real data.

| Test file | What it proves |
|---|---|
| `CalculatorTests.cs` | Reorder rules (including the A4 Bond Paper example = 450 sheets), stock status rules, Weighted Moving Average, Exponential Smoothing, forecast error (MAE / MAPE), trend direction, movement thresholds, ageing risk, stock reconstruction, password policy and hashing, role permissions, menu permissions, CSV export safety |
| `StockAndCatalogTests.cs` | Stock-In / Stock-Out maths and validation, stock never exceeds history, usage records, items / categories / suppliers rules (duplicates, inactive parents, negatives), material substitution |
| `AuthAndUserTests.cs` | Login, wrong password, unknown user, inactive account, lockout after 5 failures, logout, forced password change, password change rules, user management and last-administrator protection |
| `ReorderAndAlertTests.cs` | Recommendation creation, owner approve / defer / dismiss, only-the-owner rule, no duplicate suggestions, no automatic purchasing, restock status flow, alert creation / closing / acknowledging, settings validation |
| `IntegrityAndPipelineTests.cs` | Database rules (negative stock, duplicate SKU / username, one inventory row per item, zero quantity, self-substitute), and the whole analytics pipeline on the real seed data (stock = in - out for every item, movement totals match the records, weekly trend rows balance, forecasts can be reproduced by hand, no duplicate alerts) |

**What a pass means:** the business rules and calculations behave as written.
**What it does not cover:** screens, buttons, colours, printing - that is Part B.

---

## Part B - Manual test checklist

Run the app (F5). Tick **Pass** or **Fail** and write the date and any notes.
Dev accounts: `admin / Admin@123`, `inventory / Inventory@123`, `cashier / Cashier@123`
(each must choose a new password at first sign-in - write it down).

### B1. Login and sessions

| ID | Steps | Expected result | Result |
|---|---|---|---|
| L-01 | Sign in as `admin` with the right password | Dashboard opens; top bar shows your name and role | |
| L-02 | Sign in with a wrong password | "Invalid username or password." and no hint about which part was wrong | |
| L-03 | Sign in with an unknown username | The same message as L-02 | |
| L-04 | Leave both fields empty and press Sign in | Validation messages; no sign-in | |
| L-05 | Type the username in capitals (`ADMIN`) | Sign-in works | |
| L-06 | Enter 5 wrong passwords in a row, then the right one | Message says too many attempts; works again after about a minute | |
| L-07 | First sign-in of a seeded account | Forced to Change password before anything else | |
| L-08 | Change password with a weak new password (`abc`) | Rejected with a clear message | |
| L-09 | Change password correctly, log out, sign in with the new one | Works; the old password no longer works | |
| L-10 | Click Log out | Login page appears; pressing Back / typing `/items` does not show data | |

### B2. Roles and access

| ID | Steps | Expected result | Result |
|---|---|---|---|
| R-01 | Sign in as `cashier` | Sidebar shows only Dashboard, Inventory Monitoring, Stock-Out | |
| R-02 | As cashier, open Stock-Out and record a usage | Works | |
| R-03 | As cashier, try to reach Items / Users / Reports (no link is shown - the address is not reachable) | Access denied or page not found; no data shown | |
| R-04 | Sign in as `inventory` | Sees inventory, analytics, reorder, suppliers, alerts, reports; does NOT see Users, Settings, Audit Logs | |
| R-05 | As inventory, open a recommendation | Can read it; no Approve / Defer / Dismiss buttons | |
| R-06 | As inventory, open Demand Forecasting | Weights are visible but greyed out (read-only) | |
| R-07 | Sign in as `admin` | Sees every menu item | |

### B3. Categories, suppliers, items

| ID | Steps | Expected result | Result |
|---|---|---|---|
| C-01 | Add a category; add another with the same name in capitals | Second is refused (duplicate) | |
| C-02 | Deactivate a category that still has active items | Refused with an explanation | |
| C-03 | Add a supplier with lead time 400 | Refused (0-365 days) | |
| C-04 | Add a supplier with an invalid email | Refused | |
| C-05 | Add an item with a new SKU | Saved; shows 0 stock | |
| C-06 | Add another item with the same SKU (different case) | Refused | |
| C-07 | Add an item with a negative cost / safety stock / reorder point | Refused | |
| C-08 | Edit an item and save without changing the SKU | Saved | |
| C-09 | Deactivate an item, then try Stock-In for it | Refused (item is inactive) | |
| C-10 | Add a substitute on the Items page; add the same one again | Second is refused | |

### B4. Stock-In and Stock-Out (check the numbers!)

| ID | Steps | Expected result | Result |
|---|---|---|---|
| S-01 | Note an item's stock (e.g. 100). Stock-In 50 | Inventory Monitoring shows 150 | |
| S-02 | Stock-Out 30 with job reference "Business Card Printing - Customer #001" | Stock is now 120; appears in recent stock-out | |
| S-03 | Stock-Out more than is available | Clear error; stock unchanged | |
| S-04 | Stock-Out exactly all remaining stock | Allowed; status becomes Out of Stock | |
| S-05 | Stock-Out with an empty job reference | Refused | |
| S-06 | Stock-In with quantity 0 or negative | Refused | |
| S-07 | Stock-In dated tomorrow | Refused | |
| S-08 | Stock-Out when stock is short: look under the quantity box | Possible alternatives are listed; nothing changes until you click Use instead | |
| S-09 | After S-01 to S-04, add the Stock-In total minus the Stock-Out total by hand | Equals the stock shown | |

### B5. Dashboard and monitoring

| ID | Steps | Expected result | Result |
|---|---|---|---|
| D-01 | Compare Dashboard "Low / critical" and "Out of stock" with Inventory Monitoring (filter by status) | Same counts | |
| D-02 | Inventory Monitoring: search, filter by category / status / movement, click column headers to sort | Each works; counts shown below the table are right | |
| D-03 | Record a stock-out, press Refresh on the Dashboard | Usage chart and recent stock-out update | |
| D-04 | As cashier, open the Dashboard | No inventory value, no analytics panels | |

### B6. Analytics

| ID | Steps | Expected result | Result |
|---|---|---|---|
| A-01 | Movement: run the 90-day analysis | Every item gets a class, turnover, risk | |
| A-02 | Pick one item; add up its usage by hand for the range (see worksheet C1) | Matches "Total consumption" | |
| A-03 | Movement: filter by category, item and classification | Table narrows correctly | |
| A-04 | Forecasting: Back-test last 6 periods (Weighted Moving Average) | Forecasts with Actual, Error and % error appear; MAPE card has a value | |
| A-05 | Recalculate one forecast by hand (worksheet C2) | Matches the forecast value | |
| A-06 | As admin set weights 0.5 / 0.5 / 0.5 and Save | Refused: must total 1.00 | |
| A-07 | Switch to Simple Exponential Smoothing and forecast | Forecasts created; labelled with the method | |
| A-08 | Trends: generate the last 90 days; choose one item | Two charts and a weekly table; Beginning + Stock-in - Stock-out = Ending in every row | |

### B7. Reordering

| ID | Steps | Expected result | Result |
|---|---|---|---|
| O-01 | Recommendations: click Evaluate inventory | Message summarises urgent / recommended / not necessary | |
| O-02 | Open one recommendation; compare with worksheet C3 | Quantity and priority match your hand calculation | |
| O-03 | Press Evaluate inventory again | No duplicate recommendations | |
| O-04 | As admin: Approve with a quantity and date | Status Approved; a Restock record is created; **nothing is ordered automatically** | |
| O-05 | Try to approve the same recommendation again | Not possible | |
| O-06 | Defer one with a note, then Evaluate again | Stays deferred; note still shown | |
| O-07 | Dismiss one with a note | Recorded with your name and the date | |
| O-08 | Restock Records: Planned -> Ordered -> Received | Each step works; skipping a step is refused | |
| O-09 | After Received, record the delivery in Stock-In, Evaluate again | The suggestion disappears | |

### B8. Alerts

| ID | Steps | Expected result | Result |
|---|---|---|---|
| AL-01 | Open Alerts | Low-stock / risk alerts match Inventory Monitoring and the Dashboard | |
| AL-02 | Acknowledge one alert | Moves to "Acknowledged by <you>" | |
| AL-03 | Fix a low-stock item with a Stock-In, press Check now | Its alert is closed ("Resolved") | |
| AL-04 | Dashboard "Open alerts" card | Equals the open count on the Alerts page | |

### B9. Reports

| ID | Steps | Expected result | Result |
|---|---|---|---|
| P-01 | Generate each of the 5 reports | A table appears; row counts make sense | |
| P-02 | Filter a report by category | Only that category | |
| P-03 | Press Print | Print preview shows the report only (no sidebar / buttons) | |
| P-04 | Press Export to CSV, open the file in Excel | Opens with correct columns and special characters | |

### B10. Users, settings, audit

| ID | Steps | Expected result | Result |
|---|---|---|---|
| U-01 | Add a user, sign in as them | Forced to choose a new password | |
| U-02 | Add a user with an existing username (different case) | Refused | |
| U-03 | Try to deactivate your own account | Refused | |
| U-04 | Reset a user's password | They must change it at next sign-in | |
| U-05 | Settings: enter an invalid value (letters, out of range) | Refused; nothing saved | |
| U-06 | Settings: change one value | Saved; the change is visible in Audit Logs with old and new value | |
| U-07 | Audit Logs: filter by user / action / date | Rows match; the log cannot be edited | |
| U-08 | Check the audit log after S-01..S-04 | Stock-In and Stock-Out entries with your name | |

### B11. Errors and robustness

| ID | Steps | Expected result | Result |
|---|---|---|---|
| E-01 | Close the app while the database is open elsewhere, or rename the database and start | A friendly message; details in `Documents\SmartInventorySystem\logs` | |
| E-02 | Trigger any validation error | Plain-language message; no technical text (SQL, stack trace) is shown | |
| E-03 | Start the app with the database file deleted | A fresh database is created and seeded | |
| E-04 | Open every page once as admin | No page crashes or shows a blank screen | |

---

## Part C - Hand-calculation worksheets

### C1. Total consumption (movement analysis)

Add every Stock-Out quantity of one item between the "From" and "To" dates (Stock-Out list or the usage
report). It must equal *Total consumption*. *Average daily usage* = total / number of days in the range.
*Turnover rate* = total / average stock level in the range.

### C2. Weighted Moving Average

With the default period of 7 days and weights 0.50 / 0.30 / 0.20:

1. Add the item's consumption for the 7 days just before the forecast period (**D1**), the 7 days before that (**D2**) and the 7 days before that (**D3**).
2. **Forecast = D1 x 0.50 + D2 x 0.30 + D3 x 0.20**

Example: D1 = 100, D2 = 80, D3 = 60 -> 50 + 24 + 12 = **86**.
The same three numbers are shown in the *Historical consumption* column of the forecast table.

**Forecast error:** Error = |Actual - Forecast|. % error = Error / Actual x 100. MAPE = the average % error.
Example: forecast 90, actual 100 -> error 10, 10 %.

### C3. Reorder recommendation

1. Daily demand **d** = forecast for the period / period length (example: 70 / 7 = 10 per day).
2. Lead-time demand = d x lead time (10 x 5 = 50).
3. Target level = lead-time demand + safety stock (50 + 5 = 55).
4. Recommended quantity = target level - current stock, never below zero, rounded up, and at least the item's minimum order quantity (55 - 20 = **35**).
5. Priority:
   * **URGENT**: stock is 0, or at/below safety stock, or lower than the lead-time demand.
   * **RECOMMENDED**: stock is at/below the reorder point, or below the target level.
   * **NOT YET NECESSARY**: otherwise.

Specification example: stock 250, safety stock 100, lead time 5 days, demand 120 per day -> lead-time demand 600, target 700,
**recommended 450**, priority **URGENT** (250 is lower than 600).

### C4. Stock status

| Status | Rule |
|---|---|
| Out of Stock | quantity is 0 or less |
| Critical | quantity at or below safety stock |
| Low Stock | quantity at or below reorder point |
| Overstock | quantity above reorder point x overstock multiplier (default 3) |
| In Stock | everything else |

---

## Known design notes (not bugs, but worth knowing and mentioning in your documentation)

1. **Forecasts are estimates.** They are never shown as guaranteed values, and the back-test shows how far off they were.
2. **A dismissed recommendation can come back.** Dismissing records the owner's decision, but the next evaluation will suggest the item again if it is still short of stock.
3. **Receiving a restock does not change stock.** The delivery is recorded on the Stock-In page, so stock is never counted twice.
4. **There is no automatic sign-out after inactivity.** Log out when you leave the computer.
5. **The database lives in your Documents folder.** If Documents is synced by OneDrive, move the database (see `AppPaths.cs`) to a folder that is not synced.
6. **Printing and CSV export use the Windows print and Save dialogs**, so they need a real printer / file location to test fully.
7. **Demo data is generated.** The seeded 90-day history is artificial; replace it with real transactions before real use (set `SeedDemoHistory = false` in `DatabaseSeeder.cs`).
