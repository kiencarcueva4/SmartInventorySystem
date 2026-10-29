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
    public static class InfrastructureServiceExtensions
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services)
        {
            AppPaths.EnsureFolders();

            // Blazor components live a long time, so use a factory and create a short-lived
            // DbContext per operation: await using var db = await factory.CreateDbContextAsync();
            services.AddDbContextFactory<AppDbContext>(options =>
                options.UseSqlite($"Data Source={AppPaths.DatabasePath}"));

            return services;
        }
    }


    public static class DatabaseInitializer
    {
        /// <summary>Applies migrations, then seeds. Safe to run on every startup.</summary>
        public static async Task InitializeAsync(IServiceProvider provider, CancellationToken ct = default)
        {
            var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync(ct);

            // If you ran Add-Migration, apply the migrations. Until then, create the
            // tables straight from the model so the project runs with no extra steps.
            // NOTE: when you add your first migration, delete smart_inventory.db first.
            if (db.Database.GetMigrations().Any())
                await db.Database.MigrateAsync(ct);
            else
                await db.Database.EnsureCreatedAsync(ct);

            await DatabaseSeeder.SeedAsync(db, ct);
        }
    }
}
