namespace DriveTrack.Application.Vehicles;

/// <summary>
/// NFR-27's paging over the fleet list, expressed as a request rather than as two loose integers.
/// <para>
/// The original's <c>GET /vehicles</c> paged with <c>skip</c> and <c>limit</c> and defaulted them
/// to 0 and 100; this preserves exactly that and adds the half the original was missing — the
/// values are validated before a query is built, so a negative offset or a limit of two million is
/// refused rather than passed through to the database.
/// </para>
/// </summary>
/// <param name="Offset">Rows to skip. Zero is the first page.</param>
/// <param name="Limit">Rows to take, at most <see cref="ListVehiclesQueryValidator.MaximumLimit"/>.</param>
public sealed record ListVehiclesQuery(int Offset, int Limit);
