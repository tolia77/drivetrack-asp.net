using DriveTrack.Domain.Identity;

namespace DriveTrack.Domain.Chat;

/// <summary>
/// One line in the conversation about a driver.
/// <para>
/// The thread key is <see cref="DriverId"/> — the driver row, never a user id (AD-15). There is
/// no recipient column: the original's nullable receiver made "who is this message for" a
/// per-row guess, and it is the defect this entity exists to fix. A message belongs to a
/// driver's thread; <see cref="SenderUserId"/> only says who wrote it.
/// </para>
/// </summary>
public sealed class Message
{
    /// <summary>Surrogate key.</summary>
    public int Id { get; set; }

    /// <summary>The driver whose thread this is. The message dies with the driver (FR-39).</summary>
    public DriverId DriverId { get; set; }

    /// <summary>
    /// Who wrote it, or null once that user is deleted (AD-20 set-null). Restrict would make
    /// anyone who has ever posted undeletable, and cascade would delete half a conversation to
    /// remove one account - so the thread survives with an unattributed line.
    /// </summary>
    public UserId? SenderUserId { get; set; }

    /// <summary>The message body.</summary>
    public required string Text { get; set; }

    /// <summary>When it was sent, at offset zero (AD-13).</summary>
    public DateTimeOffset SentAt { get; set; }
}
