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
}
