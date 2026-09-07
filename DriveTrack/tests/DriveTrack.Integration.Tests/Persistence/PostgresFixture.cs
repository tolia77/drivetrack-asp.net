using Testcontainers.PostgreSql;

namespace DriveTrack.Integration.Tests.Persistence;

/// <summary>
/// A real PostgreSQL 18 container, shared by the migration-pipeline tests. The migration
/// fidelity requirement (AD-20, NFR-18) cannot be verified against an in-memory provider,
/// so the tests never use one.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("drivetrack")
        .WithUsername("drivetrack")
        .WithPassword("drivetrack-tests")
        .Build();

    /// <summary>Connection string for the running container.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await _container.StartAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>Collection binding so one container serves every migration test.</summary>
[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
