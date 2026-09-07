using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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

        await using var database = await CreateFreshDatabaseAsync(cancellationToken);
        await using var provider = BuildProvider(database.ConnectionString);

        await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync(cancellationToken);

        await using var context = await provider
            .GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContextAsync(cancellationToken);

        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);

        Assert.NotEmpty(applied);
        Assert.Empty(pending);

        // The model and the migration history agree - no drift between the two.
        Assert.False(context.Database.HasPendingModelChanges());

        // The pipeline is real: the history table exists in the database itself.
        Assert.True(await TableExistsAsync(
            database.ConnectionString, "__EFMigrationsHistory", cancellationToken));
    }

    [Fact]
    public async Task Warm_start_applies_nothing_and_throws_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var database = await CreateFreshDatabaseAsync(cancellationToken);
        await using var provider = BuildProvider(database.ConnectionString);

        var migrator = provider.GetRequiredService<DatabaseMigrator>();
        var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();

        await migrator.MigrateAsync(cancellationToken);

        string[] firstRun;
        await using (var afterFirst = await factory.CreateDbContextAsync(cancellationToken))
        {
            firstRun = (await afterFirst.Database.GetAppliedMigrationsAsync(cancellationToken))
                .ToArray();
        }

        // Second start against the same volume.
        await migrator.MigrateAsync(cancellationToken);

        await using var afterSecond = await factory.CreateDbContextAsync(cancellationToken);

        var secondRun = (await afterSecond.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToArray();

        Assert.Equal(firstRun, secondRun);
        Assert.Empty(await afterSecond.Database.GetPendingMigrationsAsync(cancellationToken));
    }

    [Fact]
    public async Task Unreachable_database_throws_rather_than_returning_as_if_migrated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var provider = BuildProvider(UnreachableConnectionString);

        var migrator = provider.GetRequiredService<DatabaseMigrator>();

        await Assert.ThrowsAnyAsync<NpgsqlException>(
            () => migrator.MigrateAsync(cancellationToken));
    }

    /// <summary>
    /// Creates a database that has never been migrated, so "cold start" means what it says.
    /// </summary>
    private async Task<TemporaryDatabase> CreateFreshDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = "drivetrack_" + Guid.NewGuid().ToString("N")[..12];

        await using (var connection = new NpgsqlConnection(postgres.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var builder = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = name,
        };

        return new TemporaryDatabase(postgres.ConnectionString, name, builder.ConnectionString);
    }

    /// <summary>
    /// Builds the container's real registration path, so the test exercises
    /// <c>AddInfrastructure</c> rather than a hand-rolled context.
    /// </summary>
    private static ServiceProvider BuildProvider(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration, new TestHostEnvironment());

        return services.BuildServiceProvider();
    }

    private static async Task<bool> TableExistsAsync(
        string connectionString,
        string table,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        // Matched case-sensitively: EF creates the history table as "__EFMigrationsHistory",
        // and an unquoted identifier lookup would fold it to lower case and miss it.
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relname = @name
                  AND c.relkind = 'r'
                  AND n.nspname = 'public')
            """;
        command.Parameters.AddWithValue("name", table);

        var result = await command.ExecuteScalarAsync(cancellationToken);

        return result is true;
    }

    private sealed class TemporaryDatabase(
        string adminConnectionString,
        string name,
        string connectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();

            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "DriveTrack.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
