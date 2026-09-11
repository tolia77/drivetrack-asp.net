using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Deliveries;
using DriveTrack.Infrastructure.SideEffects;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// AD-12's two outbound ports, answered inside the process.
/// <para>
/// Every suite that exercises a side effect registers these. The real adapters reach a public
/// geocoding service and a mail relay, and a test that let them do it would assert against somebody
/// else's uptime — and would take about as long. What the story actually claims is about
/// <em>this</em> system: that the work is dispatched after the commit, that it never fails the
/// write, and that a failed send becomes a row. All three are claims about what happens on either
/// side of the port, which is exactly what a fake can stand in for.
/// </para>
/// <para>
/// Both types are written for two threads. The queue's worker calls them and the test asserts on
/// what they recorded, and those are different threads by construction — the whole point of
/// <see cref="IDeliverySideEffects"/> is that the caller did not wait.
/// </para>
/// </summary>
internal static class OutboundPorts
{
    /// <summary>Registers the fakes, last, so they win the resolve over the real adapters.</summary>
    /// <param name="geocoder">The geocoding port's stand-in.</param>
    /// <param name="sender">The mail transport's stand-in.</param>
    /// <param name="assets">
    /// The object store's stand-in, or null to leave the real adapter registered. Null is the right
    /// default for every suite that never captures a proof: the real adapter reaches nothing until
    /// it is called, so registering a fake it would not use would only hide which suites depend on
    /// the port.
    /// </param>
    public static Action<IServiceCollection> Replace(
        FakeGeocoder geocoder,
        FakeEmailSender sender,
        FakeAssetStore? assets = null) =>
        services =>
        {
            services.AddSingleton<IGeocoder>(geocoder);
            services.AddSingleton<IEmailSender>(sender);

            if (assets is not null)
            {
                services.AddSingleton<IAssetStore>(assets);
            }
        };

    /// <summary>
    /// Registers an in-memory object store and nothing else, for a suite whose subject is the proof
    /// capability rather than the side effects.
    /// </summary>
    public static Action<IServiceCollection> Replace(FakeAssetStore assets) =>
        services => services.AddSingleton<IAssetStore>(assets);

    /// <summary>Waits for every queued side effect to finish, however it finished.</summary>
    /// <remarks>
    /// The queue's own signal rather than a delay. A delay long enough to be reliable on a loaded
    /// machine is paid by every run; one short enough to be quick fails a few times a month for no
    /// reason anybody can reproduce.
    /// </remarks>
    public static Task DrainAsync(ApiFactory factory, CancellationToken cancellationToken) =>
        factory.Services
            .GetRequiredService<DeliverySideEffectQueue>()
            .WhenIdleAsync(cancellationToken);
}

/// <summary>A geocoder that answers whatever the test told it to, and remembers what it was asked.</summary>
internal sealed class FakeGeocoder : IGeocoder
{
    private readonly Lock _gate = new();
    private readonly List<(double Latitude, double Longitude)> _described = [];
    private readonly List<string> _searched = [];

    /// <summary>What every reverse lookup answers. Null is FR-94's "the provider had nothing".</summary>
    public string? Address { get; set; }

    /// <summary>When set, every reverse lookup throws it instead of answering.</summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// When set, a reverse lookup for coordinates this matches throws
    /// <see cref="PointFailure"/> and every other point answers normally.
    /// </summary>
    /// <remarks>
    /// The runner claims each point is contained on its own — "a dropoff lookup that throws must not
    /// discard a pickup address that already resolved". <see cref="Failure"/> is all-or-nothing and
    /// cannot state that claim: with it set, neither point resolves, so a runner that abandoned both
    /// on the first exception would pass. This is what lets one point fail while the other answers.
    /// </remarks>
    public Func<double, double, bool>? FailsFor { get; set; }

    /// <summary>What <see cref="FailsFor"/> throws when it matches.</summary>
    public Exception PointFailure { get; set; } =
        new HttpRequestException("This point could not be described.");

    /// <summary>
    /// When set, a reverse lookup waits for it before answering — with the answer it would have
    /// given on the way in, not the one <see cref="Address"/> holds on the way out.
    /// </summary>
    /// <remarks>
    /// The seconds between a lookup and the write are seconds a dispatcher can spend moving the
    /// point, and the runner refuses to write an address whose coordinates have since changed. That
    /// window does not exist in a test where the fake answers from a field, so this is how a test
    /// opens one: hold the job here, edit the delivery, release. The answer is captured before the
    /// wait so that the held job returns the address of where the point <em>was</em>, which is the
    /// whole hazard — an answer that arrives correct and is stale by the time it is written.
    /// </remarks>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Completes when a lookup first reaches <see cref="Gate"/>.</summary>
    /// <remarks>
    /// The test's signal that the job really is in flight. Without it the edit can land before the
    /// worker has picked the job up at all, and the race the test is named for never happens — it
    /// would pass by not testing anything rather than by holding the rule.
    /// </remarks>
    public TaskCompletionSource Arrived { get; } = new();

    /// <summary>What every forward lookup answers.</summary>
    public IReadOnlyList<GeocodedPlace> Matches { get; set; } = [];

    /// <summary>The coordinates asked about, in order.</summary>
    public IReadOnlyList<(double Latitude, double Longitude)> Described
    {
        get
        {
            lock (_gate)
            {
                return [.. _described];
            }
        }
    }

    /// <summary>The queries asked about, in order.</summary>
    public IReadOnlyList<string> Searched
    {
        get
        {
            lock (_gate)
            {
                return [.. _searched];
            }
        }
    }

    /// <inheritdoc />
    public async Task<string?> DescribeAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _described.Add((latitude, longitude));
        }

        // Recorded before it throws, deliberately: "the geocoder was asked and could not answer" is
        // a different claim from "the geocoder was never asked", and the third row of the matrix is
        // about the second one.
        if (Failure is not null)
        {
            throw Failure;
        }

        var answer = Address;

        if (Gate is { } gate)
        {
            Arrived.TrySetResult();

            await gate.Task.WaitAsync(cancellationToken);
        }

        return FailsFor?.Invoke(latitude, longitude) == true ? throw PointFailure : answer;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GeocodedPlace>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _searched.Add(query);
        }

        return Task.FromResult(Matches);
    }
}

/// <summary>
/// AD-26's object store, answered inside the process: a dictionary of keys to bytes.
/// <para>
/// Garage is a container and an S3 protocol, and a suite that ran one to prove the ordering rule
/// would be asserting against somebody else's daemon. What story 6.1 actually claims is about
/// <em>this</em> system: that a refused capture writes nothing at all, that the bytes are written
/// before the transaction that names them opens, and that a committed key always resolves. All
/// three are claims about what happens on either side of the port, which is exactly what a fake can
/// stand in for — and the real adapter's own protocol is asserted separately, against a loopback
/// socket, in <c>ObjectStoreAdapterTests</c>.
/// </para>
/// <para>
/// Written for two threads, like the two fakes above it: a capture runs on the request's thread and
/// the test reads <see cref="Saved"/> on its own.
/// </para>
/// </summary>
internal sealed class FakeAssetStore : IAssetStore
{
    private readonly Lock _gate = new();
    private readonly List<SavedAsset> _saved = [];
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    /// <summary>One stored object as the store received it.</summary>
    /// <param name="Key">The key it was minted under.</param>
    /// <param name="ContentType">The type it was stored with.</param>
    /// <param name="Content">The bytes, copied, so a disposed source stream cannot change them.</param>
    internal sealed record SavedAsset(string Key, string ContentType, byte[] Content);

    /// <summary>When set, every save throws it instead of succeeding.</summary>
    /// <remarks>
    /// This is how the "store write fails" row of the matrix is reached. The port's contract is that
    /// a failed save throws so that nothing is committed against a key the store does not hold, and
    /// a fake that could only succeed could not assert the half that matters.
    /// </remarks>
    public Exception? Failure { get; set; }

    /// <summary>
    /// When set, <see cref="OpenAsync"/> answers null for every key, whatever was saved. The "row
    /// exists, object does not" row of the matrix, without emptying the dictionary by hand.
    /// </summary>
    public bool Empty { get; set; }

    /// <summary>Everything handed over, in the order it was handed over.</summary>
    public IReadOnlyList<SavedAsset> Saved
    {
        get
        {
            lock (_gate)
            {
                return [.. _saved];
            }
        }
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (Failure is not null)
        {
            throw Failure;
        }

        // Copied rather than referenced: the caller disposes the source once the capture is done,
        // and a test asserting on the bytes afterwards would otherwise be reading a closed stream.
        using var buffer = new MemoryStream();

        await content.CopyToAsync(buffer, cancellationToken);

        var bytes = buffer.ToArray();
        var key = "proof/" + Guid.NewGuid().ToString("N");

        lock (_gate)
        {
            _objects[key] = bytes;
            _saved.Add(new SavedAsset(key, contentType, bytes));
        }

        return key;
    }

    /// <inheritdoc />
    public Task<Stream?> OpenAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Empty)
        {
            return Task.FromResult<Stream?>(null);
        }

        lock (_gate)
        {
            return Task.FromResult<Stream?>(
                _objects.TryGetValue(key, out var bytes)
                    ? new MemoryStream(bytes, writable: false)
                    : null);
        }
    }
}

/// <summary>A transport that records what it was handed, or throws what the test told it to.</summary>
internal sealed class FakeEmailSender : IEmailSender
{
    private readonly Lock _gate = new();
    private readonly List<EmailMessage> _sent = [];

    /// <summary>When set, every send throws it instead of succeeding.</summary>
    public Exception? Failure { get; set; }

    /// <summary>The messages handed over, in order.</summary>
    public IReadOnlyList<EmailMessage> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            return Task.FromException(Failure);
        }

        lock (_gate)
        {
            _sent.Add(message);
        }

        return Task.CompletedTask;
    }
}
