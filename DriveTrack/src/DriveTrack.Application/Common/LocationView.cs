namespace DriveTrack.Application.Common;

/// <summary>
/// A stored point as every screen reads one (AD-11, AD-17): the coordinates, which are the record,
/// and the address text, which is a cache of what a geocoder last resolved for them (DR-11).
/// <para>
/// <see cref="Address"/> is nullable and stays nullable: story 5.2 owns the geocoder, and story 5.1
/// has to work while there is none — so a screen falls back to showing the coordinates rather than
/// an empty cell (FR-94). Nothing reads the address as authoritative, and nothing here resolves
/// one: this is a shape, not a service.
/// </para>
/// <para>
/// The point is <see cref="MapLocation"/>, the one coordinate pair the shared map component binds
/// to, so a location can be handed straight to a map without a second conversion — which is what
/// keeps every epic's map showing the same thing.
/// </para>
/// </summary>
/// <param name="Point">The coordinates, always present.</param>
/// <param name="Address">The last resolved address, or null when none has been.</param>
public sealed record LocationView(MapLocation Point, string? Address);
