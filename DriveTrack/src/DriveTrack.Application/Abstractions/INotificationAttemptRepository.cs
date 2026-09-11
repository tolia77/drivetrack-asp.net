using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Abstractions;

/// <summary>
/// Persistence for <see cref="NotificationAttempt"/>. No <c>Remove</c>: an attempt is a record
/// of what happened, removed only by the cascade from its delivery.
/// </summary>
public interface INotificationAttemptRepository
{
    /// <summary>Loads an attempt, or null when there is none with that id.</summary>
    Task<NotificationAttempt?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Every attempt, newest first (FR-28).
    /// <para>
    /// FR-28's remediation is "a row an administrator can read", and a row nothing can read is a
    /// log line with a table behind it. Unpaginated for the reason the two administration rosters
    /// are: NFR-27 preserves the paging that already existed rather than inventing more.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<NotificationAttempt>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Stages a new attempt for the next commit.</summary>
    void Add(NotificationAttempt attempt);
}
