using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using FluentValidation;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// AD-3's pipeline over the product's central record: load resource → guard → validate → act →
/// commit → map.
/// <para>
/// AD-2: every method calls <see cref="IAccessGuard"/> through the interface, inline in its own
/// body. Folding the call into a private helper would read better and would not count — the
/// coverage gate walks each public method's IL and does not follow a call it makes.
/// </para>
/// <para>
/// "Dispatcher or admin" is written <c>RequireRole(UserRole.Dispatcher)</c> throughout, because
/// AD-4 makes admin satisfy every check by rule inside the guard. <see cref="DeleteAsync"/> is the
/// exception and says <c>RequireRole(UserRole.Admin)</c> outright: FR-24 reserves deletion to an
/// administrator, so a dispatcher is refused there and nowhere else.
/// </para>
/// <para>
/// <c>ValidatorExtensions.ValidateAndThrowAsync</c> is called in static form on purpose. This
/// namespace is the one <c>ValidatorExtensions</c>' own documentation names: a file here that
/// imported only <c>FluentValidation</c> would bind the identically-named extension method to
/// FluentValidation's own, whose exception carries no <see cref="ErrorCode"/> and leaves as a 500
/// where 422 was meant. The static call cannot bind anywhere else.
/// </para>
/// </summary>
public sealed class DeliveryService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAccessGuard accessGuard,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IValidator<ListDeliveriesQuery> listValidator,
    IValidator<CreateDeliveryCommand> createValidator,
    IValidator<UpdateDeliveryCommand> updateValidator,
    IValidator<ChangeDeliveryStatusCommand> statusValidator,
    IValidator<AddDeliveryNoteCommand> noteValidator) : IDeliveryService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DeliverySummary>> ListAsync(
        ListDeliveriesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        accessGuard.RequireRole(UserRole.Dispatcher);

        // AD-3's "the predicate comes from the guard", satisfied rather than optimized away. On
        // this route it can only ever be unrestricted - the role check above has already narrowed
        // the caller to the two roles the guard answers unrestricted for - but the scope still
        // travels from the guard into the query, so the day a fourth role is given the dispatch
        // list there is no second place deciding what it may see.
        var scope = accessGuard.RequireScope();

        // NFR-27: refused before any query is built.
        await ValidatorExtensions.ValidateAndThrowAsync(listValidator, query, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var deliveries = await unitOfWork.Deliveries.ListAsync(
            scope,
            query.Offset,
            query.Limit,
            cancellationToken);

        var parties = await PartiesAsync(unitOfWork, deliveries, cancellationToken);

        // Nothing was written, so the scope is disposed without a commit and the empty transaction
        // rolls back. That is the ordinary read path, not an omission.
        return [.. deliveries.Select(delivery => Summary(delivery, parties))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssignedDeliverySummary>> ListMineAsync(
        ListDeliveriesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No role check: this route is for whoever the scope narrows to, which is every signed-in
        // caller who has one. An admin or a dispatcher gets the unrestricted scope and therefore
        // every row - which is exactly why the screen at /my-deliveries is a driver's and a
        // client's, and why the dispatch list is a separate route.
        var scope = accessGuard.RequireScope();

        await ValidatorExtensions.ValidateAndThrowAsync(listValidator, query, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var deliveries = await unitOfWork.Deliveries.ListAsync(
            scope,
            query.Offset,
            query.Limit,
            cancellationToken);

        // No party lookup at all, and nothing to strip: the type has no field a counterparty's
        // name could be written into (AD-17).
        return [.. deliveries.Select(Assigned)];
    }

    /// <inheritdoc />
    public async Task<DeliverySummary> GetAsync(int id, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // AD-3: load, then guard. The row is read before the decision so a future rule can be about
        // the row; nothing about it is disclosed unless the guard passes.
        var delivery = await unitOfWork.Deliveries.GetByIdAsync(id, cancellationToken);

        accessGuard.RequireRole(UserRole.Dispatcher);

        if (delivery is null)
        {
            throw NotFound(id);
        }

        var parties = await PartiesAsync(unitOfWork, [delivery], cancellationToken);

        return Summary(delivery, parties);
    }

    /// <inheritdoc />
    public async Task<DeliverySummary> CreateAsync(
        CreateDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A create loads nothing, so the guard is the first step rather than the second.
        accessGuard.RequireRole(UserRole.Dispatcher);

        await ValidatorExtensions.ValidateAndThrowAsync(createValidator, command, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // FR-29: a reference to a row that is not there is refused by name, so a dispatcher who
        // picked a driver deleted a moment ago is told which half of the request was wrong.
        var driverId = await ResolveDriverAsync(unitOfWork, command.DriverId, cancellationToken);
        var clientId = await ResolveClientAsync(unitOfWork, command.ClientId, cancellationToken);

        await DeliveryCapacity.EnsureDeliveryFitsAsync(
            unitOfWork,
            driverId,
            command.PackageWeightKg,
            cancellationToken);

        var delivery = new Delivery
        {
            ClientId = clientId,
            DriverId = driverId,

            // The validator has proved both are present with coordinates in range; the nullable
            // annotations exist because the wire can send anything and the command has to carry it
            // as far as here.
            PickupLocation = Point(command.Pickup!),
            DropoffLocation = Point(command.Dropoff!),
            PackageDetails = command.PackageDetails!.Trim(),
            PackageWeightKg = command.PackageWeightKg,
            DeliveryNotes = Blank(command.DeliveryNotes),
            WindowEarliestAt = Utc(command.WindowEarliestAt),
            WindowLatestAt = Utc(command.WindowLatestAt),

            // No Status here, and its absence is the point: FR-30's "a delivery is only ever born
            // Pending" is the entity's own initializer now, and the setter is private, so this path
            // could not say otherwise even if a command grew a status field (AD-10, AD-25).

            // AD-13: the injected clock, at offset zero. DateTimeOffset.UtcNow here would fail
            // PersistenceContractTests and, more to the point, make FR-19's overdue rule untestable.
            CreatedAt = timeProvider.GetUtcNow(),
        };

        unitOfWork.Deliveries.Add(delivery);

        // Read before the commit, inside the transaction that is about to close: the names do not
        // depend on the write, and a query issued after CommitAsync would run outside the scope
        // AD-5 gave this operation.
        var parties = await PartiesAsync(unitOfWork, [delivery], cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        // Built from the entity the commit populated - the database assigned its id - rather than
        // re-read: a second read would only ask the database to confirm what this scope wrote.
        return Summary(delivery, parties);
    }

    /// <inheritdoc />
    public async Task<DeliverySummary> UpdateAsync(
        int id,
        UpdateDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var delivery = await unitOfWork.Deliveries.GetByIdAsync(id, cancellationToken);

        accessGuard.RequireRole(UserRole.Dispatcher);

        if (delivery is null)
        {
            throw NotFound(id);
        }

        // AD-23: the validator reads the state the row will hold, not the payload. An update that
        // mentions only the weight must not be refused for a window it never sent, and the window
        // rule has to see the bound already stored.
        var merged = command.MergedOnto(delivery);

        await ValidatorExtensions.ValidateAndThrowAsync(updateValidator, merged, cancellationToken);

        var driverId = await ResolveDriverAsync(unitOfWork, merged.DriverId.Value, cancellationToken);
        var clientId = await ResolveClientAsync(unitOfWork, merged.ClientId.Value, cancellationToken);

        // FR-103 against the merged state: raising the weight and reassigning the driver are the
        // same question asked of two different halves of the pair.
        await DeliveryCapacity.EnsureDeliveryFitsAsync(
            unitOfWork,
            driverId,
            merged.PackageWeightKg.Value,
            cancellationToken);

        delivery.DriverId = driverId;
        delivery.ClientId = clientId;

        // DR-11: coordinates are authoritative and the address is their cache, so a point that
        // moved loses the address that described where it used to be. Story 5.2's reverse geocoder
        // refills it; leaving a stale address beside new coordinates is the one thing DR-11 forbids.
        delivery.PickupLocation = Moved(delivery.PickupLocation, merged.Pickup.Value!);
        delivery.DropoffLocation = Moved(delivery.DropoffLocation, merged.Dropoff.Value!);

        delivery.PackageDetails = merged.PackageDetails.Value!.Trim();
        delivery.PackageWeightKg = merged.PackageWeightKg.Value;
        delivery.DeliveryNotes = Blank(merged.DeliveryNotes.Value);
        delivery.WindowEarliestAt = Utc(merged.WindowEarliestAt.Value);
        delivery.WindowLatestAt = Utc(merged.WindowLatestAt.Value);

        // Before the commit, for the reason the create path states: the read belongs inside the
        // operation's own transaction.
        var parties = await PartiesAsync(unitOfWork, [delivery], cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return Summary(delivery, parties);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // Loaded without its timeline, and that is not an accident of the repository: EF
        // client-cascades tracked dependents by issuing its own DELETE at trigger depth 1, where
        // the append-only guard raises. Nothing tracked means the database's own cascade runs
        // inside the referential-integrity trigger at depth 2, which the guard permits (AD-27).
        var delivery = await unitOfWork.Deliveries.GetByIdAsync(id, cancellationToken);

        // FR-24: deletion is an administrator's. This is the one method here that names Admin.
        accessGuard.RequireRole(UserRole.Admin);

        if (delivery is null)
        {
            throw NotFound(id);
        }

        unitOfWork.Deliveries.Remove(delivery);

        await unitOfWork.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TimelineEntryView> ChangeStatusAsync(
        int id,
        ChangeDeliveryStatusCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // AD-3: load, then guard, because this decision is about the row - specifically about which
        // driver it names. Tracked, unlike the two read paths below: this one writes.
        var delivery = await unitOfWork.Deliveries.GetByIdAsync(id, cancellationToken);

        // One guard call covering every role (AD-2, FR-34), inline in this method's own body so the
        // coverage gate can see it. It runs on a null row deliberately: a driver asking about a
        // delivery that does not exist is refused before being told it does not exist.
        accessGuard.RequireAssignedDriver(delivery?.DriverId);

        if (delivery is null)
        {
            throw NotFound(id);
        }

        await ValidatorExtensions.ValidateAndThrowAsync(statusValidator, command, cancellationToken);

        // Epic 6's precondition on the Delivered transition (FR-120) attaches here, before the
        // move: proof exists, or the command itself carries an explanatory note. One site, and it
        // must not be role-branched when it lands.
        var previous = delivery.Status;

        // The validator has proved a status was named and that it is one the enum declares; the
        // nullable annotation exists because the wire can omit it and the command has to carry that
        // absence as far as the refusal.
        var requested = command.Status!.Value;

        if (!delivery.TryChangeStatus(requested))
        {
            // FR-32: the message names the status the delivery is in and the one that was asked
            // for, so a caller can see which half of their assumption was wrong. AD-10 makes this a
            // domain-rule failure and therefore a 409, never a 422.
            throw new DomainRuleException(
                ErrorCode.DELIVERY_INVALID_STATUS_TRANSITION,
                "A delivery in status " + previous + " cannot move to " + requested + ".");
        }

        var entry = new TimelineEntry(
            delivery.Id,
            currentUser.UserId,
            await ActorNameAsync(unitOfWork, cancellationToken),
            currentUser.Role,
            previous,
            requested,

            // FR-106: the same entry carries both the change and whatever was said about it.
            Blank(command.Note),
            timeProvider.GetUtcNow());

        // AD-27: staged, not saved - so the status row and its record land in the one transaction
        // this scope commits, and a refusal above leaves neither.
        unitOfWork.TimelineEntries.Add(entry);

        await unitOfWork.CommitAsync(cancellationToken);

        // Story 5.2's client email attaches here, after the commit and outside the transaction
        // (AD-12, FR-28). A send that fails is recorded as a notification attempt and never fails
        // the status write, which is why it cannot be moved above this line.
        return View(entry);
    }

    /// <inheritdoc />
    public async Task<TimelineEntryView> AddNoteAsync(
        int id,
        AddDeliveryNoteCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // FR-107's audience is "anyone who can view the delivery", which is what the scope answers.
        // A fifth guard member would be a second way of saying AD-3.
        var scope = accessGuard.RequireScope();

        await ValidatorExtensions.ValidateAndThrowAsync(noteValidator, command, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // Narrowed in the query, so a client asking about another client's delivery gets the same
        // answer as one asking about a delivery that never existed.
        if (await unitOfWork.Deliveries.FindVisibleAsync(id, scope, cancellationToken) is null)
        {
            throw NotFound(id);
        }

        var entry = new TimelineEntry(
            id,
            currentUser.UserId,
            await ActorNameAsync(unitOfWork, cancellationToken),
            currentUser.Role,

            // Both null: FR-107's entry records that something was said, not that something moved.
            previousStatus: null,
            newStatus: null,
            command.Note!.Trim(),
            timeProvider.GetUtcNow());

        unitOfWork.TimelineEntries.Add(entry);

        await unitOfWork.CommitAsync(cancellationToken);

        return View(entry);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimelineEntryView>> ListTimelineAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var scope = accessGuard.RequireScope();

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        if (await unitOfWork.Deliveries.FindVisibleAsync(id, scope, cancellationToken) is null)
        {
            throw NotFound(id);
        }

        var entries = await unitOfWork.TimelineEntries.ListForDeliveryAsync(id, cancellationToken);

        // The mapping step AD-17 puts the disclosure decision in. Nothing was written, so the scope
        // is disposed without a commit, as on every read path here.
        return [.. entries.Select(View)];
    }

    /// <summary>
    /// The acting user's display name for the snapshot AD-20 asks for.
    /// <para>
    /// Read from the account because <see cref="ICurrentUser"/> carries identities and a role and
    /// no name — deliberately, since a name in a token is a name that goes stale. Snapshotting it
    /// onto the entry is what makes the history readable after the account is deleted.
    /// </para>
    /// </summary>
    /// <remarks>
    /// A missing account is reachable and is refused rather than papered over. A bearer token
    /// carries no revocation, so an administrator who deletes a dispatcher leaves that dispatcher's
    /// token working until it expires; their next write would insert an entry whose
    /// <c>actor_user_id</c> names no row, which is SQLSTATE 23503 — a foreign-key violation the
    /// constraint translator deliberately passes through, so it would leave as a 500. It is
    /// <c>AUTH_UNAUTHENTICATED</c> rather than <c>AUTH_FORBIDDEN</c> because the account behind the
    /// credentials is gone: the caller's next move is to sign in again, and FR-13's session-expiry
    /// flow branches on exactly this code.
    /// </remarks>
    /// <exception cref="ForbiddenException">The account the token names no longer exists.</exception>
    private async Task<string> ActorNameAsync(
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        var account = await unitOfWork.Users.GetByIdAsync(currentUser.UserId, cancellationToken)
            ?? throw new ForbiddenException(
                ErrorCode.AUTH_UNAUTHENTICATED,
                "The account behind these credentials no longer exists, so the actor of a timeline "
                    + "entry cannot be recorded.");

        var name = DisplayName(account);

        // Fitted to the column rather than refused. A first and a last name are bounded at a hundred
        // characters each, so a legal account can produce a hundred and one characters more than the
        // snapshot holds; the alternative is SQLSTATE 22001 at commit, which the constraint
        // translator passes through and which would leave a legal status change as a 500. The
        // snapshot is a display name, and a display name that has been cut is still readable
        // history - a failed write is not.
        return name.Length <= TimelineEntry.ActorDisplayNameMaximumLength
            ? name
            : name[..TimelineEntry.ActorDisplayNameMaximumLength];
    }

    /// <summary>
    /// AD-17's final step: the entry as this caller may read it.
    /// <para>
    /// Dispatch sees who acted; a driver and a client see only what role they held. That is FR-27
    /// and FR-96 reaching the timeline — a client must not learn the assigned driver's name through
    /// the history any more than through the delivery — and it is decided here, against the current
    /// user, rather than in a component that has no way to know who is looking.
    /// </para>
    /// </summary>
    private TimelineEntryView View(TimelineEntry entry) =>
        new(
            entry.Id,
            currentUser.Role is UserRole.Admin or UserRole.Dispatcher
                ? entry.ActorDisplayName
                : null,
            entry.ActorRole,
            entry.PreviousStatus,
            entry.NewStatus,
            entry.Note,
            entry.OccurredAt);

    /// <summary>
    /// The driver a command named, as a typed id, or null when it named none.
    /// </summary>
    /// <exception cref="NotFoundException">
    /// The id names no driver (<c>DELIVERY_DRIVER_NOT_FOUND</c>). A code of this capability's own
    /// rather than <c>COMMON_NOT_FOUND</c>, because the resource the caller addressed does exist -
    /// it is the row they pointed at from inside it that does not, and they can act on that.
    /// </exception>
    private static async Task<DriverId?> ResolveDriverAsync(
        IUnitOfWork unitOfWork,
        int? driverId,
        CancellationToken cancellationToken)
    {
        if (driverId is not { } id)
        {
            return null;
        }

        var driver = await unitOfWork.Drivers.GetByIdAsync(new DriverId(id), cancellationToken)
            ?? throw new NotFoundException(
                ErrorCode.DELIVERY_DRIVER_NOT_FOUND,
                "No driver exists with id " + id.ToString(CultureInfo.InvariantCulture) + ".");

        return driver.Id;
    }

    /// <inheritdoc cref="ResolveDriverAsync" />
    private static async Task<ClientId?> ResolveClientAsync(
        IUnitOfWork unitOfWork,
        int? clientId,
        CancellationToken cancellationToken)
    {
        if (clientId is not { } id)
        {
            return null;
        }

        var client = await unitOfWork.Clients.GetByIdAsync(new ClientId(id), cancellationToken)
            ?? throw new NotFoundException(
                ErrorCode.DELIVERY_CLIENT_NOT_FOUND,
                "No client exists with id " + id.ToString(CultureInfo.InvariantCulture) + ".");

        return client.Id;
    }

    private static NotFoundException NotFound(int id) =>
        new(
            ErrorCode.COMMON_NOT_FOUND,
            "No delivery exists with id " + id.ToString(CultureInfo.InvariantCulture) + ".");

    /// <summary>A new location at the given coordinates, with no cached address (DR-11).</summary>
    private static Location Point(LocationInput input) =>
        new(input.Latitude!.Value, input.Longitude!.Value, Address: null, AddressResolvedAt: null);

    /// <summary>
    /// The stored location moved to new coordinates, keeping its address only when it did not
    /// actually move. Comparing rather than always resetting is what stops an edit to the weight
    /// throwing away an address story 5.2 resolved, since the merge fills an absent location in
    /// from the row and hands back the coordinates it already had.
    /// </summary>
    private static Location Moved(Location current, LocationInput input) =>
        current.Latitude == input.Latitude && current.Longitude == input.Longitude
            ? current
            : Point(input);

    /// <summary>
    /// The same instant at offset zero (AD-13).
    /// <para>
    /// A window bound arrives from wherever the caller is: a browser's datetime input carries the
    /// local offset, and a REST client may send any offset at all. The column is
    /// <c>timestamp with time zone</c>, which Npgsql will only write a <c>DateTimeOffset</c> to when
    /// its offset is zero — so an unnormalized bound is not a formatting wrinkle, it is an
    /// exception at the commit that would leave as a 500 for a request the caller got right.
    /// Converting rather than rejecting is correct because the offset carries no information the
    /// instant does not: local time is a presentation concern, and this is storage.
    /// </para>
    /// </summary>
    private static DateTimeOffset? Utc(DateTimeOffset? value) => value?.ToUniversalTime();

    /// <summary>
    /// Trimmed, and null when nothing but whitespace is left. A notes column holding a single space
    /// is a value that renders as absent and filters as present, which is worse than either.
    /// </summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// FR-19, derived and stored nowhere: the latest bound has passed and the parcel has not been
    /// delivered. A delivered parcel is never overdue however long the window has been shut - it
    /// arrived, and the record of when is the timeline's, not a flag's.
    /// </summary>
    private static bool IsOverdue(Delivery delivery, DateTimeOffset now) =>
        delivery.WindowLatestAt is { } latest
        && latest < now
        && delivery.Status != DeliveryStatus.Delivered;

    private static LocationView View(Location location) =>
        new(new MapLocation(location.Latitude, location.Longitude), location.Address);

    private DeliverySummary Summary(Delivery delivery, DeliveryParties parties) =>
        new(
            delivery.Id,
            parties.Driver(delivery.DriverId),
            parties.Client(delivery.ClientId),
            View(delivery.PickupLocation),
            View(delivery.DropoffLocation),
            delivery.PackageDetails,
            delivery.PackageWeightKg,
            delivery.DeliveryNotes,
            delivery.WindowEarliestAt,
            delivery.WindowLatestAt,
            delivery.Status,
            delivery.CreatedAt,
            IsOverdue(delivery, timeProvider.GetUtcNow()));

    private AssignedDeliverySummary Assigned(Delivery delivery) =>
        new(
            delivery.Id,
            View(delivery.PickupLocation),
            View(delivery.DropoffLocation),
            delivery.PackageDetails,
            delivery.PackageWeightKg,
            delivery.DeliveryNotes,
            delivery.WindowEarliestAt,
            delivery.WindowLatestAt,
            delivery.Status,
            delivery.CreatedAt,
            IsOverdue(delivery, timeProvider.GetUtcNow()));

    /// <summary>
    /// The names of every party the page actually refers to, read once for the whole page rather
    /// than once per row.
    /// <para>
    /// <c>Delivery</c> has no navigation to <c>Driver</c> or <c>Client</c> - the configuration
    /// declares the foreign keys and no navigations - and a client's name is not on the client row
    /// at all: it lives on the Identity account, reachable only through
    /// <see cref="IUserAccountRepository"/>. So the summary resolves its own parties, and it does
    /// it with two roster reads instead of two per row.
    /// </para>
    /// <para>
    /// A roster is only read when some row on the page actually names that kind of party, so the
    /// ordinary create - a delivery with neither - costs no lookup at all.
    /// </para>
    /// </summary>
    private static async Task<DeliveryParties> PartiesAsync(
        IUnitOfWork unitOfWork,
        IReadOnlyCollection<Delivery> deliveries,
        CancellationToken cancellationToken)
    {
        var drivers = new Dictionary<DriverId, string>();
        var clients = new Dictionary<ClientId, string>();

        if (deliveries.Any(delivery => delivery.DriverId is not null))
        {
            foreach (var account in await unitOfWork.Users.ListByRoleAsync(
                         UserRole.Driver,
                         cancellationToken))
            {
                if (account.DriverId is { } driverId)
                {
                    drivers[driverId] = DisplayName(account);
                }
            }
        }

        if (deliveries.Any(delivery => delivery.ClientId is not null))
        {
            foreach (var account in await unitOfWork.Users.ListByRoleAsync(
                         UserRole.Client,
                         cancellationToken))
            {
                if (account.ClientId is { } clientId)
                {
                    clients[clientId] = DisplayName(account);
                }
            }
        }

        return new DeliveryParties(drivers, clients);
    }

    private static string DisplayName(UserAccount account) =>
        (account.FirstName + " " + account.LastName).Trim();

    /// <summary>
    /// The page's parties, keyed by the row id a delivery refers to them by.
    /// </summary>
    private sealed class DeliveryParties(
        IReadOnlyDictionary<DriverId, string> drivers,
        IReadOnlyDictionary<ClientId, string> clients)
    {
        /// <summary>
        /// The assigned driver, or null when the delivery has none.
        /// </summary>
        /// <remarks>
        /// A missing name yields an empty one rather than a null party, deliberately: null has to
        /// keep meaning "unassigned" and nothing else (AD-17). The foreign key makes the miss
        /// unreachable - a delivery naming a driver names a row that exists, and every driver row
        /// has an account - so this is the answer to a state the schema forbids, not a fallback
        /// anything depends on.
        /// </remarks>
        public DeliveryParty? Driver(DriverId? driverId) =>
            driverId is { } id
                ? new DeliveryParty(
                    id.Value,
                    drivers.TryGetValue(id, out var name) ? name : string.Empty)
                : null;

        /// <inheritdoc cref="Driver" />
        public DeliveryParty? Client(ClientId? clientId) =>
            clientId is { } id
                ? new DeliveryParty(
                    id.Value,
                    clients.TryGetValue(id, out var name) ? name : string.Empty)
                : null;
    }
}
