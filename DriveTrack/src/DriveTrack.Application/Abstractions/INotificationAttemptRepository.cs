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
    /// One page of attempts, newest first (FR-28).
    /// <para>
    /// FR-28's remediation is "a row an administrator can read", and a row nothing can read is a
    /// log line with a table behind it. Paged like every other list that pages (NFR-27), and this
    /// is the table that most needed it: it grows by a row per status change per delivery and
    /// nothing prunes it, so an unbounded read is the whole history materialised every time the
    /// screen is opened.
    /// </para>
    /// <para>
    /// The two administration rosters — <see cref="IClientRepository.ListAsync"/> and
    /// <see cref="IDriverRepository.ListAsync"/> — are still unpaged, and that is not an oversight
    /// this should follow: they are bounded by headcount, which is a number somebody hires. This
    /// table is bounded by nothing.
    /// </para>
    /// </summary>
    /// <param name="offset">Rows to skip. Zero is the first page.</param>
    /// <param name="limit">Rows to take.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<NotificationAttempt>> ListAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Stages a new attempt for the next commit.</summary>
    void Add(NotificationAttempt attempt);
}
