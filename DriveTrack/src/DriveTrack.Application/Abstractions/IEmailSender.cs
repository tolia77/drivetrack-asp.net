namespace DriveTrack.Application.Abstractions;

/// <summary>
/// One outbound message, already worded.
/// </summary>
/// <param name="Recipient">The address to send to.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="Body">The plain-text body.</param>
public sealed record EmailMessage(string Recipient, string Subject, string Body);

/// <summary>
/// AD-12's outbound-mail port (FR-28). A dumb transport and nothing more.
/// <para>
/// The wording is the caller's: this takes a composed <see cref="EmailMessage"/> rather than a
/// delivery and a status, so no user-facing sentence lives in an adapter and NFR-14's catalogue
/// stays in the layer that owns it.
/// </para>
/// <para>
/// A failure is thrown, not swallowed. Recording an attempt is the caller's job — the row an
/// administrator reads is a <c>NotificationAttempt</c>, and a transport that quietly returned
/// false would make "sent" and "not sent" indistinguishable at the only place that can write one.
/// </para>
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends the message, throwing when it could not be handed over.</summary>
    /// <param name="message">The composed message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
