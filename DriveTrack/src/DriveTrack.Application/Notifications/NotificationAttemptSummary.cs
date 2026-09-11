using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Notifications;

/// <summary>
/// One notification attempt as an administrator reads it (FR-28).
/// <para>
/// A DTO rather than the entity, for AD-17's reason: the mapping step is where the disclosure
/// decision lives, and an entity handed to an adapter is an entity whose every future field is
/// disclosed by default. The delivery is named by its number rather than by a navigation, because
/// <c>NotificationAttempt</c> deliberately has none — an attempt points at its delivery and the
/// delivery knows nothing of its attempts.
/// </para>
/// </summary>
/// <param name="Id">The attempt row's id.</param>
/// <param name="DeliveryId">The delivery the notification was about.</param>
/// <param name="Kind">What the notification was about.</param>
/// <param name="Recipient">The address it was sent to.</param>
/// <param name="AttemptedAt">When it was attempted, at offset zero (AD-13).</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Error">Why it failed, or null when it did not.</param>
public sealed record NotificationAttemptSummary(
    int Id,
    int DeliveryId,
    NotificationKind Kind,
    string Recipient,
    DateTimeOffset AttemptedAt,
    NotificationOutcome Outcome,
    string? Error);
