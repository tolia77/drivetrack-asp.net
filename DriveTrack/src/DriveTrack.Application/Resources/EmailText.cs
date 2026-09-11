using System.Globalization;
using System.Resources;
using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Resources;

/// <summary>
/// The words an outbound message is built from (NFR-14), read from <c>EmailText.resx</c> through a
/// BCL <see cref="ResourceManager"/>.
/// <para>
/// A <see cref="ResourceManager"/> rather than <c>IStringLocalizer</c> because that interface is an
/// ASP.NET abstraction and this is a class library AD-1 keeps free of one. The resx is embedded by
/// the SDK's default item globbing, so the manifest name is the root namespace plus the folder.
/// </para>
/// </summary>
internal static class EmailText
{
    private static readonly ResourceManager Resources =
        new("DriveTrack.Application.Resources.EmailText", typeof(EmailText).Assembly);

    /// <summary>The subject of a status-change notice. Takes the delivery's number.</summary>
    public static string StatusChangeSubject => Value(nameof(StatusChangeSubject));

    /// <summary>The body of one. Takes the number, the previous state and the new one.</summary>
    public static string StatusChangeBody => Value(nameof(StatusChangeBody));

    /// <summary>
    /// A status as a client reads it, worded exactly as the screens word it — the equality
    /// <c>LocalizationTests</c> pins.
    /// </summary>
    /// <param name="status">The status to name.</param>
    public static string Label(DeliveryStatus status) => Value("Status" + status);

    /// <summary>
    /// The entry, or its key when the catalogue has none.
    /// <para>
    /// The key rather than an exception, for the reason <c>IStringLocalizer</c> does the same: a
    /// missing entry must not be the reason a delivery's notification attempt is never recorded.
    /// The forward and reverse catalogue tests are what make the miss a build failure instead.
    /// </para>
    /// </summary>
    private static string Value(string key) =>
        Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
