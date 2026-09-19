using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using DriveTrack.Application.Abstractions;
using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Objects;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Configuration;

/// <summary>
/// The layer beneath <see cref="ObjectStoreAdapterTests"/>: the same <see cref="IAssetStore"/>, from
/// the same <c>AddInfrastructure</c>, pointed at a real Garage node that
/// <c>compose.dev.yaml</c>'s own <c>objects-init</c> script provisioned.
/// <para>
/// Two things were untested until this existed, and both fail in production without failing here
/// first. A node can refuse what the adapter sends — a canned 200 cannot tell you that Garage
/// disagrees about the signature, the path or the framing. And the provisioning was executed by
/// nothing at all: the cluster layout, the bucket, the imported key and its permissions are declared
/// in a shell script inside a YAML block scalar, and a script that silently stopped creating the
/// bucket would leave every test in the solution green.
/// </para>
/// <para>
/// One class, so the cases run serially against the shared node — one of them re-runs the
/// provisioning, and that is only a meaningful assertion if nothing else is mid-write.
/// </para>
/// </summary>
public class ObjectStoreDaemonTests(GarageFixture garage) : IDisposable
{
    /// <summary>Every container <see cref="Resolve"/> built, released with the test.</summary>
    private readonly List<ServiceProvider> _providers = [];

    /// <summary>Present so <c>AddInfrastructure</c> is satisfied; nothing here opens it.</summary>
    private const string ValidConnectionString =
        "Host=localhost;Port=5432;Database=drivetrack;Username=drivetrack;Password=irrelevant";

    [Fact]
    public async Task An_object_round_trips_through_a_real_node()
    {
        // The whole port against the whole daemon: what comes back has to be what went in, byte for
        // byte, and the bytes are non-ASCII because a store that mangled the encoding somewhere in
        // the middle would round-trip ASCII perfectly.
        var cancellationToken = TestContext.Current.CancellationToken;

        var bytes = Encoding.UTF8.GetBytes("докази-proof-of-delivery");

        var store = Resolve(GarageFixture.Bucket);

        using var upload = new MemoryStream(bytes);

        var key = store.NewKey("image/png");

        Assert.StartsWith("proof/", key, StringComparison.Ordinal);
        Assert.EndsWith(".png", key, StringComparison.Ordinal);

        await store.SaveAsync(key, upload, "image/png", cancellationToken);

        await using var content = await store.OpenAsync(key, cancellationToken);

        Assert.NotNull(content);

        using var buffer = new MemoryStream();

        await content.CopyToAsync(buffer, cancellationToken);

        Assert.Equal(bytes, buffer.ToArray());
    }

    [Fact]
    public async Task A_key_the_node_does_not_hold_answers_no_stream_rather_than_throwing()
    {
        // The same null as the adapter suite asserts, but from the 404 a real Garage sends rather
        // than from a canned one. The adapter maps any 404 to null, so what this pins is that a
        // missing key is what Garage actually answers 404 to - not a shape a stub was told to send.
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Null(await Resolve(GarageFixture.Bucket)
            .OpenAsync("proof/does-not-exist", cancellationToken));
    }

    [Fact]
    public async Task The_bucket_compose_declares_exists_on_the_node()
    {
        // compose.dev.yaml says objects-init creates this bucket. Nothing checked that it did.
        var cancellationToken = TestContext.Current.CancellationToken;

        var state = await ReadProvisionedStateAsync(cancellationToken);

        Assert.True(
            state.BucketId.Length == 64 && state.BucketId.All(Uri.IsHexDigit),
            $"'{state.BucketId}' is not a Garage bucket id.");
    }

    [Fact]
    public async Task The_S3_key_compose_declares_was_imported_under_that_exact_id()
    {
        // Imported rather than created, which is the whole reason one `docker compose up` is enough:
        // a generated key would have an id nobody could predict and the .env would need editing by
        // hand. If ImportKey ever stopped honouring the id, this is where it shows.
        var cancellationToken = TestContext.Current.CancellationToken;

        var (status, body) = await AdminAsync(
            "v2/GetKeyInfo?id=" + Uri.EscapeDataString(GarageFixture.AccessKey),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, status);

        using var document = JsonDocument.Parse(body);

        // Read out of the parsed answer rather than looked for in the raw text: Garage echoes the
        // request, so a body that merely *contains* the id proves only that the id was asked about.
        Assert.True(
            document.RootElement.TryGetProperty("accessKeyId", out var id)
                && id.ValueKind == JsonValueKind.String,
            $"GetKeyInfo answered no accessKeyId: {body}");

        Assert.Equal(GarageFixture.AccessKey, id.GetString());
    }

    [Fact]
    public async Task The_key_may_read_and_write_the_bucket_and_does_not_own_it()
    {
        // Read and write, not owner: the application stores proof assets and reads them back, and
        // nothing in it deletes a bucket or changes who may see one. An accidental `owner: true` in
        // the provisioning script is invisible until somebody uses it.
        var cancellationToken = TestContext.Current.CancellationToken;

        var state = await ReadProvisionedStateAsync(cancellationToken);

        AssertGrantedPermissions(state);
    }

    [Fact]
    public async Task Provisioning_the_node_a_second_time_succeeds_and_changes_nothing()
    {
        // The compose file's own claim - "every step is idempotent: a second `up` finds the layout
        // applied, the bucket present and the key imported, reports so, and still exits 0" - which
        // the app service's service_completed_successfully gate depends on and which nothing tested.
        var cancellationToken = TestContext.Current.CancellationToken;

        var before = await ReadProvisionedStateAsync(cancellationToken);

        var run = await garage.RunObjectsInitAsync(cancellationToken);

        Assert.True(
            run.ExitCode == 0,
            $"Re-running compose.dev.yaml's objects-init script exited {run.ExitCode}:"
                + Environment.NewLine
                + run.Logs);

        var after = await ReadProvisionedStateAsync(cancellationToken);

        // The whole state in one comparison, and the cluster layout version is the load-bearing
        // member of it: the layout is the script's one branch that is not naturally idempotent, so a
        // second run that re-applied it would bump the version here even though nothing else moved.
        Assert.Equal(before, after);

        AssertGrantedPermissions(after);
    }

    [Fact]
    public async Task The_shell_flags_compose_runs_the_provisioning_under_stop_it_at_the_first_failure()
    {
        // Reading `entrypoint:` out of compose pins where the flags come from; it does not pin what
        // they do, and a healthy provisioning run never fails a step, so `-e` could be dropped
        // from compose.dev.yaml with every other case here still green. What that would cost: a
        // step that failed halfway runs on and still exits 0, and the app service's
        // service_completed_successfully gate opens on a node whose key holds no grant.
        var cancellationToken = TestContext.Current.CancellationToken;

        var run = await garage.RunUnderInitEntrypointAsync(
            "false\necho 'ran on past the failed step'\n",
            cancellationToken);

        Assert.True(
            run.ExitCode != 0,
            "A failed step did not fail the script:" + Environment.NewLine + run.Logs);

        Assert.DoesNotContain("ran on past the failed step", run.Logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_to_a_bucket_nobody_created_throws()
    {
        // AD-26: a save that failed quietly would let a capture answer 200 with none of its evidence
        // in the bucket. The refusal here comes from Garage rather than from a canned status line,
        // so it also pins that the adapter does not swallow a real NoSuchBucket.
        var cancellationToken = TestContext.Current.CancellationToken;

        var store = Resolve(GarageFixture.Bucket + "-was-never-created");

        // The exception type and its code, not merely "something threw": an unconfigured store and
        // an unreachable node both throw too, and either would satisfy a bare ThrowsAny while
        // proving nothing about what Garage said.
        using var upload = new MemoryStream(Encoding.UTF8.GetBytes("evidence"));

        var failure = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            store.SaveAsync(store.NewKey("image/png"), upload, "image/png", cancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
        Assert.Equal("NoSuchBucket", failure.ErrorCode);
    }

    /// <summary>Everything <c>objects-init</c> is supposed to have left on the node.</summary>
    /// <param name="BucketId">The id Garage minted for the bucket compose declares.</param>
    /// <param name="Read">Whether the imported key may read that bucket.</param>
    /// <param name="Write">Whether it may write it.</param>
    /// <param name="Owner">Whether it owns it — which it must not.</param>
    /// <param name="LayoutVersion">
    /// The cluster layout version. Carried so a re-run can be held to leaving it alone: applying the
    /// layout is the script's one step that is not idempotent by nature.
    /// </param>
    private sealed record ProvisionedState(
        string BucketId,
        bool Read,
        bool Write,
        bool Owner,
        long LayoutVersion);

    /// <summary>The provisioned state, read off the node's own admin API.</summary>
    private async Task<ProvisionedState> ReadProvisionedStateAsync(CancellationToken cancellationToken)
    {
        var (bucketStatus, bucketBody) = await AdminAsync(
            "v2/GetBucketInfo?globalAlias=" + Uri.EscapeDataString(GarageFixture.Bucket),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, bucketStatus);

        using var bucket = JsonDocument.Parse(bucketBody);

        Assert.True(
            bucket.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String,
            $"GetBucketInfo answered no bucket id: {bucketBody}");

        Assert.True(
            bucket.RootElement.TryGetProperty("keys", out var keys)
                && keys.ValueKind == JsonValueKind.Array,
            $"GetBucketInfo listed no keys for the bucket: {bucketBody}");

        JsonElement? granted = null;

        foreach (var candidate in keys.EnumerateArray())
        {
            if (candidate.TryGetProperty("accessKeyId", out var keyId)
                && string.Equals(keyId.GetString(), GarageFixture.AccessKey, StringComparison.Ordinal))
            {
                granted = candidate;
                break;
            }
        }

        Assert.True(
            granted.HasValue,
            $"'{GarageFixture.AccessKey}' holds no grant on '{GarageFixture.Bucket}': {bucketBody}");

        Assert.True(
            granted.Value.TryGetProperty("permissions", out var permissions)
                && permissions.ValueKind == JsonValueKind.Object,
            $"'{GarageFixture.AccessKey}'s grant carries no permissions: {bucketBody}");

        var (layoutStatus, layoutBody) = await AdminAsync("v2/GetClusterLayout", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, layoutStatus);

        using var layout = JsonDocument.Parse(layoutBody);

        Assert.True(
            layout.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.Number,
            $"GetClusterLayout answered no version: {layoutBody}");

        return new ProvisionedState(
            id.GetString() ?? string.Empty,
            Flag(permissions, "read", bucketBody),
            Flag(permissions, "write", bucketBody),
            Flag(permissions, "owner", bucketBody),
            version.GetInt64());
    }

    /// <summary>
    /// The permission triple <c>compose.dev.yaml</c> grants: read and write, and deliberately
    /// not owner.
    /// </summary>
    private static void AssertGrantedPermissions(ProvisionedState state)
    {
        Assert.True(state.Read, "read was not granted.");
        Assert.True(state.Write, "write was not granted.");
        Assert.False(state.Owner, "owner was granted.");
    }

    /// <summary>One permission flag, named in the failure rather than thrown past it.</summary>
    private static bool Flag(JsonElement permissions, string name, string body)
    {
        Assert.True(
            permissions.TryGetProperty(name, out var flag)
                && flag.ValueKind is JsonValueKind.True or JsonValueKind.False,
            $"The grant carries no '{name}' permission: {body}");

        return flag.GetBoolean();
    }

    /// <summary>One GET against the node's admin API, with the deployment's own bearer token.</summary>
    private async Task<(HttpStatusCode Status, string Body)> AdminAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var client = garage.CreateAdminClient();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>
    /// The registered <see cref="IAssetStore"/>, pointed at the node — through
    /// <c>AddInfrastructure</c>, so the options binding and the client construction are the ones the
    /// application runs rather than a hand-assembled approximation of them.
    /// </summary>
    /// <param name="bucket">
    /// The bucket to configure. Every case but one passes the bucket the deployment declares; the
    /// refusal case passes one nothing ever created.
    /// </param>
    private IAssetStore Resolve(string bucket)
    {
        var values = TestConfiguration.Defaults();
        values["ConnectionStrings:Default"] = ValidConnectionString;

        // Trimmed of its trailing slash for the reason ObjectStoreAdapterTests documents: the SDK
        // appends its own, and two would put an empty path segment in front of the bucket.
        values[ObjectStoreOptions.ServiceUrlConfigurationKey] = garage.S3Endpoint.TrimEnd('/');
        values["ObjectStore:Region"] = GarageFixture.Region;
        values[ObjectStoreOptions.AccessKeyConfigurationKey] = GarageFixture.AccessKey;
        values[ObjectStoreOptions.SecretKeyConfigurationKey] = GarageFixture.SecretKey;
        values[ObjectStoreOptions.BucketConfigurationKey] = bucket;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new TestHostEnvironment());

        var provider = services.BuildServiceProvider();

        _providers.Add(provider);

        return provider.GetRequiredService<IAssetStore>();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _providers.Clear();

        GC.SuppressFinalize(this);
    }
}
