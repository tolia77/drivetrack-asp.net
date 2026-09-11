using System.Globalization;
using System.Text.Json;
using DriveTrack.Application.Abstractions;
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
/// </summary>
internal sealed class NominatimGeocoder(HttpClient http, IOptions<GeocoderOptions> options) : IGeocoder
{
    private readonly GeocoderOptions _options = options.Value;

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

        using var document = await ReadAsync(url, cancellationToken);

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

        using var document = await ReadAsync(url, cancellationToken);

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
    private async Task<JsonDocument?> ReadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            // The agent per request rather than on the shared client's defaults: the value comes
            // from configuration, and a header set once at registration would be a value bound
            // before the options it was read from could be validated.
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Absolute));
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

            using var response = await http.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return JsonDocument.Parse(body);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
