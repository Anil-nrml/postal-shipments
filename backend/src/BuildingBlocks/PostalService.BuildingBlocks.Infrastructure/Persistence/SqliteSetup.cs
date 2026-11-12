using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PostalService.BuildingBlocks.Infrastructure.Persistence;

public static class SqliteSetup
{
    /// <summary>
    /// Registers a DbContext on SQLite. Each service owns exactly one database file (database-per-service).
    /// "Default Timeout" makes Microsoft.Data.Sqlite retry on SQLITE_BUSY instead of failing immediately,
    /// which matters because SQLite allows only one writer at a time.
    /// </summary>
    public static IServiceCollection AddSqliteDbContext<TContext>(this IServiceCollection services, string connectionString)
        where TContext : DbContext
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            DefaultTimeout = 30,
            Pooling = true,
        };
        if (builder.DataSource != ":memory:" && !string.IsNullOrWhiteSpace(builder.DataSource))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }

        services.AddDbContext<TContext>(o => o.UseSqlite(builder.ToString()));
        return services;
    }

    /// <summary>
    /// Applies EF Core migrations (or creates the schema when none have been generated yet — see
    /// scripts/add-migrations), enables WAL so readers never block the writer, then runs an optional seed.
    /// </summary>
    public static async Task InitializeDatabaseAsync<TContext>(this IHost host,
        Func<TContext, IServiceProvider, Task>? seed = null)
        where TContext : DbContext
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInit");

        if (db.Database.GetMigrations().Any())
        {
            logger.LogInformation("Applying EF Core migrations for {Context}", typeof(TContext).Name);
            await db.Database.MigrateAsync();
        }
        else
        {
            logger.LogWarning("No migrations found for {Context}; creating schema with EnsureCreated. " +
                              "Run scripts/add-migrations to generate migrations.", typeof(TContext).Name);
            await db.Database.EnsureCreatedAsync();
        }

        if (db.Database.IsSqlite() && db.Database.GetDbConnection().DataSource is not ":memory:")
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        if (seed is not null)
            await seed(db, scope.ServiceProvider);
    }
}
