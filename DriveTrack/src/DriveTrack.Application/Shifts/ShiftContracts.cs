using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Shifts;

namespace DriveTrack.Application.Shifts;

/// <summary>
/// What a driver sends to go on duty (FR-109).
/// <para>
/// The instant is not a field and never can be: it is <c>TimeProvider.GetUtcNow()</c>'s answer
/// inside the service (AD-13). A start a caller could name is a start a caller could backdate, and
/// the whole point of a shift is that it records when somebody actually went on duty.
/// </para>
/// <para>
/// The driver <em>is</em> a field, because dispatch may put a driver on duty as well as the driver
/// themselves (FR-113). It is not a hole: <c>IAccessGuard.RequireShiftOwner</c> refuses a driver
/// who names anybody but themselves.
/// </para>
/// </summary>
/// <param name="DriverId">The driver going on duty.</param>
public sealed record StartShiftCommand(DriverId DriverId);

/// <summary>What a driver sends to go off duty (FR-109). The instant is the injected clock's, as it is on the way in.</summary>
/// <param name="DriverId">The driver going off duty.</param>
public sealed record EndShiftCommand(DriverId DriverId);

/// <summary>
/// What a caller sends to record a shift that already happened: dispatch for anybody's driver, a
/// driver for their own (FR-112, FR-113).
/// <para>
/// The same rule the corrections carry, and the same guard member states it —
/// <c>IAccessGuard.RequireShiftOwner</c> passes dispatch for every driver and a driver only for
/// themselves. FR-112 gives a driver their own shifts to correct, and a shift they forgot to log at
/// all is the first correction they need; withholding this one route while leaving them the edit and
/// the delete would be a rule nothing else in the capability keeps.
/// </para>
/// <para>
/// Both instants are the caller's here, unlike <see cref="StartShiftCommand"/>: this is the
/// after-the-fact path, and somebody entering a shift that has already been worked is naming a
/// window rather than reporting the present moment.
/// </para>
/// </summary>
/// <param name="DriverId">The driver the shift belongs to.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="EndedAt">When it ended, or null to open it.</param>
public sealed record CreateShiftCommand(
    DriverId DriverId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>
/// What a caller sends to correct a shift (FR-114).
/// <para>
/// AD-23: both fields are <see cref="Optional{T}"/>, so an edit that only moves the start leaves
/// the end alone rather than clearing it.
/// </para>
/// <para>
/// <see cref="EndedAt"/> is <c>Optional&lt;DateTimeOffset&gt;</c> and deliberately not
/// <c>Optional&lt;DateTimeOffset?&gt;</c>, and that one character is FR-117. Present always means a
/// real instant and absent always means "leave the row's value": there is no third case, so
/// reopening a closed shift is not something this contract can express. The wire agrees — the
/// converter refuses a <c>null</c> for a field that cannot hold one — so
/// <c>{"endedAt": null}</c> is a 422 rather than a reopened shift.
/// </para>
/// <para>
/// The driver is deliberately absent. A shift is one driver's stretch of time; moving it to another
/// driver is deleting it and writing another, and a movable driver would let one edit slip a row
/// past the open-shift index by carrying it to a driver who already has one.
/// </para>
/// </summary>
/// <param name="StartedAt">The new start, or absent to leave it alone.</param>
/// <param name="EndedAt">The new end, or absent to leave it alone. Never a clear.</param>
public sealed record UpdateShiftCommand(
    Optional<DateTimeOffset> StartedAt,
    Optional<DateTimeOffset> EndedAt)
{
    /// <summary>
    /// The command with every absent field filled in from the stored row: the state the shift will
    /// hold afterwards.
    /// <para>
    /// AD-23 makes this the thing the validator reads. Validating the payload instead would accept
    /// <c>{"startedAt": T+9h}</c> against a shift that ended at <c>T+8h</c> — a window that runs
    /// backwards, assembled out of two individually harmless halves (FR-111).
    /// </para>
    /// </summary>
    /// <param name="shift">The stored row the edit will be applied to.</param>
    internal ShiftState MergedOnto(Shift shift)
    {
        ArgumentNullException.ThrowIfNull(shift);

        // EndedAt is Optional<DateTimeOffset>, so `HasValue` means a real instant and absence means
        // the row's own value. Written out rather than as `EndedAt.Or(...)` because the types
        // differ: the command carries a non-nullable instant and the row carries a nullable one.
        return new ShiftState(
            StartedAt.Or(shift.StartedAt),
            EndedAt.HasValue ? EndedAt.Value : shift.EndedAt);
    }
}

/// <summary>
/// The state a shift will hold once an edit is applied — AD-23's merged shape, and the only thing
/// <see cref="ShiftStateValidator"/> is ever handed.
/// </summary>
/// <param name="StartedAt">The start the row will carry.</param>
/// <param name="EndedAt">The end the row will carry, or null while it is open.</param>
public sealed record ShiftState(DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

/// <summary>
/// NFR-27's paging over a shift list, expressed as a request rather than as loose integers.
/// <para>
/// <see cref="DriverId"/> is a filter dispatch may set, never the authorization decision. A driver's
/// narrowing comes from <c>IAccessGuard.RequireShiftScope</c> and overrides whatever the query
/// asked for, so a driver cannot widen their own listing by naming somebody else (AD-3).
/// </para>
/// </summary>
/// <param name="Offset">Rows to skip. Zero is the first page.</param>
/// <param name="Limit">Rows to take, at most <see cref="ListShiftsQueryValidator.MaximumLimit"/>.</param>
/// <param name="DriverId">The driver row id to narrow to, or null for every driver.</param>
public sealed record ListShiftsQuery(int Offset, int Limit, int? DriverId);

/// <summary>
/// A shift as a screen reads one (FR-112, FR-113): the row, the driver it belongs to by name, and
/// whether it is still running.
/// </summary>
/// <param name="Id">The shift row's id.</param>
/// <param name="DriverId">The driver on duty.</param>
/// <param name="DriverName">
/// That driver's name, read from the Identity account rather than joined — a driver's name lives on
/// their account and nowhere else, which is how the Shifts capability names a driver without
/// depending on the Drivers one.
/// </param>
/// <param name="StartedAt">When the shift started, at offset zero (AD-13).</param>
/// <param name="EndedAt">When it ended, or null while it is open.</param>
/// <param name="IsOpen">
/// Whether the driver is still on duty. Derived from <paramref name="EndedAt"/> rather than stored:
/// there is no status column, and a second answer is a second thing that can disagree (FR-117).
/// </param>
public sealed record ShiftSummary(
    int Id,
    DriverId DriverId,
    string DriverName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    bool IsOpen);
