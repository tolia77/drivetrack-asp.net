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

    /// <summary>Where the delivery is in its lifecycle (FR-30).</summary>
    public DeliveryStatus Status { get; set; }

    /// <summary>When the delivery was created, at offset zero, from an injected TimeProvider (AD-13).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The single proof captured at hand-over, or null until then (FR-119).</summary>
    public ProofOfDelivery? ProofOfDelivery { get; set; }

    /// <summary>The single review the client left, or null (DR-6, FR-62).</summary>
    public Review? Review { get; set; }

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
