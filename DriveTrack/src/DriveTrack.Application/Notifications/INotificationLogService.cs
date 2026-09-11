namespace DriveTrack.Application.Notifications;

/// <summary>
/// FR-28's read side: the notification log an administrator opens.
/// <para>
/// The original swallowed a failed notification silently, and the remediation is a row somebody can
/// read. A table nothing can query is a log line with extra storage, so the row gets a capability of
/// its own — read-only, because an attempt is a record of what happened and nothing may edit it.
/// </para>
/// </summary>
public interface INotificationLogService
{
    /// <summary>
    /// Every attempt, newest first. Administrators only, decided by <c>IAccessGuard</c> inside the
    /// implementation (AD-2).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<NotificationAttemptSummary>> ListAsync(CancellationToken cancellationToken);
}
