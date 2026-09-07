namespace DriveTrack.Domain.Deliveries;

/// <summary>
/// What a notification attempt was about. Only one kind exists because FR-28 defines only one
/// notification — a status change, by email. A speculative second member would be dead weight (NFR-6).
/// </summary>
public enum NotificationKind
{
    /// <summary>The delivery's status changed.</summary>
    StatusChange,
}
