using DriveTrack.Application.Abstractions;
using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Identity;
using DriveTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// A database of its own for one test, created on the shared PostgreSQL 18 container and
/// dropped when the test ends.
/// <para>
/// Every suite that touches the schema goes through here, so each gets an isolated database
/// produced by the real migration pipeline and the real <c>AddInfrastructure</c> registration —
/// a test that passes against a hand-rolled context proves nothing about the schema the
/// application actually runs on (AD-20, NFR-18).
/// </para>
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _name;
    private readonly ServiceProvider _services;

    private TestDatabase(
        string adminConnectionString,
        string name,
        string connectionString,
        ServiceProvider services)
    {
        _adminConnectionString = adminConnectionString;
        _name = name;
        _services = services;
        ConnectionString = connectionString;
    }

    /// <summary>Connection string for this test's own database.</summary>
    public string ConnectionString { get; }

    /// <summary>The context factory the container would hand a caller (AD-5).</summary>
    public IDbContextFactory<AppDbContext> ContextFactory =>
        _services.GetRequiredService<IDbContextFactory<AppDbContext>>();

    /// <summary>The per-operation persistence scope factory (AD-5).</summary>
    public IUnitOfWorkFactory UnitOfWorkFactory =>
        _services.GetRequiredService<IUnitOfWorkFactory>();

    /// <summary>The migrator the composition root runs at start-up (AD-20).</summary>
    public DatabaseMigrator Migrator => _services.GetRequiredService<DatabaseMigrator>();

    /// <summary>The startup seeder the composition root runs after the migrator (FR-9).</summary>
    public IdentitySeeder Seeder => _services.GetRequiredService<IdentitySeeder>();

    /// <summary>
    /// Creates an empty database and, unless told otherwise, migrates it to head.
    /// </summary>
    /// <param name="adminConnectionString">Connection string of the container's own database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="migrate">False leaves the database empty, so "cold start" means what it says.</param>
    /// <param name="settings">Configuration overlaid on the defaults, for the seeder tests.</param>
    public static async Task<TestDatabase> CreateAsync(
        string adminConnectionString,
        CancellationToken cancellationToken,
        bool migrate = true,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        var name = "drivetrack_" + Guid.NewGuid().ToString("N")[..12];

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = name,
        }.ConnectionString;

        var services = BuildProvider(connectionString, settings);
        var database = new TestDatabase(adminConnectionString, name, connectionString, services);

        if (migrate)
        {
            await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(cancellationToken);
        }

        return database;
    }

    /// <summary>
    /// Builds the container's real registration path, so a test exercises
    /// <c>AddInfrastructure</c> rather than a hand-rolled context.
    /// </summary>
    public static ServiceProvider BuildProvider(
        string connectionString,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        var values = TestConfiguration.Defaults();
        values["ConnectionStrings:Default"] = connectionString;

        foreach (var setting in settings ?? new Dictionary<string, string?>(StringComparer.Ordinal))
        {
            values[setting.Key] = setting.Value;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // IdentitySeeder reads Admin:* itself, and AddInfrastructure does not register the
        // configuration it was handed - the host normally does that.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration, new TestHostEnvironment());

        return services.BuildServiceProvider();
    }

    /// <summary>Opens a context on this database.</summary>
    public Task<AppDbContext> CreateContextAsync(CancellationToken cancellationToken) =>
        ContextFactory.CreateDbContextAsync(cancellationToken);

    /// <summary>Opens a raw connection, for the assertions that must bypass EF entirely.</summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        return connection;
    }

    /// <summary>Runs a statement outside EF, for proving what the schema itself does.</summary>
    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads a single value outside EF.</summary>
    public async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is DBNull ? null : value;
    }

    /// <summary>Whether a table of that exact name exists in the <c>public</c> schema.</summary>
    public static async Task<bool> TableExistsAsync(
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();

        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
