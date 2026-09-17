using System.Net.Http.Headers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using DriveTrack.Integration.Tests.Support;

[assembly: AssemblyFixture(typeof(DriveTrack.Integration.Tests.Configuration.GarageFixture))]

namespace DriveTrack.Integration.Tests.Configuration;

/// <summary>
/// A real Garage node, stood up the way <c>compose.prod.yaml</c> stands it up and provisioned by
/// <c>compose.prod.yaml</c>'s own <c>objects-init</c> script, started once for the whole assembly.
/// <para>
/// <see cref="ObjectStoreAdapterTests"/> asserts what the adapter puts on the wire, which is the
/// half a loopback socket can answer for. It cannot answer for the other half: a Garage node that
/// refuses the request, or a provisioning script that quietly stopped creating the bucket, leaves
/// that suite green. The node here is the daemon those tests do not have, and the provisioning is
/// not a C# re-implementation of the script — it is the script, lifted out of the compose file and
/// run in the image the compose file names.
/// </para>
/// <para>
/// The <c>DotNet.Testcontainers.*</c> types below arrive transitively, through
/// <c>Testcontainers.PostgreSql</c>, and that is deliberate rather than an oversight to tidy up:
/// <c>api.nuget.org</c> is unreachable from this machine, so adding a <c>PackageReference</c> on the
/// core <c>Testcontainers</c> package — or on a module package such as <c>Testcontainers.Minio</c> —
/// breaks restore for everyone. Nothing here needs a module: a Garage node is an image, a config
/// file and two ports.
/// </para>
/// <para>
/// <b>Every container is built and started lazily</b>, for the reason
/// <see cref="Persistence.PostgresFixture"/> is lazy: an assembly fixture is constructed for every
/// run of this assembly, including the filtered run the NFR-29 design-token gate performs from
/// <c>AfterTargets="Build"</c>. <c>ContainerBuilder.Build()</c> validates the Docker endpoint, so
/// even building eagerly would fail <c>dotnet build</c> on a machine without Docker.
/// </para>
/// </summary>
public sealed class GarageFixture : IAsyncLifetime
{
    /// <summary>The S3 API port <c>garage.toml</c> binds.</summary>
    private const int S3Port = 3900;

    /// <summary>The admin API port <c>garage.toml</c> binds, and the one the script talks to.</summary>
    private const int AdminPort = 3903;

    /// <summary>
    /// The network alias the provisioning script resolves — it posts to <c>http://objects:3903</c>,
    /// which is the compose service name and has to keep being the node's name here too.
    /// </summary>
    private const string NodeAlias = "objects";

    /// <summary>The compose service that provisions the node.</summary>
    private const string InitService = "objects-init";

    private readonly Lazy<Task<GarageNode>> _node = new(
        StartAsync,
        // Cases in one class run serially, but the assembly fixture is shared with whatever else
        // asks for it; this mode runs the factory once and publishes the same task to every caller.
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The node's S3 endpoint, with no trailing slash — the SDK appends its own.
    /// <para>
    /// The host is the container's own, not the literal <c>localhost</c>: with a remote or TCP
    /// <c>DOCKER_HOST</c> the published port is on the daemon's machine, and a hardcoded loopback
    /// address would be refused by whatever happens to be listening here.
    /// </para>
    /// </summary>
    public string S3Endpoint => EndpointFor(S3Port);

    /// <summary>
    /// The node's admin endpoint, with no trailing slash and resolved to the same host as
    /// <see cref="S3Endpoint"/> — on the port the provisioning script talks to.
    /// </summary>
    public string AdminEndpoint => EndpointFor(AdminPort);

    /// <summary>The access key id <c>.env.example</c> declares and <c>objects-init</c> imported.</summary>
    public static string AccessKey => ComposeStack.EnvExample("ObjectStore__AccessKey");

    /// <summary>The secret <c>.env.example</c> declares and <c>objects-init</c> imported the key with.</summary>
    public static string SecretKey => ComposeStack.EnvExample("ObjectStore__SecretKey");

    /// <summary>The bucket <c>.env.example</c> declares and <c>objects-init</c> created.</summary>
    public static string Bucket => ComposeStack.EnvExample("ObjectStore__Bucket");

    /// <summary>The region the requests are signed for, as the deployment declares it.</summary>
    public static string Region => ComposeStack.EnvExample("ObjectStore__Region");

    /// <summary>
    /// A client for the node's admin API, already carrying the bearer token the deployment uses.
    /// The caller owns it and disposes it.
    /// </summary>
    public HttpClient CreateAdminClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(AdminEndpoint + "/") };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ComposeStack.EnvExample("GARAGE_ADMIN_TOKEN"));

        return client;
    }

    /// <summary>
    /// Runs <c>compose.prod.yaml</c>'s provisioning script against the node again, and answers
    /// what it did. The script promises to be idempotent, and this is what lets a test hold it to
    /// that.
    /// </summary>
    public async Task<ObjectsInitRun> RunObjectsInitAsync(CancellationToken cancellationToken) =>
        await RunInitContainerAsync(Node.Network, ComposeStack.Prod.ObjectsInitScript, cancellationToken);

    /// <summary>
    /// Runs an arbitrary script under the entrypoint <c>compose.prod.yaml</c> gives
    /// <c>objects-init</c>, in the image it names, on the node's network.
    /// <para>
    /// Reading the entrypoint out of compose pins where the shell flags come from; only running a
    /// script that fails partway pins what they do. Without the <c>-e</c>, a provisioning step that
    /// failed halfway runs on and still exits 0, and the <c>app</c> service's
    /// <c>service_completed_successfully</c> gate opens on a node whose key holds no grant.
    /// </para>
    /// </summary>
    public async Task<ObjectsInitRun> RunUnderInitEntrypointAsync(
        string script,
        CancellationToken cancellationToken) =>
        await RunInitContainerAsync(Node.Network, script, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately does nothing, exactly as <see cref="Persistence.PostgresFixture"/>'s does:
    /// starting here would defeat the laziness above, because xUnit initializes an assembly fixture
    /// before it knows which tests the filter selected.
    /// </remarks>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Nothing to tear down when no test asked for the node - the design-token gate's run ends
        // here without ever having spoken to Docker.
        if (!_node.IsValueCreated)
        {
            return;
        }

        var start = _node.Value;

        // A start that threw has already disposed everything it created, and awaiting it here would
        // rethrow the same failure as an assembly-teardown error - reporting the cause twice and
        // burying the run's real first failure under a second copy of it.
        if (start.IsFaulted || start.IsCanceled)
        {
            return;
        }

        var node = await start;

        try
        {
            await node.Container.DisposeAsync();
        }
        finally
        {
            // The same care StartAsync takes, at the other end: a container that will not go away
            // must not take the network with it, or every run that hits it leaves one more orphaned
            // Docker network behind.
            await node.Network.DisposeAsync();
        }
    }

    /// <summary>The started node, starting it on first use.</summary>
    /// <remarks>
    /// Blocking mirrors <see cref="Persistence.PostgresFixture.ConnectionString"/>: only the first
    /// caller waits, and xUnit v3 installs no synchronization context for it to deadlock against.
    /// </remarks>
    private GarageNode Node => _node.Value.GetAwaiter().GetResult();

    /// <summary>A published port, addressed at whichever host the Docker daemon exposes it on.</summary>
    private string EndpointFor(int port) =>
        "http://" + Node.Container.Hostname + ":" + Node.Container.GetMappedPublicPort(port);

    private static async Task<GarageNode> StartAsync()
    {
        var network = new NetworkBuilder().Build();

        await network.CreateAsync(CancellationToken.None);

        IContainer? container = null;

        // Everything after the network exists is guarded: a refused image, a node that will not
        // start and a provisioning script that fails all have to leave the network and any started
        // container behind them, or a failing run leaks Docker resources on every retry.
        try
        {
            container = new ContainerBuilder(ComposeStack.Prod.ImageOf(NodeAlias))

                // The argument vector compose gives the service, read from compose rather than
                // copied out of it. The image carries no entrypoint, so the binary is the first word.
                .WithCommand(ComposeStack.Prod.CommandOf(NodeAlias))

                // The repository's own configuration, at the path compose mounts it to - read from
                // compose rather than restated, because the ports, the region and the replication
                // factor the adapter has to match are decided by whatever lands at that path.
                .WithResourceMapping(
                    ComposeStack.GarageConfigBytes,
                    ComposeStack.Prod.GarageConfigMountTarget(NodeAlias))
                .WithNetwork(network)
                .WithNetworkAliases(NodeAlias)
                .WithEnvironment(EnvironmentOf(NodeAlias))
                .WithPortBinding(S3Port, true)
                .WithPortBinding(AdminPort, true)
                .Build();

            await container.StartAsync(CancellationToken.None);

            // No wait strategy on the node beyond "running": the provisioning script already polls
            // the admin API for up to two minutes, so the readiness check is the script's and there
            // is only one of it.
            var provisioning = await RunInitContainerAsync(
                network,
                ComposeStack.Prod.ObjectsInitScript,
                CancellationToken.None);

            if (provisioning.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "compose.prod.yaml's objects-init script failed to provision the Garage node "
                        + $"(exit code {provisioning.ExitCode}).{Environment.NewLine}{provisioning.Logs}");
            }

            return new GarageNode(network, container);
        }
        catch
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }

            await network.DisposeAsync();

            throw;
        }
    }

    /// <summary>
    /// The environment a compose service declares, with the names taken from
    /// <c>compose.prod.yaml</c> and the values from <c>.env.example</c> — so a key dropped or
    /// renamed in either file throws.
    /// </summary>
    private static IReadOnlyDictionary<string, string> EnvironmentOf(string service) =>
        ComposeStack.Prod.EnvironmentKeysOf(service)
            .ToDictionary(key => key, ComposeStack.EnvExample, StringComparer.Ordinal);

    /// <summary>
    /// Runs a script under the entrypoint compose gives <c>objects-init</c>, in the image it names,
    /// and waits for it to finish.
    /// </summary>
    private static async Task<ObjectsInitRun> RunInitContainerAsync(
        INetwork network,
        string script,
        CancellationToken cancellationToken)
    {
        var init = new ContainerBuilder(ComposeStack.Prod.ImageOf(InitService))
            .WithNetwork(network)

            // Read rather than copied, because the flags are the contract: drop the `-e` and a
            // provisioning step that failed halfway would still exit 0, leaving an S3 key with no
            // grant on a bucket and a `docker compose up` that reports success.
            .WithEntrypoint(ComposeStack.Prod.EntrypointOf(InitService))
            .WithCommand(script)
            .WithEnvironment(EnvironmentOf(InitService))

            // A one-shot container needs a wait strategy that does not wait: the process is allowed
            // to have exited by the time StartAsync returns, and the outcome is read from the exit
            // code below rather than from a running container.
            .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new Immediately()))
            .Build();

        try
        {
            try
            {
                await init.StartAsync(cancellationToken);
            }
            catch (ContainerNotRunningException)
            {
                // A one-shot container that exited non-zero fails Testcontainers' readiness check,
                // and the exception it raises carries the container id and nothing else. Swallowed
                // here so the exit code and the logs below can say what actually went wrong - a
                // failed provisioning is a result to report, not a harness malfunction.
            }

            // Blocks until the script ends.
            var exitCode = await init.GetExitCodeAsync(cancellationToken);
            var (standardOutput, standardError) = await init.GetLogsAsync(ct: cancellationToken);

            // Separated rather than concatenated: the script's own diagnostics go to stderr, and a
            // stdout that did not end in a newline would splice the line that explains the failure
            // onto the tail of the one before it.
            return new ObjectsInitRun(
                exitCode,
                standardOutput + Environment.NewLine + standardError);
        }
        finally
        {
            await init.DisposeAsync();
        }
    }

    /// <summary>What one run of the provisioning script did.</summary>
    /// <param name="ExitCode">The script's exit code; the compose file promises 0 on a re-run.</param>
    /// <param name="Logs">Everything the container wrote, so a failure says why rather than what.</param>
    public sealed record ObjectsInitRun(long ExitCode, string Logs);

    /// <summary>The network and the node on it, held together so both are torn down.</summary>
    private sealed record GarageNode(INetwork Network, IContainer Container);

    /// <summary>A wait strategy that is satisfied the moment it is asked.</summary>
    private sealed class Immediately : IWaitUntil
    {
        /// <inheritdoc />
        public Task<bool> UntilAsync(IContainer container) => Task.FromResult(true);
    }
}
