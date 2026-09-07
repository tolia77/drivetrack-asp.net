using DriveTrack.Domain.Identity;

namespace DriveTrack.Domain.Shifts;

/// <summary>
/// A stretch of time a driver was on duty.
/// <para>
/// There is no status column: an open shift is <see cref="EndedAt"/> <c>IS NULL</c>, and that
/// exact predicate is the filter of the partial unique index enforcing one open shift per
/// driver (FR-110, FR-117). The filter must be immutable, so nothing time-dependent may appear
/// in it. Ending a shift is a one-way transition — no command may null <see cref="EndedAt"/>
/// back to reopen one (AD-23).
/// </para>
/// <para>
/// A shift references its driver directly and nothing else. It carries no delivery reference
/// (DR-13), which is what retires the denormalized-owner problem DR-8 described.
/// </para>
/// </summary>
public sealed class Shift
{
    /// <summary>Surrogate key.</summary>
    public int Id { get; set; }

    /// <summary>The driver on duty. The shift dies with the driver (FR-39).</summary>
    public DriverId DriverId { get; set; }

    /// <summary>When the shift started, at offset zero (AD-13).</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the shift ended, or null while it is open.</summary>
    public DateTimeOffset? EndedAt { get; set; }
}
