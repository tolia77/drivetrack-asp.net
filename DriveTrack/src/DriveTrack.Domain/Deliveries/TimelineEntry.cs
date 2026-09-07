using DriveTrack.Domain.Identity;

namespace DriveTrack.Domain.Deliveries;

/// <summary>
/// One appended line in a delivery's history (FR-105 to FR-108).
/// <para>
/// Every property is get-only and set through the constructor: AD-27 forbids a mutable
/// property as much as it forbids an update statement, because "append-only" that any caller
/// can edit in memory is not append-only. A correction is a new entry (FR-108), and an entry
/// may carry only a note with no status change (FR-107).
/// </para>
/// <para>
/// The actor is snapshotted. <see cref="ActorUserId"/> is nullable and set null when the user
/// is deleted, while <see cref="ActorDisplayName"/> and <see cref="ActorRole"/> keep what was
/// true at the moment of writing (AD-20). A restrict there would make every user who has ever
/// acted undeletable — the exact "deleting a driver raises a database error" defect this
/// rewrite exists to fix — and a cascade would erase the history instead.
/// </para>
/// </summary>
public sealed class TimelineEntry
{
    /// <summary>Creates an entry. There is no other way to populate one.</summary>
    /// <param name="deliveryId">The delivery this entry belongs to.</param>
    /// <param name="actorUserId">Who acted, or null once that user is deleted.</param>
    /// <param name="actorDisplayName">The actor's name as it read when the entry was written.</param>
    /// <param name="actorRole">The actor's role as it read when the entry was written.</param>
    /// <param name="previousStatus">Status before the change, or null when nothing changed.</param>
    /// <param name="newStatus">Status after the change, or null when nothing changed.</param>
    /// <param name="note">Free text, or null.</param>
    /// <param name="occurredAt">When it happened, at offset zero (AD-13).</param>
    public TimelineEntry(
        int deliveryId,
        UserId? actorUserId,
        string actorDisplayName,
        string actorRole,
        DeliveryStatus? previousStatus,
        DeliveryStatus? newStatus,
        string? note,
        DateTimeOffset occurredAt)
    {
        DeliveryId = deliveryId;
        ActorUserId = actorUserId;
        ActorDisplayName = actorDisplayName;
        ActorRole = actorRole;
        PreviousStatus = previousStatus;
        NewStatus = newStatus;
        Note = note;
        OccurredAt = occurredAt;
    }

    /// <summary>Surrogate key, assigned by the database.</summary>
    public int Id { get; }

    /// <summary>The delivery this entry belongs to. The entry dies with it (DR-9).</summary>
    public int DeliveryId { get; }

    /// <summary>Who acted, or null once that user is deleted (AD-20 set-null).</summary>
    public UserId? ActorUserId { get; }

    /// <summary>The actor's display name, snapshotted at write time and never updated.</summary>
    public string ActorDisplayName { get; }

    /// <summary>The actor's role, snapshotted at write time and never updated.</summary>
    public string ActorRole { get; }

    /// <summary>Status before the change, or null for a note-only entry (FR-107).</summary>
    public DeliveryStatus? PreviousStatus { get; }

    /// <summary>Status after the change, or null for a note-only entry (FR-107).</summary>
    public DeliveryStatus? NewStatus { get; }

    /// <summary>Free text explaining the entry, or null.</summary>
    public string? Note { get; }

    /// <summary>When it happened, at offset zero, from an injected TimeProvider (AD-13).</summary>
    public DateTimeOffset OccurredAt { get; }
}
