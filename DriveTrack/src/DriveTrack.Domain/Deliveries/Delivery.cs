using DriveTrack.Domain.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;

namespace DriveTrack.Domain.Deliveries;

/// <summary>
/// A parcel to move from one point to another — the system's central record.
/// </summary>
public sealed class Delivery
{
    /// <summary>Surrogate key.</summary>
    public int Id { get; set; }

    /// <summary>The requesting client, or null once that client is deleted (FR-47).</summary>
    public ClientId? ClientId { get; set; }

    /// <summary>The assigned driver, or null when unassigned or once that driver is deleted (FR-16, FR-39).</summary>
    public DriverId? DriverId { get; set; }

    /// <summary>Where the parcel is collected. Embedded in this row, not a foreign row (AD-11).</summary>
    public required Location PickupLocation { get; set; }

    /// <summary>Where the parcel is delivered. Embedded in this row, not a foreign row (AD-11).</summary>
    public required Location DropoffLocation { get; set; }

    /// <summary>What is being moved.</summary>
    public required string PackageDetails { get; set; }

    /// <summary>Weight of the parcel. Must be positive — a check constraint, not an <c>if</c> (FR-102).</summary>
    public decimal PackageWeightKg { get; set; }

    /// <summary>Free-text instructions, or null.</summary>
    public string? DeliveryNotes { get; set; }

    /// <summary>
    /// Earliest acceptable delivery time, or null. Either bound may stand alone; when both are
    /// present the earliest must precede the latest, enforced by a check constraint (FR-100).
    /// </summary>
    public DateTimeOffset? WindowEarliestAt { get; set; }

    /// <inheritdoc cref="WindowEarliestAt" />
    public DateTimeOffset? WindowLatestAt { get; set; }

    /// <summary>
    /// Where the delivery is in its lifecycle (FR-30). Read-only from outside: the only writer is
    /// <see cref="TryChangeStatus"/>, which is what makes FR-33 - nothing is created in a completed
    /// status, and nothing jumps the lifecycle - structural rather than a check somebody remembers.
    /// <para>
    /// The initializer is the whole of "a delivery is only ever born Pending": a create path has no
    /// way to say otherwise, so the command carries no status and none can be smuggled in (AD-25).
    /// EF sets this through the backing field when it materializes a row, which is the one write
    /// that is not a transition.
    /// </para>
    /// </summary>
    public DeliveryStatus Status { get; private set; } = DeliveryStatus.Pending;

    /// <summary>When the delivery was created, at offset zero, from an injected TimeProvider (AD-13).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The single proof captured at hand-over, or null until then (FR-119).</summary>
    public ProofOfDelivery? ProofOfDelivery { get; set; }

    /// <summary>The single review the client left, or null (DR-6, FR-62).</summary>
    public Review? Review { get; set; }

    /// <summary>
    /// The statuses a delivery in <paramref name="from"/> may legally move to (FR-31, FR-32).
    /// <para>
    /// AD-10's single declaration. Every other part of the system - the service that writes a
    /// change, the screen that offers the buttons - reads the lifecycle from here rather than
    /// restating it, because a second copy of a transition table is a second answer waiting to
    /// disagree with the first.
    /// </para>
    /// <para>
    /// <see cref="DeliveryStatus.Delivered"/> is terminal and answers an empty list; a status is
    /// never in its own list, so re-sending the status a delivery already holds is refused rather
    /// than treated as a no-op that writes a timeline entry saying nothing happened.
    /// </para>
    /// </summary>
    /// <param name="from">The status the delivery holds now.</param>
    /// <returns>The legal next statuses, which may be empty. Never null.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="from"/> is not a declared status. Total over the enum on purpose: a fifth
    /// status added without a decision about where it leads fails loudly here rather than becoming
    /// a silent dead end.
    /// </exception>
    public static IReadOnlyList<DeliveryStatus> NextStatuses(DeliveryStatus from) => from switch
    {
        DeliveryStatus.Pending => [DeliveryStatus.InTransit],
        DeliveryStatus.InTransit => [DeliveryStatus.Delivered, DeliveryStatus.Failed],

        // FR-31: a failed attempt is retried by putting the parcel back on the road.
        DeliveryStatus.Failed => [DeliveryStatus.InTransit],

        // Terminal. A correction after this is a timeline note, not a status (FR-108).
        DeliveryStatus.Delivered => [],

        _ => throw new ArgumentOutOfRangeException(
            nameof(from),
            from,
            "No lifecycle transition is declared for this status."),
    };

    /// <summary>
    /// Moves the delivery to <paramref name="next"/> when the lifecycle allows it (FR-32, FR-33).
    /// <para>
    /// The only mutator of <see cref="Status"/>, and it answers a bool rather than throwing because
    /// this assembly has no exception type and no reference to the one that does. Inventing a domain
    /// exception here only to catch and re-wrap it in the single caller would add a type and a
    /// <c>catch</c> without adding a rule - so the declaration stays here and the throw stays at the
    /// one write path, where it can name both statuses.
    /// </para>
    /// </summary>
    /// <param name="next">The status being asked for.</param>
    /// <returns>True when the move was legal and made; false when nothing changed.</returns>
    public bool TryChangeStatus(DeliveryStatus next)
    {
        if (!NextStatuses(Status).Contains(next))
        {
            return false;
        }

        Status = next;

        return true;
    }

    // Deliberately no TimelineEntries or NotificationAttempts collection.
    //
    // A collection navigation is a mutable path into an append-only table -
    // delivery.TimelineEntries.Clear() is exactly the mutation AD-27 forbids. It would also
    // break the append-only guard: EF client-cascades tracked dependents by issuing its own
    // DELETE statements before deleting the principal, and those arrive at trigger depth 1,
    // where the guard raises. Without the navigation the database cascade runs inside the
    // referential-integrity trigger instead, at depth 2, which the guard permits.
    // Reads go through ITimelineEntryRepository.ListForDeliveryAsync (FR-108).
    // A NotificationAttempt is written after the transaction commits (AD-12), so it never
    // belongs to this entity's tracked graph either.
}
