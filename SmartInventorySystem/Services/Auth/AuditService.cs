using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SmartInventorySystem.Domain.Entities;
using SmartInventorySystem.Infrastructure;

namespace SmartInventorySystem.Services.Auth
{
    public interface IAuditService
    {
        Task LogAsync(int? userId, string action, string? entityName = null,
                      string? recordId = null, string? description = null);
    }

    /// <summary>Writes rows to Audit_Logs. A failure here is logged but never breaks the user's action.</summary>
    public sealed class AuditService : IAuditService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;

        public AuditService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

        public async Task LogAsync(int? userId, string action, string? entityName = null,
                                   string? recordId = null, string? description = null)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync();
                db.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    Action = action,
                    EntityName = entityName,
                    RecordId = recordId,
                    Description = description,
                    Timestamp = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unable to write audit log: " + action, ex);
            }
        }
    }
}
