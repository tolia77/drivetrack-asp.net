namespace DriveTrack.Infrastructure.Geocoding;

/// <summary>
/// The geocoding settings, bound from the <c>Geocoder</c> configuration section (AD-19).
/// <para>
/// Every value arrives as an environment variable in the container — <c>Geocoder__Endpoint</c>,
/// <c>Geocoder__SearchEndpoint</c>, <c>Geocoder__UserAgent</c>, <c>Geocoder__TimeoutSeconds</c> —
/// and each one appears in <c>.env.example</c> and is forwarded a line at a time in
/// <c>compose.yaml</c>.
/// </para>
/// <para>
/// Neither endpoint has a default, deliberately. A committed default would make every environment
/// that never configured one — a test host included — issue live requests to somebody else's public
/// service the first time a delivery was created. Blank means "no geocoding is configured", which
/// DR-11 already has an answer for: the address stays absent and the screen shows the coordinates.
/// </para>
/// </summary>
public sealed class GeocoderOptions
{
    /// <summary>The configuration section these settings bind from.</summary>
    public const string SectionName = "Geocoder";

    /// <summary>
    /// Configuration key of the request timeout, in the colon form. The environment-variable
    /// spelling is <c>Geocoder__TimeoutSeconds</c>, which is what the startup failure names.
    /// </summary>
    public const string TimeoutSecondsConfigurationKey = "Geocoder:TimeoutSeconds";

    /// <summary>The reverse endpoint: coordinates in, an address out (FR-94). Blank disables it.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The forward endpoint: a typed address in, places out (FR-104). Blank disables it.</summary>
    public string SearchEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// The agent string every request carries. Nominatim refuses a request without one, so this is
    /// a requirement of the protocol rather than a courtesy.
    /// </summary>
    public string UserAgent { get; set; } = "DriveTrack/1.0";

    /// <summary>
    /// How long a lookup may take before it is abandoned. Short on purpose: an address is a
    /// convenience DR-11 makes optional, and nothing waits for it.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;
}
