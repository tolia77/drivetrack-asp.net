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
    /// <summary>
    /// How long a note may be.
    /// <para>
    /// Declared on the entity that owns the column, and read by the mapping that sets the column's
    /// width and by both validators that refuse an over-long note. Three copies of the figure, each
    /// commented "matching the column", is three chances for one of them to drift — and the drift
    /// is silent in the worst direction: a validator that allows more than the column holds turns a
    /// legal note into a truncation or a failed commit.
    /// </para>
    /// </summary>
    public const int NoteMaximumLength = 1000;

    /// <summary>
    /// The widest actor name the snapshot column holds.
    /// <para>
    /// Declared here for the same reason as <see cref="NoteMaximumLength"/>, and it is not academic:
    /// a first and last name are bounded at a hundred characters each, so the two of them joined by
    /// a space reach a hundred and one over this bound. Nothing refuses that name at registration,
    /// and a truncation error at commit is SQLSTATE 22001, which the constraint translator
    /// deliberately passes through — so the write path reads this figure and fits the name to it
    /// rather than letting a legal account produce a 500 the first time it moves a delivery.
    /// </para>
    /// </summary>
    public const int ActorDisplayNameMaximumLength = 200;

    /// <summary>
    /// Whether a note is one the column can hold, measured the way the service stores it.
    /// <para>
    /// Trimmed, and declared beside the bound it measures against rather than on either validator:
    /// the standalone-note path and the note riding on a status change apply exactly the same rule,
    /// and neither of them should have to depend on the other's validator to say so. The service
    /// writes the trimmed text, so measuring the raw string would refuse a note that fits, for
    /// whitespace that is never stored.
    /// </para>
    /// </summary>
    /// <param name="note">The note as it arrived, or null.</param>
    /// <returns>True when the note is absent or fits.</returns>
    public static bool NoteFits(string? note) =>
        note is null || note.Trim().Length <= NoteMaximumLength;

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
        UserRole actorRole,
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

    /// <summary>
    /// The actor's role, snapshotted at write time and never updated.
    /// <para>
    /// The enum rather than free text (AD-21): the column still holds the member name, so nothing
    /// about the schema changes, but a reader gets a closed set instead of a string to parse. That
    /// is what lets the view expose a role a screen can label without a lookup table of its own,
    /// and what stops "dispatcher" and "Dispatcher" ever both appearing in the same table.
    /// </para>
    /// </summary>
    public UserRole ActorRole { get; }

    /// <summary>Status before the change, or null for a note-only entry (FR-107).</summary>
    public DeliveryStatus? PreviousStatus { get; }

    /// <summary>Status after the change, or null for a note-only entry (FR-107).</summary>
    public DeliveryStatus? NewStatus { get; }

    /// <summary>Free text explaining the entry, or null.</summary>
    public string? Note { get; }

    /// <summary>When it happened, at offset zero, from an injected TimeProvider (AD-13).</summary>
    public DateTimeOffset OccurredAt { get; }
}
