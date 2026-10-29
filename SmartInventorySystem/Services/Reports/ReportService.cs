using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Infrastructure;
using SmartInventorySystem.Services.Analytics;
using SmartInventorySystem.Services.Auth;
using SmartInventorySystem.Services.Core;
using SmartInventorySystem.Services.Reordering;

namespace SmartInventorySystem.Services.Reports
{
    public sealed record ReportRequest(ReportType Type, int CategoryId, DateTime? From, DateTime? To);

    public sealed record ReportTable(string Title, string Subtitle, IReadOnlyList<string> Columns,
        IReadOnlyList<IReadOnlyList<string>> Rows, DateTime GeneratedAt);

    public sealed record ReportResult(bool Success, string? Error, ReportTable? Table);

    public interface IReportService
    {
        Task<ReportResult> BuildAsync(ReportRequest request, int userId);
        string ToCsv(ReportTable table);
    }

    public interface IExportService
    {
        /// <summary>Shows a "Save as" dialog and writes the text. Returns the saved path, or null if cancelled.</summary>
        string? SaveCsv(string suggestedFileName, string content);
    }

    public sealed class ExportService : IExportService
    {
        public string? SaveCsv(string suggestedFileName, string content)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = suggestedFileName,
                Filter = "CSV file (*.csv)|*.csv",
                DefaultExt = ".csv",
                AddExtension = true
            };

            if (dialog.ShowDialog() != true) return null;

            // UTF-8 with a byte-order mark so Excel opens special characters correctly.
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(true));
            return dialog.FileName;
        }
    }

    /// <summary>Builds the five reports from the same services the screens use, so the numbers always agree.</summary>
    public sealed class ReportService : IReportService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IAuditService _audit;
        private readonly IAuthService _auth;
        private readonly IInventoryService _inventory;
        private readonly IMovementService _movement;
        private readonly IForecastService _forecast;
        private readonly ITrendService _trends;
        private readonly IReorderService _reorder;

        public ReportService(IDbContextFactory<AppDbContext> factory, IAuditService audit, IAuthService auth,
            IInventoryService inventory, IMovementService movement, IForecastService forecast,
            ITrendService trends, IReorderService reorder)
        {
            _factory = factory;
            _audit = audit;
            _auth = auth;
            _inventory = inventory;
            _movement = movement;
            _forecast = forecast;
            _trends = trends;
            _reorder = reorder;
        }

        public static string TypeName(ReportType t) => t switch
        {
            ReportType.InventoryStatus => "Inventory Status",
            ReportType.MovementClassification => "Movement Classification",
            ReportType.DemandForecast => "Demand Forecast",
            ReportType.InventoryTrends => "Inventory Trends",
            ReportType.ReorderHistory => "Reorder History",
            _ => t.ToString()
        };

        public static bool UsesDateRange(ReportType t) =>
            t is ReportType.DemandForecast or ReportType.InventoryTrends or ReportType.ReorderHistory;

        public async Task<ReportResult> BuildAsync(ReportRequest request, int userId)
        {
            if (!_auth.Can(Permissions.ReportsView)) return new ReportResult(false, "You do not have permission to generate reports.", null);

            var dateFrom = request.From?.Date;
            var dateTo = request.To?.Date;
            if (UsesDateRange(request.Type) && dateFrom is not null && dateTo is not null && dateFrom > dateTo)
                return new ReportResult(false, "The start date must be on or before the end date.", null);

            try
            {
                string? categoryName = null;
                if (request.CategoryId != 0)
                {
                    await using var db = await _factory.CreateDbContextAsync();
                    categoryName = (await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.CategoryId == request.CategoryId))?.CategoryName;
                }

                var columns = new List<string>();
                var rows = new List<IReadOnlyList<string>>();
                var notes = new List<string>();

                switch (request.Type)
                {
                    case ReportType.InventoryStatus:
                    {
                        columns.AddRange(new[] { "Item", "Category", "Current Stock", "Reorder Point", "Status" });
                        var data = (await _inventory.GetMonitoringAsync())
                            .Where(r => request.CategoryId == 0 || r.CategoryId == request.CategoryId)
                            .OrderBy(r => r.CategoryName).ThenBy(r => r.ItemName);
                        foreach (var r in data)
                            rows.Add(new[] { r.ItemName, r.CategoryName, $"{Fmt.Qty(r.QuantityOnHand)} {r.Unit}",
                                Fmt.Qty(r.ReorderPoint), StockStatusUi.Label(r.Status) });
                        notes.Add("Current stock levels of active items");
                        break;
                    }

                    case ReportType.MovementClassification:
                    {
                        columns.AddRange(new[] { "Item", "Consumption", "Turnover", "Classification" });
                        var snapshot = await _movement.GetLatestAsync();
                        foreach (var r in snapshot.Rows
                            .Where(r => request.CategoryId == 0 || r.CategoryId == request.CategoryId)
                            .OrderByDescending(r => r.TurnoverRate))
                            rows.Add(new[] { r.ItemName, $"{Fmt.Qty(r.TotalConsumption)} {r.Unit}",
                                r.TurnoverRate.ToString("0.##"), AnalyticsUi.Movement(r.Classification) });
                        notes.Add(snapshot.Period is null ? "No analysis has been run yet" : $"Analysis period {snapshot.Period}");
                        break;
                    }

                    case ReportType.DemandForecast:
                    {
                        columns.AddRange(new[] { "Item", "Forecast Period", "Forecast Value", "Method", "Actual", "Error" });
                        var data = (await _forecast.GetAsync())
                            .Where(r => request.CategoryId == 0 || r.CategoryId == request.CategoryId)
                            .Where(r => dateFrom is null || r.PeriodStart.Date >= dateFrom)
                            .Where(r => dateTo is null || r.PeriodStart.Date <= dateTo);
                        foreach (var r in data)
                            rows.Add(new[] { r.ItemName, r.Period, $"{Fmt.Qty(r.ForecastValue)} {r.Unit}",
                                AnalyticsUi.Method(r.Method),
                                r.Actual is decimal a ? $"{Fmt.Qty(a)} {r.Unit}" : "-",
                                r.AbsoluteError is decimal e ? Fmt.Qty(e) : "-" });
                        notes.Add("Forecasts are estimates, not guaranteed values");
                        break;
                    }

                    case ReportType.InventoryTrends:
                    {
                        columns.AddRange(new[] { "Item", "Period", "Consumption", "Stock Level", "Trend" });
                        var data = (await _trends.GetAsync())
                            .Where(r => request.CategoryId == 0 || r.CategoryId == request.CategoryId)
                            .Where(r => dateFrom is null || r.PeriodStart.Date >= dateFrom)
                            .Where(r => dateTo is null || r.PeriodEnd.Date <= dateTo);
                        foreach (var r in data)
                            rows.Add(new[] { r.ItemName, r.Period, $"{Fmt.Qty(r.Consumption)} {r.Unit}",
                                $"{Fmt.Qty(r.EndingStock)} {r.Unit}", r.Direction.ToString() });
                        notes.Add("Weekly periods; stock level is the end-of-period quantity");
                        break;
                    }

                    default: // ReorderHistory
                    {
                        columns.AddRange(new[] { "Item", "Recommended Quantity", "Priority", "Owner Decision", "Date", "Status" });
                        var data = (await _reorder.ListAsync())
                            .Where(r => categoryName is null || r.CategoryName == categoryName)
                            .Where(r => dateFrom is null || HistoryLoader.LocalDate(r.CreatedAt) >= dateFrom)
                            .Where(r => dateTo is null || HistoryLoader.LocalDate(r.CreatedAt) <= dateTo)
                            .OrderByDescending(r => r.CreatedAt);
                        foreach (var r in data)
                            rows.Add(new[] { r.ItemName, $"{Fmt.Qty(r.RecommendedQuantity)} {r.Unit}", UrgencyUi.Label(r.Urgency),
                                r.Decision.ToString(), Fmt.Local(r.CreatedAt), r.Status.ToString() });
                        break;
                    }
                }

                var parts = new List<string>();
                if (categoryName is not null) parts.Add($"Category: {categoryName}");
                if (UsesDateRange(request.Type) && (dateFrom is not null || dateTo is not null))
                    parts.Add($"Dates: {(dateFrom is DateTime fd ? fd.ToString("yyyy-MM-dd") : "start")} to {(dateTo is DateTime td ? td.ToString("yyyy-MM-dd") : "today")}");
                parts.AddRange(notes);

                var table = new ReportTable(TypeName(request.Type), string.Join("  |  ", parts), columns, rows, DateTime.Now);

                // Save the report's metadata (the contents are always rebuilt from live data).
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    db.Reports.Add(new Report
                    {
                        ReportType = request.Type,
                        GeneratedByUserId = userId,
                        Parameters = JsonSerializer.Serialize(new { request.CategoryId, From = dateFrom, To = dateTo }),
                        GeneratedAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                }
                await _audit.LogAsync(userId, "Generate Report", "Report", null, $"{table.Title}: {rows.Count} row(s). {table.Subtitle}");

                return new ReportResult(true, null, table);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Building the report failed.", ex);
                return new ReportResult(false, "Unable to generate the report. Please try again.", null);
            }
        }

        public string ToCsv(ReportTable table)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", table.Columns.Select(Escape)));
            foreach (var row in table.Rows) sb.AppendLine(string.Join(",", row.Select(Escape)));
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            value ??= string.Empty;

            // Stop spreadsheet programs from running text such as "=1+1" as a formula.
            if (value.Length > 0 && (value[0] is '=' or '+' or '@' || (value[0] == '-' && value.Length > 1 && !char.IsDigit(value[1]))))
                value = "'" + value;

            return value.Contains(',') || value.Contains('"') || value.Contains('\n')
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }
    }
}
