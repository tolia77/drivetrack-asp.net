using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveTrack.Infrastructure.Persistence;

/// <summary>
/// The single deterministic entry point to AD-20: the schema is produced only by EF Core
/// migrations, applied at container start before the web host accepts traffic.
/// <para>
/// This type never calls <c>EnsureCreated</c> and never swallows a connection or migration
/// failure — starting without a schema is strictly worse than failing loudly, because the
/// former only surfaces as a broken request much later.
/// </para>
/// </summary>
public sealed class DatabaseMigrator(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<DatabaseMigrator> logger)
{
    /// <summary>
    /// Applies every migration the database is missing. A no-op when the database is already
    /// at head. Any failure propagates.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var pending = (await context.Database
            .GetPendingMigrationsAsync(cancellationToken))
            .ToList();

        if (pending.Count == 0)
        {
            logger.LogInformation("Database is already at the latest migration; nothing to apply.");
            return;
        }

        logger.LogInformation(
            "Applying {Count} pending migration(s): {Migrations}.",
            pending.Count,
            string.Join(", ", pending));

        await context.Database.MigrateAsync(cancellationToken);

        logger.LogInformation("Migrations applied.");
    }
}
