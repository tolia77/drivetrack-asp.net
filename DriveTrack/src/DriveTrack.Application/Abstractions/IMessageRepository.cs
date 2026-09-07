using DriveTrack.Domain.Chat;

namespace DriveTrack.Application.Abstractions;

/// <summary>
/// Persistence for <see cref="Message"/>. No <c>Remove</c>: a message is removed only by the
/// cascade from its driver thread (FR-39).
/// </summary>
public interface IMessageRepository
{
    /// <summary>Loads a message, or null when there is none with that id.</summary>
    Task<Message?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Stages a new message for the next commit.</summary>
    void Add(Message message);
}
