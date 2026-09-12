using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Shifts;
using FluentValidation;

namespace DriveTrack.Application.Shifts;

/// <summary>
/// AD-3's pipeline over a driver's duty log: open a scope → load → guard → not found → validate →
/// act → commit → map.
/// <para>
/// AD-2: every method calls <see cref="IAccessGuard"/> through the interface, inline in its own
/// body. Folding <c>RequireShiftOwner</c> into a private helper would read better and would not
/// count — <c>GuardCoverageTests</c> walks each public method's own IL and does not follow a call it
/// makes.
/// </para>
/// <para>
/// <b>What this capability reads that it does not own (AD-24).</b> Only
/// <c>unitOfWork.Users.ListByRoleAsync(UserRole.Driver, …)</c>, and only to put a name beside a
/// driver row id — the same door <c>DeliveryService</c> opens for the parties it names, and the
/// only place a driver's name lives at all. There is no <c>unitOfWork.Drivers</c> and no
/// <c>IDriverService</c> anywhere in this file, and there must not be: the Drivers capability
/// injects <see cref="IShiftService"/> for FR-116's flag, so a dependency back would be a
/// constructor cycle the container cannot resolve.
/// </para>
/// <para>
/// <b>Open is <c>EndedAt IS NULL</c>, and the index is the rule.</b> The friendly
/// <c>SHIFT_ALREADY_OPEN</c> below comes from a lookup, and a lookup is exactly what concurrency
/// defeats: two requests in flight both find nothing. <c>IX_Shifts_DriverId_Open</c> is what
/// actually enforces one open shift per driver, and the loser of a race leaves here as
/// <c>PERSISTENCE_UNIQUE_VIOLATION</c> — the same 409, through the one translation point in
/// Infrastructure (AD-20, NFR-2).
/// </para>
/// <para>
/// <c>ValidatorExtensions.ValidateAndThrowAsync</c> is called in static form, for the reason
/// <c>DeliveryService</c> states: a file in this namespace that imported only
/// <c>FluentValidation</c> would bind to that library's identically named extension, whose
/// exception carries no <see cref="ErrorCode"/> and leaves as a 500 where 422 was meant.
/// </para>
/// </summary>
public sealed class ShiftService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAccessGuard accessGuard,
    TimeProvider timeProvider,
    IValidator<CreateShiftCommand> createValidator,
    IValidator<ShiftState> stateValidator,
    IValidator<ListShiftsQuery> listValidator) : IShiftService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ShiftSummary>> ListAsync(
        ListShiftsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // AD-3: the narrowing comes from the guard, never from the payload. A driver gets their own
        // driver row id back and it overrides whatever the query asked for, so naming somebody else
        // in the query string narrows to the caller rather than widening to the other driver.
        var scope = accessGuard.RequireShiftScope();

        await ValidatorExtensions.ValidateAndThrowAsync(listValidator, query, cancellationToken);

        var driverId = scope ?? (query.DriverId is { } filter ? new DriverId(filter) : null);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var shifts = await unitOfWork.Shifts.ListAsync(
            driverId,
            query.Offset,
            query.Limit,
            cancellationToken);

        if (shifts.Count == 0)
        {
            // No roster read for a page with nothing on it. The names below are only ever asked for
            // once per page, never once per row, and an empty page needs the question asked zero
            // times rather than once.
            return [];
        }

        var names = await DriverNamesAsync(unitOfWork, cancellationToken);

        return [.. shifts.Select(shift => Summary(shift, Name(names, shift.DriverId)))];
    }

    /// <inheritdoc />
    public async Task<ShiftSummary> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // AD-3: load, then guard. The row is read before the decision because the decision is about
        // the row - whose shift it is - and nothing about it is disclosed unless the guard passes.
        var shift = await unitOfWork.Shifts.GetByIdAsync(id, cancellationToken);

        // A missing shift reaches the guard as a null that matches no driver, so a caller who may
        // not read it is refused before learning whether it exists.
        accessGuard.RequireShiftOwner(shift?.DriverId);

        if (shift is null)
        {
            throw NotFound(id);
        }

        var names = await DriverNamesAsync(unitOfWork, cancellationToken);

        // Nothing was written, so the scope is disposed without a commit and the empty transaction
        // rolls back - the ordinary read path, not an omission.
        return Summary(shift, Name(names, shift.DriverId));
    }

    /// <inheritdoc />
    public async Task<ShiftSummary> StartAsync(
        StartShiftCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A start loads nothing of its own, so the guard is the first step rather than the second.
        // The driver it is about is the payload's, which is safe because this is exactly the
        // question the member answers: a driver naming anybody but themselves is refused here.
        accessGuard.RequireShiftOwner(command.DriverId);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var name = await RequireDriverNameAsync(unitOfWork, command.DriverId, cancellationToken);

        // The friendly answer. The partial unique index behind it is the race backstop and surfaces
        // as PERSISTENCE_UNIQUE_VIOLATION, which is also a 409 (NFR-2).
        if (await unitOfWork.Shifts.FindOpenAsync(command.DriverId, cancellationToken) is not null)
        {
            throw AlreadyOpen(command.DriverId);
        }

        var shift = new Shift
        {
            DriverId = command.DriverId,

            // AD-13: the injected clock, at offset zero. DateTimeOffset.UtcNow here would fail
            // PersistenceContractTests and, more to the point, let a caller backdate going on duty.
            StartedAt = timeProvider.GetUtcNow(),
        };

        unitOfWork.Shifts.Add(shift);

        await unitOfWork.CommitAsync(cancellationToken);

        // Built from the entity the commit populated - the database assigned its id - rather than
        // re-read: a second read would only ask the database to confirm what this scope wrote.
        return Summary(shift, name);
    }

    /// <inheritdoc />
    public async Task<ShiftSummary> EndAsync(
        EndShiftCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        accessGuard.RequireShiftOwner(command.DriverId);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var name = await RequireDriverNameAsync(unitOfWork, command.DriverId, cancellationToken);

        var shift = await unitOfWork.Shifts.FindOpenAsync(command.DriverId, cancellationToken)
            ?? throw new ConflictException(
                ErrorCode.SHIFT_NOT_OPEN,
                "Driver " + command.DriverId.Value.ToString(CultureInfo.InvariantCulture)
                    + " has no open shift, so there is nothing to end.");

        var endedAt = timeProvider.GetUtcNow();

        // FR-111 on the one write path whose instant nobody typed. An open shift may be dated in the
        // future - CreateShiftCommandValidator has nothing to compare a null end against - so a
        // dispatcher can record a shift starting tomorrow and the driver can press off duty today,
        // which is exactly the backwards window the rule forbids. The same validator the edit path
        // uses judges it, so the refusal is one sentence rather than two.
        await ValidatorExtensions.ValidateAndThrowAsync(
            stateValidator,
            new ShiftState(shift.StartedAt, endedAt),
            cancellationToken);

        // The same row, closed. A new row would leave the driver with a stretch of time they were
        // never on duty for, and the index would refuse it anyway while the first stays open.
        shift.EndedAt = endedAt;

        await unitOfWork.CommitAsync(cancellationToken);

        return Summary(shift, name);
    }

    /// <inheritdoc />
    public async Task<ShiftSummary> CreateAsync(
        CreateShiftCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        accessGuard.RequireShiftOwner(command.DriverId);

        await ValidatorExtensions.ValidateAndThrowAsync(createValidator, command, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var name = await RequireDriverNameAsync(unitOfWork, command.DriverId, cancellationToken);

        // Only when the new row would itself be open. A closed window recorded after the fact is
        // never in conflict with an open shift, because the index only looks at open rows - so
        // refusing one would refuse the very correction FR-113 exists for.
        if (command.EndedAt is null
            && await unitOfWork.Shifts.FindOpenAsync(command.DriverId, cancellationToken) is not null)
        {
            throw AlreadyOpen(command.DriverId);
        }

        var shift = new Shift
        {
            DriverId = command.DriverId,
            StartedAt = command.StartedAt,
            EndedAt = command.EndedAt,
        };

        unitOfWork.Shifts.Add(shift);

        await unitOfWork.CommitAsync(cancellationToken);

        return Summary(shift, name);
    }

    /// <inheritdoc />
    public async Task<ShiftSummary> UpdateAsync(
        int id,
        UpdateShiftCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var shift = await unitOfWork.Shifts.GetByIdAsync(id, cancellationToken);

        accessGuard.RequireShiftOwner(shift?.DriverId);

        if (shift is null)
        {
            throw NotFound(id);
        }

        // AD-23: the validator reads the state the shift will hold, not the payload - so an edit
        // that moves only the start is judged on the window it actually produces (FR-111).
        var merged = command.MergedOnto(shift);

        await ValidatorExtensions.ValidateAndThrowAsync(stateValidator, merged, cancellationToken);

        shift.StartedAt = merged.StartedAt;

        // FR-117: the merge cannot produce a null where the row held an instant, because the
        // command's EndedAt is Optional<DateTimeOffset> - present means a real instant and absent
        // means the row's own value. There is no assignment here that can reopen a closed shift.
        shift.EndedAt = merged.EndedAt;

        var name = await RequireDriverNameAsync(unitOfWork, shift.DriverId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return Summary(shift, name);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var shift = await unitOfWork.Shifts.GetByIdAsync(id, cancellationToken);

        accessGuard.RequireShiftOwner(shift?.DriverId);

        if (shift is null)
        {
            throw NotFound(id);
        }

        unitOfWork.Shifts.Remove(shift);

        await unitOfWork.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DriverId>> ListOnDutyDriverIdsAsync(
        CancellationToken cancellationToken)
    {
        // Written first, before anything is read: a cross-capability feed has to take the same
        // decision whatever the answer turns out to be, or the guard becomes something a caller can
        // skip by asking at a moment when nobody is on duty.
        accessGuard.RequireRole(UserRole.Dispatcher);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        return await unitOfWork.Shifts.ListOpenDriverIdsAsync(cancellationToken);
    }

    private static NotFoundException NotFound(int id) =>
        new(
            ErrorCode.COMMON_NOT_FOUND,
            "No shift exists with id " + id.ToString(CultureInfo.InvariantCulture) + ".");

    private static ConflictException AlreadyOpen(DriverId driverId) =>
        new(
            ErrorCode.SHIFT_ALREADY_OPEN,
            "Driver " + driverId.Value.ToString(CultureInfo.InvariantCulture)
                + " already has an open shift.");

    /// <summary>
    /// Every driver's name, keyed by the driver row id a shift refers to them by.
    /// <para>
    /// AD-24: a driver's name lives on their Identity account and nowhere else, so this is the one
    /// door the capability opens outside its own table. Read once per call rather than once per
    /// row - the same rule <c>DeliveryService.PartiesAsync</c> follows, and the reason a page of a
    /// hundred shifts costs one roster read instead of a hundred.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<DriverId, string>> DriverNamesAsync(
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<DriverId, string>();

        foreach (var account in await unitOfWork.Users.ListByRoleAsync(
                     UserRole.Driver,
                     cancellationToken))
        {
            if (account.DriverId is { } driverId)
            {
                names[driverId] = (account.FirstName + " " + account.LastName).Trim();
            }
        }

        return names;
    }

    /// <summary>
    /// The named driver's display name, or a 404 when no driver row holds that id.
    /// <para>
    /// The lookup is not a second authorization decision and is not extra work: every write answers
    /// a <see cref="ShiftSummary"/>, which carries the driver's name, so the roster has to be read
    /// on this path anyway. Answering the miss rather than writing the row is what keeps a
    /// dispatcher's typo a 404 instead of a foreign-key violation surfacing as a 500 — AD-8
    /// deliberately leaves 23503 untranslated, because an unreachable constraint failure is a defect
    /// rather than a caller error.
    /// </para>
    /// <para>
    /// It runs after the guard, so a driver naming somebody else is refused before they can use the
    /// answer to learn which driver ids exist.
    /// </para>
    /// </summary>
    private static async Task<string> RequireDriverNameAsync(
        IUnitOfWork unitOfWork,
        DriverId driverId,
        CancellationToken cancellationToken)
    {
        var names = await DriverNamesAsync(unitOfWork, cancellationToken);

        return names.TryGetValue(driverId, out var name)
            ? name
            : throw new NotFoundException(
                ErrorCode.COMMON_NOT_FOUND,
                "No driver exists with id "
                    + driverId.Value.ToString(CultureInfo.InvariantCulture) + ".");
    }

    /// <summary>
    /// One driver's name off the page's roster.
    /// </summary>
    /// <remarks>
    /// A miss yields an empty name rather than a failure, deliberately: the foreign key makes it
    /// unreachable - a shift names a driver row that exists, and every driver row has an account -
    /// so this is the answer to a state the schema forbids, not a fallback anything depends on.
    /// </remarks>
    private static string Name(IReadOnlyDictionary<DriverId, string> names, DriverId driverId) =>
        names.TryGetValue(driverId, out var name) ? name : string.Empty;

    private static ShiftSummary Summary(Shift shift, string driverName) =>
        new(
            shift.Id,
            shift.DriverId,
            driverName,
            shift.StartedAt,
            shift.EndedAt,

            // Derived here and stored nowhere: there is no status column, and the one predicate the
            // index filters on is the one answer the whole system gives (FR-117).
            shift.EndedAt is null);
}
