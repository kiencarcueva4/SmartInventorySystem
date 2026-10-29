using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Infrastructure;

namespace SmartInventorySystem.Services.Admin
{
    public sealed record AuditRow(int Id, DateTime Timestamp, string User, string Action, string? Record, string? Description);

    public sealed record AuditPage(List<AuditRow> Rows, int Total, int Page, int PageSize);

    public sealed record AuditUserOption(int UserId, string Name);

    public sealed record AuditLookups(List<AuditUserOption> Users, List<string> Actions);

    public sealed class AuditFilter
    {
        public int UserId { get; set; }
        public string Action { get; set; } = "All";
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public string? Search { get; set; }
    }

    public interface IAuditQueryService
    {
        Task<AuditLookups> LookupsAsync();
        Task<AuditPage> QueryAsync(AuditFilter filter, int page, int pageSize);
    }

    /// <summary>Read-only view of the audit trail. Nothing here can change or delete a log entry.</summary>
    public sealed class AuditQueryService : IAuditQueryService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;

        public AuditQueryService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

        public async Task<AuditLookups> LookupsAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var users = await db.Users.AsNoTracking().OrderBy(u => u.FullName).ToListAsync();
            var actions = await db.AuditLogs.AsNoTracking().Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync();
            return new AuditLookups(users.Select(u => new AuditUserOption(u.UserId, $"{u.FullName} ({u.Username})")).ToList(), actions);
        }

        public async Task<AuditPage> QueryAsync(AuditFilter filter, int page, int pageSize)
        {
            await using var db = await _factory.CreateDbContextAsync();
            IQueryable<Domain.Entities.AuditLog> q = db.AuditLogs.AsNoTracking().Include(a => a.User);

            if (filter.UserId != 0) q = q.Where(a => a.UserId == filter.UserId);
            if (filter.Action != "All") q = q.Where(a => a.Action == filter.Action);

            if (filter.From is DateTime fromDate)
            {
                var fromUtc = DateTime.SpecifyKind(fromDate.Date, DateTimeKind.Local).ToUniversalTime();
                q = q.Where(a => a.Timestamp >= fromUtc);
            }
            if (filter.To is DateTime toDate)
            {
                var toUtc = DateTime.SpecifyKind(toDate.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime();
                q = q.Where(a => a.Timestamp < toUtc);
            }
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var pattern = $"%{filter.Search.Trim()}%";
                q = q.Where(a => EF.Functions.Like(a.Description ?? string.Empty, pattern)
                              || EF.Functions.Like(a.EntityName ?? string.Empty, pattern));
            }

            var total = await q.CountAsync();
            var rows = await q.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.AuditLogId)
                .Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToListAsync();

            return new AuditPage(rows.Select(a => new AuditRow(
                a.AuditLogId, a.Timestamp,
                a.User is null ? "System / not signed in" : $"{a.User.FullName} ({a.User.Username})",
                a.Action,
                a.EntityName is null ? null : (a.RecordId is null ? a.EntityName : $"{a.EntityName} #{a.RecordId}"),
                a.Description)).ToList(), total, page, pageSize);
        }
    }
}
