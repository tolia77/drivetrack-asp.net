namespace DriveTrack.Application.Abstractions;

/// <summary>
/// A place a forward lookup found: what it is called, and where it is (FR-104).
/// </summary>
/// <param name="Address">The human-readable address the provider returned.</param>
/// <param name="Latitude">Latitude in decimal degrees.</param>
/// <param name="Longitude">Longitude in decimal degrees.</param>
public sealed record GeocodedPlace(string Address, double Latitude, double Longitude);

/// <summary>
/// AD-12's geocoding port, in both directions: coordinates to an address (FR-94) and a typed
/// query to a list of places (FR-104).
/// <para>
/// The original called out to a geocoding service from inside <c>Location.get_address()</c> — a
/// model class issuing a network request, which DR-12 and NFR-8 forbid. The capability lives here
/// instead, behind an interface Application owns and Infrastructure implements, and the reverse
/// direction is called after the transaction rather than during a render.
/// </para>
/// <para>
/// Neither method is allowed to be the reason an operation fails. A provider that is down, slow or
/// unhelpful answers <c>null</c> and an empty list respectively (DR-11: the coordinates are
/// authoritative and the address is a cache), so the only failure an adapter may raise is one the
/// caller could do something about — and there is none.
/// </para>
/// </summary>
public interface IGeocoder
{
    /// <summary>
    /// The address at those coordinates, or null when the provider has none or cannot be reached
    /// (FR-94).
    /// </summary>
    /// <param name="latitude">Latitude in decimal degrees.</param>
    /// <param name="longitude">Longitude in decimal degrees.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> DescribeAsync(double latitude, double longitude, CancellationToken cancellationToken);

    /// <summary>
    /// The places matching a typed query, newest provider ranking first, or an empty list when
    /// there are none (FR-104).
    /// </summary>
    /// <param name="query">What the dispatcher typed. Already validated by the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<GeocodedPlace>> SearchAsync(string query, CancellationToken cancellationToken);
}
