using DriveTrack.Application.Common;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// One answer to an address search (FR-104): what the place is called, and the point a picker would
/// have produced by clicking it.
/// <para>
/// A capability's own shape rather than the port's <c>GeocodedPlace</c>, for AD-17's reason: a
/// record declared beside a port is part of that port's contract, and handing it to a screen would
/// make every consumer of the delivery form a consumer of the geocoding abstraction too. Carrying
/// a <see cref="MapLocation"/> is what makes choosing a match and clicking the map the same
/// assignment on the same field.
/// </para>
/// </summary>
/// <param name="Address">The place as the provider describes it.</param>
/// <param name="Point">Where it is.</param>
public sealed record PlaceMatch(string Address, MapLocation Point);
