namespace DriveTrack.Domain.Deliveries;

/// <summary>
/// A record of one outbound notification and how it ended (FR-28).
/// <para>
/// The original swallowed a failed notification silently. FR-28's remediation is a row an admin
/// can read, not a log line — so failure gets a named entity with a home, written after the
/// transaction commits (AD-12) so an SMTP timeout can never roll back a legitimate write.
/// </para>
/// </summary>
public sealed class NotificationAttempt
{
    /// <summary>Surrogate key.</summary>
    public int Id { get; set; }

    /// <summary>The delivery the notification was about. The attempt dies with it.</summary>
    public int DeliveryId { get; set; }

    /// <summary>What the notification was about.</summary>
    public NotificationKind Kind { get; set; }

    /// <summary>The address the notification was sent to.</summary>
    public required string Recipient { get; set; }

    /// <summary>When the attempt was made, at offset zero (AD-13).</summary>
    public DateTimeOffset AttemptedAt { get; set; }

    /// <summary>How it ended.</summary>
    public NotificationOutcome Outcome { get; set; }

    /// <summary>Why it failed, or null when it did not.</summary>
    public string? Error { get; set; }
}
