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
/// <para>
/// <b>The container is built and started lazily, on the first request for a connection string,
/// and that is load-bearing rather than an optimization.</b> An assembly fixture is constructed
/// for every run of this assembly, including the single filtered run the NFR-29 design-token
/// gate performs from <c>AfterTargets="Build"</c>. That gate scans files and reads the compiled
/// theme; it touches no database. Building the container eagerly made <c>dotnet build</c> itself
/// fail on any machine without Docker — note that <c>PostgreSqlBuilder.Build()</c> validates the
/// Docker endpoint, so deferring only <c>StartAsync</c> would not have been enough.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly Lazy<Task<PostgreSqlContainer>> _container = new(
        static async () =>
        {
            var container = new PostgreSqlBuilder("postgres:18-alpine")
                .WithDatabase("drivetrack")
                .WithUsername("drivetrack")
                .WithPassword("drivetrack-tests")
                .Build();

            await container.StartAsync();

            return container;
        },
        // Suites run in parallel, so several may ask for the connection string at once. This mode
        // runs the factory exactly once and publishes the same task to every caller.
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Connection string for the container, starting it on first use.
    /// <para>
    /// Blocking is deliberate: the property is read from constructors and field initializers all
    /// over the suite, and the alternative — an async accessor — would change every call site to
    /// buy nothing. Only the first caller waits, and xUnit v3 installs no synchronization context
    /// for it to deadlock against.
    /// </para>
    /// </summary>
    public string ConnectionString => _container.Value.GetAwaiter().GetResult().GetConnectionString();

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately does nothing. Starting here would defeat the laziness above, because xUnit
    /// initializes an assembly fixture before it knows which tests the filter selected.
    /// </remarks>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Nothing to tear down when no test asked for a database - the design-token gate's run
        // ends here without ever having spoken to Docker.
        if (!_container.IsValueCreated)
        {
            return;
        }

        await (await _container.Value).DisposeAsync();
    }
}
