using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Chat;

/// <summary>
/// One line of a conversation, as a caller sees it (AD-17: the entity never crosses the boundary).
/// <para>
/// <see cref="SenderUserId"/> is a plain <see cref="int"/> rather than a <c>UserId</c> on purpose.
/// It exists so a viewer can recognise their own lines; nothing authorizes on it, and a typed
/// identity here would only be a second wire shape for a number the screen compares against the one
/// it already holds.
/// </para>
/// </summary>
/// <param name="Id">The message row's id, which is also its tie-break in the thread order.</param>
/// <param name="SenderUserId">Who wrote it, or null once that account has been deleted (AD-20).</param>
/// <param name="SenderName">
/// The sender's display name, or the empty string when the account is gone. Empty rather than a
/// sentence: the unattributed label is a rendered string and belongs in the localized catalogue,
/// not in a layer that has no culture (NFR-14).
/// </param>
/// <param name="SentAt">When it was sent, at offset zero (AD-13).</param>
/// <param name="Text">The message body, exactly as it was stored.</param>
public sealed record ChatMessage(
    int Id,
    int? SenderUserId,
    string SenderName,
    DateTimeOffset SentAt,
    string Text);

/// <summary>
/// One driver's whole conversation: who it is about, and every line of it oldest first (FR-71).
/// </summary>
/// <param name="DriverId">The driver row the thread is keyed on (AD-15, AD-22).</param>
/// <param name="DriverName">The driver's display name, for the thread header.</param>
/// <param name="Messages">Every message, ordered by <c>SentAt</c> then by id.</param>
public sealed record ChatThread(
    DriverId DriverId,
    string DriverName,
    IReadOnlyList<ChatMessage> Messages);

/// <summary>
/// One row of the dispatch desk's roster of conversations (FR-68): enough to name a thread and open
/// it, and deliberately no message count or preview — this story ships no unread counts.
/// </summary>
/// <param name="DriverId">The driver row the thread is keyed on.</param>
/// <param name="DriverName">The driver's display name, which the roster is ordered by.</param>
public sealed record ChatThreadSummary(DriverId DriverId, string DriverName);

/// <summary>
/// A message being sent into a driver's conversation (FR-70).
/// </summary>
/// <param name="DriverId">The thread it belongs to. Never a user id (AD-15, AD-22).</param>
/// <param name="Text">
/// The body. Nullable because a caller can leave a box empty, and an empty box is a refusal to
/// state rather than a value to guess at.
/// </param>
public sealed record SendMessageCommand(DriverId DriverId, string? Text);
