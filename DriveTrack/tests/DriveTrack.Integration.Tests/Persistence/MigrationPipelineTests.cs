using DriveTrack.Infrastructure.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DriveTrack.Integration.Tests.Persistence;

/// <summary>
/// AD-20 / NFR-18 end to end against a real PostgreSQL 18 container: migrations are the only
/// schema authority, they are applied deterministically before the app serves, a second run
/// is a no-op, and an unreachable database fails loudly instead of pretending the schema is
/// there.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class MigrationPipelineTests(PostgresFixture postgres)
{
    /// <summary>A port nothing listens on, used to prove the migrator does not swallow failures.</summary>
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=drivetrack;Username=drivetrack;Password=none;" +
        "Timeout=2;Command Timeout=2";

    [Fact]
    public async Task Cold_start_migrates_a_fresh_database_and_leaves_nothing_pending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var database = await TestDatabase.CreateAsync(
            postgres.ConnectionString, cancellationToken, migrate: false);

        await database.Migrator.MigrateAsync(cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);

        Assert.NotEmpty(applied);
        Assert.Empty(pending);

        // The model and the migration history agree - no drift between the two.
        Assert.False(context.Database.HasPendingModelChanges());

        // The pipeline is real: the history table exists in the database itself.
        Assert.True(await TestDatabase.TableExistsAsync(
            database.ConnectionString, "__EFMigrationsHistory", cancellationToken));
    }

    [Fact]
    public async Task Warm_start_applies_nothing_and_throws_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var database = await TestDatabase.CreateAsync(
            postgres.ConnectionString, cancellationToken);

        string[] firstRun;
        await using (var afterFirst = await database.CreateContextAsync(cancellationToken))
        {
            firstRun = (await afterFirst.Database.GetAppliedMigrationsAsync(cancellationToken))
                .ToArray();
        }

        // Second start against the same volume.
        await database.Migrator.MigrateAsync(cancellationToken);

        await using var afterSecond = await database.CreateContextAsync(cancellationToken);

        var secondRun = (await afterSecond.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToArray();

        Assert.Equal(firstRun, secondRun);
        Assert.Empty(await afterSecond.Database.GetPendingMigrationsAsync(cancellationToken));
    }

    [Fact]
    public async Task Unreachable_database_throws_rather_than_returning_as_if_migrated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var provider = TestDatabase.BuildProvider(UnreachableConnectionString);

        var migrator = provider.GetRequiredService<DatabaseMigrator>();

        await Assert.ThrowsAnyAsync<NpgsqlException>(
            () => migrator.MigrateAsync(cancellationToken));
    }
}
