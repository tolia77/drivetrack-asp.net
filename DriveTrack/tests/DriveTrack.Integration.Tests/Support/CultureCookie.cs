using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// The culture cookie, spelled the way a browser spells it back to the server.
/// <para>
/// Built through <see cref="CookieRequestCultureProvider"/>'s own name and formatter rather than
/// written out as a string, because the two ends have to agree and only one of them is ours: a
/// framework change to the cookie's name or to the <c>c=..|uc=..</c> shape would otherwise leave a
/// test happily asserting against a value the provider no longer reads, and the product's language
/// switch would be broken with the suite green.
/// </para>
/// </summary>
internal static class CultureCookie
{
    /// <summary>
    /// The cookie's name. Program.cs names it rather than taking the framework's
    /// <c>.AspNetCore.Culture</c>, so the two spellings have to agree - and the switch test asserts
    /// this one against the header the host actually writes, which is what makes the duplication
    /// safe rather than merely small.
    /// </summary>
    public const string Name = "drivetrack.culture";

    /// <summary>
    /// A <c>Cookie:</c> header value carrying the given culture.
    /// <para>
    /// The value is percent-encoded because that is what <c>Response.Cookies.Append</c> does on the
    /// way out and what <c>Request.Cookies</c> undoes on the way in; sent raw, the <c>=</c> inside
    /// <c>c=uk-UA</c> would be read as the end of the cookie's own value.
    /// </para>
    /// </summary>
    /// <param name="culture">The culture name, for example <c>en-US</c>.</param>
    public static string Header(string culture) =>
        $"{Name}={Uri.EscapeDataString(Value(culture))}";

    /// <summary>The cookie's value alone: what <c>MakeCookieValue</c> writes.</summary>
    /// <param name="culture">The culture name.</param>
    public static string Value(string culture) =>
        CookieRequestCultureProvider.MakeCookieValue(
            new RequestCulture(CultureInfo.GetCultureInfo(culture)));
}
