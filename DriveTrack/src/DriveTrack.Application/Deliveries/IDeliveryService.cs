namespace DriveTrack.Application.Deliveries;

/// <summary>
/// The Deliveries capability (FR-14 to FR-27, FR-101 to FR-103): the only writer of the
/// <c>deliveries</c> table, and the only reader other capabilities go through (AD-24).
/// <para>
/// Two list methods rather than one with a role branch inside it. AD-1 keeps domain branching out
/// of adapters, and AD-17 makes "a role that sees less gets a distinct DTO" a type rather than a
/// convention — so the dispatch list and the own-deliveries list answer different shapes and are
/// reached by different routes. A driver calling <see cref="ListAsync"/> is refused; a driver
/// calling <see cref="ListMineAsync"/> gets exactly their own rows, narrowed in SQL (AD-3).
/// </para>
/// </summary>
public interface IDeliveryService
{
    /// <summary>
    /// One page of every delivery, oldest first, with both parties named (FR-18). Dispatcher or
    /// admin; a driver or a client is refused rather than quietly narrowed.
    /// </summary>
    /// <exception cref="Common.ForbiddenException">The caller runs neither dispatch nor the system.</exception>
    /// <exception cref="Common.ValidationException">The paging parameters are out of range (NFR-27).</exception>
    Task<IReadOnlyList<DeliverySummary>> ListAsync(
        ListDeliveriesQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// One page of the caller's own deliveries (FR-25, FR-27), carrying no counterparty identity at
    /// all. The narrowing comes from <c>IAccessGuard.RequireScope</c> and reaches the query, so
    /// paging stays correct for a caller whose rows are not the first hundred.
    /// </summary>
    /// <exception cref="Common.ForbiddenException">The caller has no session, or no row scope.</exception>
    /// <exception cref="Common.ValidationException">The paging parameters are out of range.</exception>
    Task<IReadOnlyList<AssignedDeliverySummary>> ListMineAsync(
        ListDeliveriesQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// One delivery, as dispatch reads it. Dispatcher or admin: a driver or a client reads their
    /// own rows through <see cref="ListMineAsync"/>, which cannot carry a counterparty's name.
    /// </summary>
    /// <exception cref="Common.NotFoundException">No delivery has that id.</exception>
    /// <exception cref="Common.ForbiddenException">The caller runs neither dispatch nor the system.</exception>
    Task<DeliverySummary> GetAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a delivery (FR-14 to FR-17). It starts <c>Pending</c> and is timestamped from the
    /// injected clock (AD-13).
    /// </summary>
    /// <exception cref="Common.ValidationException">The command describes a delivery that may not exist.</exception>
    /// <exception cref="Common.NotFoundException">The named driver or client has no row (FR-29).</exception>
    /// <exception cref="Common.DomainRuleException">The assigned driver's vehicle cannot carry it (FR-103).</exception>
    Task<DeliverySummary> CreateAsync(
        CreateDeliveryCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a delivery the caller asked for themselves (FR-89, FR-90, FR-91).
    /// <para>
    /// <see cref="CreateAsync"/>'s sibling rather than an overload of it, because the two differ in
    /// who the row is attached to and in who may call them. The requester is the guard's answer,
    /// never a field of the command, so a client cannot ask on another client's behalf; the driver
    /// and the status are absent from the command entirely, so FR-90's "the client sets neither" is
    /// a shape rather than a rule someone has to enforce.
    /// </para>
    /// <para>
    /// Answers <see cref="AssignedDeliverySummary"/> rather than <see cref="DeliverySummary"/>, and
    /// that is AD-17 rather than economy: the row a client just created is a row they read on
    /// <c>/my-deliveries</c>, and a payload with a <c>Driver</c> field on it would be a
    /// counterparty's name one mapping mistake away from a client's screen (FR-27).
    /// </para>
    /// <para>
    /// Dispatch needs nothing new for the result. The row lands in <see cref="ListAsync"/> with its
    /// client named and no driver, and <see cref="UpdateAsync"/> assigns one with the vehicle
    /// capacity re-checked (FR-103) — the same edit path a dispatcher's own delivery takes.
    /// </para>
    /// </summary>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous, or presents a still-valid token for a client row that has since
    /// been deleted (<c>AUTH_UNAUTHENTICATED</c>, 401); or is a driver, is a client whose claims
    /// carry no client row id, or composes for somebody else — a dispatcher or an administrator,
    /// who open a delivery through <see cref="CreateAsync"/> where the client is an explicit field
    /// (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    /// <exception cref="Common.ValidationException">The command describes a delivery that may not exist.</exception>
    Task<AssignedDeliverySummary> RequestAsync(
        RequestDeliveryCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Changes a delivery (FR-22, FR-23). Absent fields are left alone and a present null clears
    /// what may be cleared (AD-23); every rule is judged against the merged state.
    /// </summary>
    /// <exception cref="Common.NotFoundException">No delivery has that id, or a named party has no row.</exception>
    /// <exception cref="Common.ValidationException">The merged state is one the row may not hold.</exception>
    /// <exception cref="Common.DomainRuleException">The merged state breaks the capacity invariant.</exception>
    Task<DeliverySummary> UpdateAsync(
        int id,
        UpdateDeliveryCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a delivery and, through the database's own cascade, its timeline, proof, review and
    /// notification attempts (FR-24, DR-9). Admin only — a dispatcher is refused.
    /// </summary>
    /// <exception cref="Common.NotFoundException">No delivery has that id.</exception>
    /// <exception cref="Common.ForbiddenException">The caller is not an administrator.</exception>
    Task DeleteAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Advances a delivery's lifecycle and records the change (FR-30 to FR-34, FR-105, FR-106).
    /// <para>
    /// The system's only status write path, whichever role or adapter initiated it (AD-10). The
    /// timeline entry is written inside the same transaction as the change it records (AD-27), so a
    /// status that moved without a record — or a record of a move that did not happen — is not a
    /// state the database can hold.
    /// </para>
    /// <para>
    /// Answers the entry rather than the delivery, and that is AD-17 rather than economy: a driver
    /// advancing their own parcel must not be handed a payload with the client's name on it, and a
    /// type with no party field cannot carry one.
    /// </para>
    /// </summary>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous, is a driver this delivery is not assigned to, holds a role with no
    /// part in the lifecycle — a client, always (FR-90) — or presents a still-valid token for an
    /// account that has since been deleted (<c>AUTH_UNAUTHENTICATED</c>, 401).
    /// </exception>
    /// <exception cref="Common.NotFoundException">No delivery has that id.</exception>
    /// <exception cref="Common.ValidationException">
    /// No status was named, the status named is not one the system declares, or the note is longer
    /// than the column allows.
    /// </exception>
    /// <exception cref="Common.DomainRuleException">
    /// The delivery's current status does not lead to the one asked for
    /// (<c>DELIVERY_INVALID_STATUS_TRANSITION</c>, 409). The exception's own message names both
    /// statuses (FR-32); what reaches the caller is the one catalogue sentence the code maps to,
    /// because NFR-3 gives every code exactly one message and the envelope carries no other text.
    /// </exception>
    Task<TimelineEntryView> ChangeStatusAsync(
        int id,
        ChangeDeliveryStatusCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends a note to a delivery's timeline without changing anything else (FR-107).
    /// <para>
    /// Open to anyone who may see the delivery, which is precisely <c>IAccessGuard.RequireScope</c>
    /// — including the client, who may never change a status and may always say something about
    /// their own parcel.
    /// </para>
    /// </summary>
    /// <exception cref="Common.ForbiddenException">
    /// The caller has no session, has no row scope, or presents a still-valid token for an account
    /// that has since been deleted (<c>AUTH_UNAUTHENTICATED</c>, 401).
    /// </exception>
    /// <exception cref="Common.NotFoundException">
    /// No delivery has that id, or the caller's scope does not admit it — the same answer for both,
    /// so a client learns nothing about another client's delivery.
    /// </exception>
    /// <exception cref="Common.ValidationException">The note is blank or longer than the column allows.</exception>
    Task<TimelineEntryView> AddNoteAsync(
        int id,
        AddDeliveryNoteCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// A delivery's history, oldest first (FR-108).
    /// <para>
    /// Every viewer of the delivery gets the same entries; what differs is whether each one names
    /// its actor. A dispatcher or an admin sees the name, a driver or a client sees only the role
    /// (FR-27, FR-96, FR-105), decided in this service's mapping step against the current user and
    /// nowhere else (AD-17).
    /// </para>
    /// </summary>
    /// <exception cref="Common.ForbiddenException">The caller has no session, or no row scope.</exception>
    /// <exception cref="Common.NotFoundException">No delivery has that id, or the caller may not see it.</exception>
    Task<IReadOnlyList<TimelineEntryView>> ListTimelineAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// The places matching a typed address (FR-104), each with the point a map click would have
    /// produced.
    /// <para>
    /// This capability's rather than a new one's, because the search exists for one screen: FR-104
    /// is the delivery form's second way of setting a pickup or a dropoff, beside the map. A
    /// capability of its own would be a capability with one caller and one reason to exist, and the
    /// guard decision would have to be restated in it.
    /// </para>
    /// <para>
    /// Open to the roles that compose a delivery and closed to the one that does not — precisely
    /// <c>IAccessGuard.RequireDeliveryComposer</c>, the same question <see cref="RequestAsync"/>
    /// asks. FR-104 is unqualified about who sets a location by typing, and since story 7.4 a
    /// client's request form sets two of them, so a dispatcher-only reservation would refuse the
    /// second caller the requirement is about. A driver stays refused: they read the parcel they
    /// are carrying and compose nothing, so a public geocoder is still not reachable by every
    /// session that exists.
    /// </para>
    /// </summary>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401); or is a driver, or is a client
    /// whose claims carry no client row id, or holds a role with no part in composing a delivery
    /// (<c>AUTH_FORBIDDEN</c>, 403). The same two codes <see cref="RequestAsync"/> answers, because
    /// it is the same guard member deciding.
    /// </exception>
    /// <exception cref="Common.ValidationException">The query is blank or shorter than three characters.</exception>
    Task<IReadOnlyList<PlaceMatch>> SearchPlacesAsync(
        SearchPlacesQuery query,
        CancellationToken cancellationToken);
}
