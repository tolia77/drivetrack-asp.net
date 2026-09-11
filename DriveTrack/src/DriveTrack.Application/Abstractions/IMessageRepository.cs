using DriveTrack.Domain.Chat;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Abstractions;

/// <summary>
/// Persistence for <see cref="Message"/>. No <c>Remove</c>: a message is removed only by the
/// cascade from its driver thread (FR-39).
/// </summary>
public interface IMessageRepository
{
    /// <summary>Loads a message, or null when there is none with that id.</summary>
    Task<Message?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// One driver's whole conversation, oldest first (FR-71, FR-72).
    /// <para>
    /// Read back from the same <c>DriverId</c> it was written against, which is the correction this
    /// capability exists to make: the original stored a message against a nullable recipient and
    /// read it back by another key, so a delivered message could never be retrieved.
    /// </para>
    /// <para>
    /// Unpaged, because FR-71 asks for the full prior history. The <c>(driver_id, sent_at)</c> index
    /// declared on the table is the one that serves this read.
    /// </para>
    /// </summary>
    /// <param name="driverId">The driver row the conversation is keyed on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Message>> ListForDriverAsync(
        DriverId driverId,
        CancellationToken cancellationToken);

    /// <summary>Stages a new message for the next commit.</summary>
    void Add(Message message);
}
