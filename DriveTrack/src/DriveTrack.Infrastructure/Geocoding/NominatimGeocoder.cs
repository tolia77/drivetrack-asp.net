using System.Globalization;
using System.Net;
using System.Text.Json;
using DriveTrack.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveTrack.Infrastructure.Geocoding;

/// <summary>
/// <see cref="IGeocoder"/> over Nominatim's public JSON interface.
/// <para>
/// The port promises that a lookup is never the reason an operation fails, so every failure this
/// adapter can meet — an endpoint that is not configured, a refusal, a timeout, a body that is not
/// what was expected — answers null or an empty list. The one thing it must not do is throw:
/// nothing above it has a caller left to tell, and DR-11 already says what an absent address means.
/// </para>
/// <para>
/// The query strings are built with <see cref="CultureInfo.InvariantCulture"/>. A Ukrainian culture
/// writes 50,4501 for a latitude, and a comma is a value separator to every geocoding service in
/// existence — so the one place NFR-15's formatting must not reach is the wire.
/// </para>
/// <para>
/// Outbound requests are paced: no two leave closer together than
/// <see cref="GeocoderOptions.MinimumRequestIntervalMilliseconds"/>. Nominatim's usage policy is one
/// request per second, and the callers above this adapter cannot see one another — a delivery
/// create queues two reverse lookups back to back while a dispatcher's forward search draws on the
/// same quota — so the gate belongs here, at the one place every request passes through.
/// </para>
/// </summary>
internal sealed class NominatimGeocoder(
    HttpClient http,
    IOptions<GeocoderOptions> options,
    TimeProvider timeProvider,
    ILogger<NominatimGeocoder> logger) : IGeocoder, IDisposable
{
    private readonly GeocoderOptions _options = options.Value;

    /// <summary>
    /// Serializes the gate, not the wire. It is held only long enough to wait out the remainder of
    /// the interval and stamp the turn, and is released before the request is built — so a slow
    /// response never widens the spacing, and the concurrency the callers see is unchanged.
    /// </summary>
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>
    /// When the last request started, as a <see cref="TimeProvider.GetTimestamp"/> reading, or null
    /// while none has. Read and written only under <see cref="_turn"/>.
    /// </summary>
    private long? _lastStartedAt;

    /// <inheritdoc />
    public async Task<string?> DescribeAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return null;
        }

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{_options.Endpoint}?format=json&lat={latitude}&lon={longitude}");

        using var document = await ReadAsync(url, "reverse lookup", cancellationToken);

        if (document is null)
        {
            return null;
        }

        // Nominatim answers a refusal as a 200 carrying {"error": ...}, so the shape is checked
        // rather than the status alone.
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("display_name", out var name)
            && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GeocodedPlace>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(_options.SearchEndpoint))
        {
            return [];
        }

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{_options.SearchEndpoint}?format=json&limit={MaximumMatches}&q={Uri.EscapeDataString(query)}");

        using var document = await ReadAsync(url, "forward search", cancellationToken);

        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var places = new List<GeocodedPlace>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (Place(element) is { } place)
            {
                places.Add(place);
            }
        }

        return places;
    }

    /// <summary>
    /// How many matches a search asks for. The list sits under a text box on a form, and a
    /// dispatcher scanning fifty rows for the street they typed is a dispatcher who would rather
    /// have clicked the map.
    /// </summary>
    private const int MaximumMatches = 5;

    /// <summary>
    /// One element of a search result, or null when it is not a place this system can use.
    /// </summary>
    /// <remarks>
    /// The coordinates are range-checked here rather than left to <c>MapLocation</c>'s constructor.
    /// That constructor throws, deliberately, because a coordinate outside the decimal-degree range
    /// is a programming error everywhere it is reachable from inside the system — but this is the
    /// one place the numbers come from outside it, and a third party's malformed row must not
    /// become a 500 on a dispatcher's search.
    /// </remarks>
    private static GeocodedPlace? Place(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty("display_name", out var name)
            || name.ValueKind != JsonValueKind.String
            || name.GetString() is not { Length: > 0 } address)
        {
            return null;
        }

        if (Coordinate(element, "lat") is not { } latitude
            || Coordinate(element, "lon") is not { } longitude)
        {
            return null;
        }

        // Written as a negated range rather than as two comparisons, which is the form MapLocation
        // itself uses and for the same reason: NaN fails every comparison, so `is < -90 or > 90`
        // waves it through. It is reachable - double.TryParse accepts the literal "NaN" - and one
        // row further on it would reach MapLocation's throwing constructor and turn a dispatcher's
        // search into a 500, which is the outcome this method exists to prevent.
        if (latitude is not (>= -90 and <= 90) || longitude is not (>= -180 and <= 180))
        {
            return null;
        }

        return new GeocodedPlace(address, latitude, longitude);
    }

    /// <summary>
    /// A coordinate, which Nominatim writes as a JSON <em>string</em> rather than a number, parsed
    /// invariantly for the reason the query string is built invariantly.
    /// </summary>
    private static double? Coordinate(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : null;
    }

    /// <summary>
    /// The response body as JSON, or null for any reason at all that it is not.
    /// </summary>
    /// <remarks>
    /// Every exception is caught, and the breadth is the contract rather than laziness: the port
    /// promises that a geocoder is never the reason an operation fails, and the failures reachable
    /// here — a socket that would not open, a name that would not resolve, a request the timeout
    /// abandoned, a body that is not JSON — are each the same answer as far as the caller is
    /// concerned. That answer is "no address", and DR-11 has already decided what it means.
    /// </remarks>
    private async Task<JsonDocument?> ReadAsync(
        string url,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            // One deadline for the whole lookup — the wait at the gate and the wire call together —
            // rather than one each. Two would let a lookup cost twice the deadline the deployment
            // set, and it is on the injected clock (AD-13) so that the budget and the pacing wait
            // cannot disagree with each other about how much time has passed.
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(_options.TimeoutSeconds),
                timeProvider);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);

            // Both public methods funnel through here, so one gate paces both directions. Inside
            // the try, like everything else: the port promises this method never throws, and a
            // caller whose own token source has already been disposed would otherwise escape.
            if (!await WaitForTurnAsync(operation, budget.Token))
            {
                return null;
            }

            // The agent per request rather than on the shared client's defaults: the value comes
            // from configuration, and a header set once at registration would be a value bound
            // before the options it was read from could be validated.
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Absolute));
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

            using var response = await http.SendAsync(request, budget.Token);

            if (!response.IsSuccessStatusCode)
            {
                // Worth its own line, because these are the failures that say the pacing itself is
                // wrong for this provider: Nominatim answers 429 to a caller asking too often, 403
                // to one it has decided is abusive, and 503 when it is shedding load. Everything
                // else here is somebody's server having a bad day; these three are the deployment
                // being told so, and flattened to a null address they are indistinguishable from
                // DR-11's perfectly legitimate absent one.
                if (response.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.Forbidden
                    or HttpStatusCode.ServiceUnavailable)
                {
                    logger.LogWarning(
                        "Geocoder {Operation} was refused by the provider (status {Status}, "
                            + "Retry-After: {RetryAfter}) — a rate limit, a block or load shedding. "
                            + "The address is absent. Consider raising "
                            + "'Geocoder__MinimumRequestIntervalMilliseconds'.",
                        operation,
                        (int)response.StatusCode,
                        // The provider's own answer to "how long", which beats the operator
                        // guessing at an interval. Most refusals carry it; some do not.
                        response.Headers.RetryAfter?.ToString() ?? "not sent");
                }

                return null;
            }

            var body = await response.Content.ReadAsStringAsync(budget.Token);

            return JsonDocument.Parse(body);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits until this request is allowed to start, answering false when it is not going to be.
    /// </summary>
    /// <remarks>
    /// The wait shares the whole lookup's single deadline, handed in by <c>ReadAsync</c>: the
    /// caller's token linked to <see cref="GeocoderOptions.TimeoutSeconds"/> counted from the
    /// moment the lookup began. A queue of waiters therefore cannot hold a dispatcher's search past
    /// the deadline the deployment already set, and time spent at the gate is time the wire call no
    /// longer has — one lookup, one budget. Giving up leaves <see cref="_lastStartedAt"/>
    /// untouched, which is correct: no request was sent, so nothing has moved the next turn along.
    /// </remarks>
    /// <param name="operation">What the caller was doing, for the log line.</param>
    /// <param name="deadline">The lookup's single deadline, from <c>ReadAsync</c>.</param>
    private async Task<bool> WaitForTurnAsync(string operation, CancellationToken deadline)
    {
        var interval = TimeSpan.FromMilliseconds(_options.MinimumRequestIntervalMilliseconds);

        if (interval <= TimeSpan.Zero)
        {
            // Pacing off. A self-hosted provider carries no usage policy, and a deployment that
            // said so should pay nothing at all for the gate — not even the semaphore.
            return true;
        }

        try
        {
            await _turn.WaitAsync(deadline);

            try
            {
                if (_lastStartedAt is { } previous
                    && interval - timeProvider.GetElapsedTime(previous) is { Ticks: > 0 } remaining)
                {
                    await Task.Delay(remaining, timeProvider, deadline);
                }

                _lastStartedAt = timeProvider.GetTimestamp();
            }
            finally
            {
                _turn.Release();
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // A paced skip, which is a distinct outcome from the provider having no address for
            // those coordinates. Both answer null to the caller — DR-11 leaves them one answer —
            // so the log is the only place an operator can tell them apart.
            logger.LogWarning(
                "Geocoder {Operation} was skipped at the request pacing gate before reaching the "
                    + "provider, after waiting for its turn. The address is absent.",
                operation);

            return false;
        }
        catch (Exception exception)
        {
            // Not a paced skip, and it must not read like one: a disposed semaphore at shutdown has
            // nothing to do with the interval, and a warning that named the interval would send an
            // operator to the one knob that cannot be the cause. Still false — the port's contract
            // is unconditional, and the caller gets the same absent address either way.
            logger.LogWarning(
                exception,
                "Geocoder {Operation} could not pass the request pacing gate. The address is absent.",
                operation);

            return false;
        }
    }

    /// <summary>
    /// Releases the gate and the client this adapter was handed exclusively.
    /// </summary>
    /// <remarks>
    /// The client is constructed for this adapter alone rather than resolved from the container, so
    /// there is nothing else holding it and nothing else that could be disposing it.
    /// </remarks>
    public void Dispose()
    {
        _turn.Dispose();
        http.Dispose();
    }
}
