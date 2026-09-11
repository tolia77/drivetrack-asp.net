using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Chat;

/// <summary>
/// The whole of chat, as one capability (FR-68 to FR-76, AD-15, AD-16).
/// <para>
/// Every read and every write of a message goes through here, and every method takes its own
/// authorization decision through <see cref="Authorization.IAccessGuard.RequireChatParticipant"/>.
/// The hub is a transport: it asks this service whether a caller may join a group and adds them only
/// if it says yes. Nothing else in the system holds a path to a message.
/// </para>
/// <para>
/// A conversation is addressed by <see cref="DriverId"/> — the driver row, never a user id (AD-22).
/// That is the correction this capability exists to make: the original stored a message against a
/// nullable recipient user and read it back by another key, so a delivered message could never be
/// retrieved.
/// </para>
/// <para>
/// A dispatcher reaches every conversation and the roster; a driver reaches exactly their own
/// conversation and no roster; a client and an administrator reach nothing at all.
/// </para>
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Every driver's conversation, named and in the order the driver capability lists them
    /// (FR-68). Dispatchers only — a driver has one conversation and needs no list of them.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.ForbiddenException">The caller is not a dispatcher.</exception>
    Task<IReadOnlyList<ChatThreadSummary>> ListThreadsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One driver's conversation, oldest message first (FR-71, FR-72).
    /// </summary>
    /// <param name="driverId">The driver row the conversation is keyed on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is not a participant in that conversation.
    /// </exception>
    /// <exception cref="Common.NotFoundException">
    /// No driver holds that id (<c>CHAT_THREAD_NOT_FOUND</c>).
    /// </exception>
    Task<ChatThread> GetThreadAsync(DriverId driverId, CancellationToken cancellationToken);

    /// <summary>
    /// Appends a message to a driver's conversation and returns the line that was written (FR-70).
    /// <para>
    /// The returned message is what the caller broadcasts: it carries the database-assigned id and
    /// the instant the injected clock gave it, so every participant — the sender included — sees the
    /// row that was actually stored rather than an optimistic echo of what was typed (FR-73).
    /// </para>
    /// </summary>
    /// <param name="command">The conversation and the text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is not a participant in that conversation.
    /// </exception>
    /// <exception cref="Common.NotFoundException">
    /// No driver holds that id (<c>CHAT_THREAD_NOT_FOUND</c>).
    /// </exception>
    /// <exception cref="Common.ValidationException">
    /// The text is empty once trimmed, or longer than the column holds.
    /// </exception>
    Task<ChatMessage> SendAsync(SendMessageCommand command, CancellationToken cancellationToken);
}
