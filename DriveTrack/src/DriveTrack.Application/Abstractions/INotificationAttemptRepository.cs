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

    /// <summary>Stages a new attempt for the next commit.</summary>
    void Add(NotificationAttempt attempt);
}
