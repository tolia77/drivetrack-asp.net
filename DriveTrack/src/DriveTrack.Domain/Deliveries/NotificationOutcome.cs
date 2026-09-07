namespace DriveTrack.Domain.Deliveries;

/// <summary>
/// How a notification attempt ended. Persisted as the member name (AD-21).
/// </summary>
public enum NotificationOutcome
{
    /// <summary>The channel accepted the message.</summary>
    Sent,

    /// <summary>The channel rejected it or was unreachable; the reason is on the attempt.</summary>
    Failed,
}
