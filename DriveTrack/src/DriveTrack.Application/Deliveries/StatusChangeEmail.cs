using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Resources;
using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// FR-28's notice, composed here rather than in the transport.
/// <para>
/// <see cref="IEmailSender"/> takes a written message precisely so that this — the only part a
/// client ever reads — stays in the layer that owns the product's vocabulary. An adapter that
/// composed its own subject line would be a second place NFR-14 applied, and the one place nobody
/// would look for it.
/// </para>
/// </summary>
internal static class StatusChangeEmail
{
    /// <summary>The notice that a delivery moved from one state to another.</summary>
    /// <param name="recipient">The client account's address.</param>
    /// <param name="deliveryId">The delivery's number, which is what a client can quote back.</param>
    /// <param name="previous">The state it held.</param>
    /// <param name="next">The state it holds now.</param>
    public static EmailMessage For(
        string recipient,
        int deliveryId,
        DeliveryStatus previous,
        DeliveryStatus next) =>
        new(
            recipient,

            // CurrentCulture, not InvariantCulture: the delivery's number is rendered for a reader,
            // and NFR-15 makes that a Ukrainian-formatted number rather than a machine's.
            string.Format(CultureInfo.CurrentCulture, EmailText.StatusChangeSubject, deliveryId),
            string.Format(
                CultureInfo.CurrentCulture,
                EmailText.StatusChangeBody,
                deliveryId,
                EmailText.Label(previous),
                EmailText.Label(next)));
}
