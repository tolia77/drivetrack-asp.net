using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Shifts;

/// <summary>
/// The Shifts capability (FR-109 to FR-117): the only writer of the <c>shifts</c> table, and the
/// owner of the answer "who is on duty".
/// <para>
/// AD-24 in both directions. It never reaches <c>unitOfWork.Drivers</c> or
/// <c>unitOfWork.Deliveries</c>; the only table outside its own that it reads is the Identity
/// account roster, and only to put a name beside a driver row id — the same door
/// <c>DeliveryService</c> opens for the parties it names. Symmetrically, the Drivers capability
/// learns who is on duty through <see cref="ListOnDutyDriverIdsAsync"/> and never through
/// <c>unitOfWork.Shifts</c>.
/// </para>
/// <para>
/// That asymmetry — ids out, names read from accounts — is what keeps the two capabilities
/// acyclic. <c>DriverService</c> injects this service for FR-116's flag; if this one asked
/// <c>IDriverService</c> for names in return, the container would have a constructor cycle it
/// cannot resolve.
/// </para>
/// <para>
/// Every method takes an authorization decision through <see cref="Authorization.IAccessGuard"/>,
/// inline in its own body: nothing here is in <see cref="Authorization.PublicEntryPoints"/>.
/// </para>
/// <para>
/// There is no shift-to-delivery reference anywhere in this capability (DR-13). A shift records a
/// stretch of time a driver was on duty and nothing about what they carried during it.
/// </para>
/// </summary>
public interface IShiftService
{
    /// <summary>
    /// One page of shifts, newest first (FR-112, FR-113).
    /// <para>
    /// Whose shifts is the guard's answer, not the query's: dispatch reads every driver's and may
    /// narrow with <see cref="ListShiftsQuery.DriverId"/>, a driver reads their own and the
    /// narrowing is applied whatever they asked for. A client is refused outright (FR-115).
    /// </para>
    /// </summary>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401), or is a client, or is a driver
    /// whose claims carry no driver row id (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    /// <exception cref="Common.ValidationException">The paging parameters are out of range (NFR-27).</exception>
    Task<IReadOnlyList<ShiftSummary>> ListAsync(
        ListShiftsQuery query,
        CancellationToken cancellationToken);

    /// <summary>One shift. A driver may read their own and nobody else's.</summary>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (401), or may not reach this shift — answered before its existence is
    /// disclosed (403).
    /// </exception>
    /// <exception cref="Common.NotFoundException">No shift has that id.</exception>
    Task<ShiftSummary> GetAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a driver on duty (FR-109, FR-110). The start is the injected clock's instant, never the
    /// caller's (AD-13).
    /// </summary>
    /// <exception cref="Common.ForbiddenException">The caller may not act on this driver's shifts.</exception>
    /// <exception cref="Common.NotFoundException">No driver has that id.</exception>
    /// <exception cref="Common.ConflictException">
    /// The driver already has an open shift (<c>SHIFT_ALREADY_OPEN</c>, 409) — or, when two requests
    /// race, <c>PERSISTENCE_UNIQUE_VIOLATION</c> from <c>IX_Shifts_DriverId_Open</c>, which is the
    /// same 409 (NFR-2, AD-20).
    /// </exception>
    Task<ShiftSummary> StartAsync(StartShiftCommand command, CancellationToken cancellationToken);

    /// <summary>Takes a driver off duty (FR-109). The end is the injected clock's instant.</summary>
    /// <exception cref="Common.ForbiddenException">The caller may not act on this driver's shifts.</exception>
    /// <exception cref="Common.ConflictException">
    /// The driver has no open shift (<c>SHIFT_NOT_OPEN</c>, 409).
    /// </exception>
    Task<ShiftSummary> EndAsync(EndShiftCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Records a shift that already happened (FR-113), window and all.
    /// </summary>
    /// <exception cref="Common.ForbiddenException">The caller may not act on this driver's shifts.</exception>
    /// <exception cref="Common.NotFoundException">No driver has that id.</exception>
    /// <exception cref="Common.ValidationException">The window runs backwards (FR-111).</exception>
    /// <exception cref="Common.ConflictException">
    /// The shift would be left open beside another open one (<c>SHIFT_ALREADY_OPEN</c>).
    /// </exception>
    Task<ShiftSummary> CreateAsync(CreateShiftCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Corrects a shift (FR-114). Absent fields are left alone and the rules are judged against the
    /// merged state (AD-23); <c>EndedAt</c> can never be cleared, so a closed shift cannot be
    /// reopened (FR-117).
    /// </summary>
    /// <exception cref="Common.ForbiddenException">The caller may not change this shift.</exception>
    /// <exception cref="Common.NotFoundException">No shift has that id.</exception>
    /// <exception cref="Common.ValidationException">The merged window runs backwards.</exception>
    Task<ShiftSummary> UpdateAsync(
        int id,
        UpdateShiftCommand command,
        CancellationToken cancellationToken);

    /// <summary>Removes a shift (FR-114). Its driver, or dispatch.</summary>
    /// <exception cref="Common.ForbiddenException">The caller may not change this shift.</exception>
    /// <exception cref="Common.NotFoundException">No shift has that id.</exception>
    Task DeleteAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// The drivers on duty right now (FR-116), as row ids and nothing else.
    /// <para>
    /// The port AD-24 gives the Drivers capability: it owns the driver, this capability owns the
    /// shifts on-duty is derived from, and neither reaches into the other's table. Ids rather than
    /// names because the alternative closes a constructor cycle — see the note on this interface.
    /// </para>
    /// <para>
    /// Answered whole rather than per driver: a roster of forty asks once.
    /// </para>
    /// </summary>
    /// <exception cref="Common.ForbiddenException">The caller runs neither dispatch nor the system.</exception>
    Task<IReadOnlyList<DriverId>> ListOnDutyDriverIdsAsync(CancellationToken cancellationToken);
}
