using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(DriveTrack.Integration.Tests.Persistence.PostgresFixture))]

namespace DriveTrack.Integration.Tests.Persistence;

/// <summary>
/// A real PostgreSQL 18 container, started once for the whole assembly. The migration
/// fidelity requirement (AD-20, NFR-18) cannot be verified against an in-memory provider,
/// so the tests never use one.
/// <para>
/// Bound as an assembly fixture rather than a collection fixture on purpose. A collection is
/// xUnit's unit of serialization, so binding every suite to one collection ran all of them on
/// a single thread — the container was shared, and so was the thread. Nothing here needs that:
/// <see cref="Support.TestDatabase"/> gives every test its own database on this container and
/// drops it afterwards, so suites share no mutable state and are free to run in parallel.
/// </para>
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
