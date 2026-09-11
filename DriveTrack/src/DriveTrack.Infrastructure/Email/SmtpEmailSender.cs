using System.Net;
using System.Net.Mail;
using DriveTrack.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace DriveTrack.Infrastructure.Email;

/// <summary>
/// <see cref="IEmailSender"/> over the BCL's own mail client.
/// <para>
/// <c>System.Net.Mail</c> rather than a mail library, because the product takes no dependency it
/// does not need and this transport needs nothing a third party would add: one message, one
/// recipient, plain text, no attachments and no queueing — the queue is
/// <c>DeliverySideEffectQueue</c>'s, one layer up.
/// </para>
/// <para>
/// A failure is thrown rather than reported. Recording the attempt belongs to
/// <c>DeliverySideEffectRunner</c>, which is the only place that has a unit of work to write the
/// row into and the only place that knows which delivery the notice was about (FR-28).
/// </para>
/// </summary>
internal sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            // Thrown rather than returned quietly, and it is the right answer for the one case it
            // covers: with no relay configured nothing was sent, and a caller that believed
            // otherwise would record a notification as delivered that never left the building. The
            // runner turns this into a Failed attempt saying exactly that, which is FR-28's point.
            throw new InvalidOperationException(
                $"No mail relay is configured. Set the '{SmtpOptions.SectionName}__Host' "
                    + "environment variable (see .env.example) before expecting a notification to "
                    + "be delivered.");
        }

        // A client per message. The type is not safe for concurrent sends, and a shared one would
        // make two notifications leaving at once a race rather than two messages.
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            // The property is named for SSL and means "negotiate TLS", which for a submission port
            // is STARTTLS. One switch, spelled the way the configuration spells it.
            EnableSsl = _options.UseStartTls,
        };

        if (!string.IsNullOrWhiteSpace(_options.User))
        {
            // Only when there is an account to present. A development relay accepts anonymous
            // submission, and handing it empty credentials is a failed AUTH rather than no AUTH.
            client.Credentials = new NetworkCredential(_options.User, _options.Password);
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = message.Subject,
            Body = message.Body,

            // Plain text. The body is composed from a resx template with no markup in it, and
            // declaring HTML would make a stray angle bracket in an address a rendering bug.
            IsBodyHtml = false,
        };

        mail.To.Add(message.Recipient);

        await client.SendMailAsync(mail, cancellationToken);
    }
}
