namespace DriveTrack.Domain.Common;

/// <summary>
/// A point on the map, embedded in the row of whatever owns it (AD-11): there is no
/// <c>locations</c> table and no location identity.
/// <para>
/// The coordinates are the record; <paramref name="Address"/> is a cache of whatever a
/// geocoder last resolved for them and is never read as authoritative (DR-11). This type has
/// no method that touches the network — the original's <c>Location.get_address()</c> issued
/// an HTTP call from inside a model class, which is exactly what DR-12 and NFR-8 forbid.
/// Resolving an address is a port call made by Application, after the transaction (AD-12).
/// </para>
/// </summary>
/// <param name="Latitude">Latitude in decimal degrees.</param>
/// <param name="Longitude">Longitude in decimal degrees.</param>
/// <param name="Address">Last resolved human-readable address, or null when unresolved.</param>
/// <param name="AddressResolvedAt">When <paramref name="Address"/> was resolved, or null.</param>
public sealed record Location(
    double Latitude,
    double Longitude,
    string? Address,
    DateTimeOffset? AddressResolvedAt);
