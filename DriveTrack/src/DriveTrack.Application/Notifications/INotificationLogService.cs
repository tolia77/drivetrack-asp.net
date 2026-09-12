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
    /// One page of attempts, newest first. Administrators only, decided by <c>IAccessGuard</c>
    /// inside the implementation (AD-2).
    /// </summary>
    /// <param name="query">The page asked for (NFR-27).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.ForbiddenException">The caller does not run the system; the log is an administrator's alone, and a dispatcher is refused it.</exception>
    /// <exception cref="Common.ValidationException">The paging parameters are out of range (NFR-27).</exception>
    Task<IReadOnlyList<NotificationAttemptSummary>> ListAsync(
        ListNotificationAttemptsQuery query,
        CancellationToken cancellationToken);
}
