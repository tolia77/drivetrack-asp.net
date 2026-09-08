using System.Text.RegularExpressions;
using DriveTrack.Web.Account;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// FR-79 / FR-80: the anonymous half and the forbidden half of an authorization failure are
/// different failures, and the split is a pure function so it can be asserted without a browser.
/// <para>
/// The bug this closes is a quiet one. The baseline sent every refused caller to <c>/sign-in</c>,
/// which tells a caller who is already signed in that they are not — so they sign in again,
/// succeed, and are refused again. The loop looks like a broken login rather than a missing
/// permission.
/// </para>
/// </summary>
public class AuthFailureRouteTests
{
    [Fact]
    public void An_authenticated_caller_who_is_refused_is_told_they_lack_access()
    {
        Assert.Equal("/access-denied", AuthFailureRoute.For(isAuthenticated: true));
    }

    [Fact]
    public void An_anonymous_caller_is_sent_to_sign_in()
    {
        Assert.Equal("/sign-in", AuthFailureRoute.For(isAuthenticated: false));
    }

    [Fact]
    public void The_two_answers_are_different_routes()
    {
        // Guards the pair above against being collapsed back into one answer: two assertions that
        // each pin a literal would both keep passing if someone changed the page names and the
        // strings together, but not if the split itself went away.
        Assert.NotEqual(
            AuthFailureRoute.For(isAuthenticated: true),
            AuthFailureRoute.For(isAuthenticated: false),
            StringComparer.Ordinal);
    }

    [Fact]
    public void The_route_view_redirect_asks_the_function_rather_than_deciding_for_itself()
    {
        var source = SharedMarkup.ReadComponent("Account", "RedirectOnAuthFailure.razor");

        Assert.Contains("AuthFailureRoute.For(", source, StringComparison.Ordinal);

        // A second, hand-written destination beside the call would be the decision leaking back
        // into the component - the exact thing extracting it was for.
        Assert.DoesNotContain(@"NavigateTo(""/", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_route_view_redirect_renders_nothing_on_either_path()
    {
        // FR-80: with prerendering off (AD-14) and nothing rendered here, the protected component's
        // markup cannot reach the response while the redirect is being decided or after it.
        var source = SharedMarkup.ReadComponent("Account", "RedirectOnAuthFailure.razor");

        var markup = StripNonMarkup(source);
        var tag = Regex.Match(markup, "<[A-Za-z]", RegexOptions.None, TimeSpan.FromSeconds(5));

        Assert.False(
            tag.Success,
            $"RedirectOnAuthFailure renders markup:{Environment.NewLine}{markup}");
    }

    /// <summary>Removes the Razor comments and the <c>@code</c> block, leaving only rendered markup.</summary>
    private static string StripNonMarkup(string source)
    {
        source = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        var code = source.IndexOf("@code", StringComparison.Ordinal);

        return code >= 0 ? source[..code] : source;
    }
}
