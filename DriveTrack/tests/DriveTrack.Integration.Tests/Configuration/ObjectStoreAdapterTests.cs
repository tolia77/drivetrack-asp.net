using System.Text;
using DriveTrack.Application.Abstractions;
using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Objects;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Configuration;

/// <summary>
/// The real <see cref="IAssetStore"/>, resolved from the container <c>AddInfrastructure</c> builds
/// and pointed at a loopback socket speaking S3's half of the conversation.
/// <para>
/// Every other suite replaces this port with a fake, which is right — a test of AD-26's ordering has
/// no business running a storage daemon. But it leaves the adapter itself unexecuted, and the three
/// ways it can be wrong are all silent in exactly the same manner: drop <c>ForcePathStyle</c>, sign
/// the wrong region, or leave chunk encoding on, and every test in the solution stays green while a
/// deployment answers 403 to every capture. The bucket, the path and the credential scope the
/// adapter actually puts on the wire are therefore asserted here, byte for byte.
/// </para>
/// <para>
/// No database and no container: <c>AddInfrastructure</c> needs a connection string to be present,
/// not to be reachable, and nothing here opens one.
/// </para>
/// </summary>
public class ObjectStoreAdapterTests : IDisposable
{
    /// <summary>Every container <see cref="Resolve"/> built, released with the test.</summary>
    private readonly List<ServiceProvider> _providers = [];

    private const string ValidConnectionString =
        "Host=localhost;Port=5432;Database=drivetrack;Username=drivetrack;Password=irrelevant";

    private const string Bucket = "drivetrack-proofs";

    /// <summary>A Garage-shaped key pair, so the signer is given what it would really be given.</summary>
    private const string AccessKey = "GK0123456789abcdef01234567";

    /// <inheritdoc cref="AccessKey" />
    private const string SecretKey =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task A_save_puts_the_object_path_style_under_the_configured_bucket()
    {
        // ForcePathStyle, asserted as the only thing that can show it: the bucket is a path segment.
        // Without it the SDK writes the bucket into the host name, and `drivetrack-proofs.objects`
        // is a name nothing resolves - a failure that never reaches the server at all, so a test
        // that only checked the answer would see a timeout and learn nothing.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            string.Empty,
            "application/xml"));

        var store = Resolve(server);

        var key = await store.SaveAsync(Content("evidence"), "image/png", cancellationToken);

        var request = Assert.Single(server.Requests);

        Assert.Equal("PUT", request.Method);
        Assert.StartsWith("/" + Bucket + "/proof/", request.Target, StringComparison.Ordinal);

        // The key the port answered is the key the object was written under, which is the whole of
        // what "opaque" has to mean: the caller stores this string and nothing else.
        Assert.EndsWith(key, request.Target, StringComparison.Ordinal);
        Assert.StartsWith("proof/", key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_save_signs_for_the_configured_region_and_the_S3_service()
    {
        // AuthenticationRegion, asserted through the credential scope SigV4 puts in the header. A
        // signature computed for us-east-1 against a node expecting `garage` simply does not match,
        // and the answer is a 403 whose body mentions no region at all - which is why this is
        // checked on the way out rather than inferred from a refusal on the way back.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            string.Empty,
            "application/xml"));

        await Resolve(server).SaveAsync(Content("evidence"), "image/png", cancellationToken);

        var authorization = Assert.Single(server.Requests).Header("Authorization");

        Assert.NotNull(authorization);
        Assert.Contains("AWS4-HMAC-SHA256", authorization, StringComparison.Ordinal);
        Assert.Contains("/garage/s3/aws4_request", authorization, StringComparison.Ordinal);
        Assert.Contains("Credential=" + AccessKey, authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_save_sends_the_bytes_as_one_signed_body_of_a_known_length()
    {
        // UseChunkEncoding = false, asserted as the body itself: with chunking on the payload
        // arrives wrapped in `aws-chunked` frames with a per-chunk signature, and the bytes on the
        // wire are not the bytes that were handed over.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            200,
            string.Empty,
            "application/xml"));

        await Resolve(server).SaveAsync(Content("докази"), "image/webp", cancellationToken);

        var request = Assert.Single(server.Requests);

        Assert.Equal("докази", request.BodyText);
        Assert.Equal(
            Encoding.UTF8.GetByteCount("докази").ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Header("Content-Length"));

        // The type travels with the object, so a read can answer it without sniffing (DR-14).
        Assert.Equal("image/webp", request.Header("Content-Type"));

        // And the framing the setting turns off is absent.
        Assert.DoesNotContain(
            "aws-chunked",
            request.Header("Content-Encoding") ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_object_round_trips_through_the_adapter()
    {
        // The pair, because either half can be wrong on its own: a write that lands under a key the
        // read does not ask for is a store that loses everything and reports nothing.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(request =>
            request.Method == "GET"
                ? new LoopbackHttpServer.CannedResponse(200, "докази", "image/png")
                : new LoopbackHttpServer.CannedResponse(200, string.Empty, "application/xml"));

        var store = Resolve(server);

        var key = await store.SaveAsync(Content("докази"), "image/png", cancellationToken);

        await using var content = await store.OpenAsync(key, cancellationToken);

        Assert.NotNull(content);

        using var reader = new StreamReader(content, Encoding.UTF8);

        Assert.Equal("докази", await reader.ReadToEndAsync(cancellationToken));

        var read = server.Requests.Single(request => request.Method == "GET");

        Assert.Equal("/" + Bucket + "/" + key, read.Target);
    }

    [Fact]
    public async Task A_key_the_store_does_not_hold_answers_no_stream_rather_than_throwing()
    {
        // The port's read contract, and the one place it differs from its write contract: a row can
        // outlive its object if a bucket is emptied out of band, and the honest answer to "show me
        // this image" is then that there is none. The caller turns this into a 404.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            404,
            "<Error><Code>NoSuchKey</Code></Error>",
            "application/xml"));

        Assert.Null(await Resolve(server).OpenAsync("proof/missing", cancellationToken));
    }

    [Fact]
    public async Task A_store_that_refuses_the_write_throws_rather_than_answering_a_key()
    {
        // The opposite of the geocoder's contract, and deliberately so: a save that failed quietly
        // would let a proof row commit against a key that resolves to nothing, which is evidence
        // that says it exists and does not (AD-26).
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpServer.CannedResponse(
            403,
            "<Error><Code>AccessDenied</Code></Error>",
            "application/xml"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Resolve(server).SaveAsync(Content("evidence"), "image/png", cancellationToken));
    }

    [Fact]
    public async Task An_unconfigured_store_refuses_by_name_rather_than_reaching_somebody_elses()
    {
        // ServiceUrl has no committed default, and this is why: the SDK's own default is Amazon S3,
        // so a deployment that never configured an endpoint would sign requests to a service nobody
        // chose. Blank is answered here, naming the variable an operator would search for.
        var cancellationToken = TestContext.Current.CancellationToken;

        var values = TestConfiguration.Defaults();
        values["ConnectionStrings:Default"] = ValidConnectionString;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new TestHostEnvironment());

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<IAssetStore>()
                .SaveAsync(Content("evidence"), "image/png", cancellationToken));

        Assert.Contains("ObjectStore__ServiceUrl", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The registered <see cref="IAssetStore"/>, pointed at the server — through
    /// <c>AddInfrastructure</c>, so the options binding and the client construction are the ones the
    /// application runs rather than a hand-assembled approximation of them.
    /// </summary>
    private IAssetStore Resolve(LoopbackHttpServer server)
    {
        var values = TestConfiguration.Defaults();
        values["ConnectionStrings:Default"] = ValidConnectionString;

        // Trimmed of its trailing slash: the SDK appends its own, and two would put an empty path
        // segment in front of the bucket - which is exactly the kind of thing this suite exists to
        // notice rather than to tolerate.
        values[ObjectStoreOptions.ServiceUrlConfigurationKey] = server.BaseAddress.TrimEnd('/');
        values["ObjectStore:Region"] = "garage";
        values["ObjectStore:AccessKey"] = AccessKey;
        values["ObjectStore:SecretKey"] = SecretKey;
        values[ObjectStoreOptions.BucketConfigurationKey] = Bucket;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new TestHostEnvironment());

        var provider = services.BuildServiceProvider();

        _providers.Add(provider);

        return provider.GetRequiredService<IAssetStore>();
    }

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

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
