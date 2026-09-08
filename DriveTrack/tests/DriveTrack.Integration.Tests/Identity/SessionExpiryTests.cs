using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Account;

namespace DriveTrack.Integration.Tests.Identity;

/// <summary>
/// FR-13, asserted at the two levels it is decided: the rule that recognises an expired session, and
/// the wiring that gives every screen exactly one handler for it.
/// <para>
/// No circuit is driven here, and that is the honest limit of this file: a Blazor circuit whose
/// cookie has gone needs a render harness this suite does not carry. What can be settled without one
/// is settled — the predicate survives the wrapping a lifecycle failure applies, the boundary sits
/// around every screen's body, it is the only one, and its expired branch says so in Ukrainian and
/// offers the way back to sign-in.
/// </para>
/// </summary>
public class SessionExpiryTests
{
    private static readonly string ComponentsDirectory = Path.Combine(
        RepositoryLayout.ProjectDirectory("DriveTrack.Web"),
        "Components");

    [Fact]
    public void The_contracts_unauthenticated_code_is_recognised()
    {
        Assert.True(SessionExpiry.IsExpired(
            new ForbiddenException(ErrorCode.AUTH_UNAUTHENTICATED, "The cookie is gone.")));
    }

    [Fact]
    public void A_wrapped_unauthenticated_failure_is_still_recognised()
    {
        // A failure raised inside OnInitializedAsync reaches an ErrorBoundary wrapped, so a branch
        // that only read the outermost exception would miss the one case FR-13 exists for.
        var wrapped = new InvalidOperationException(
            "Rendering failed.",
            new InvalidOperationException(
                "Component lifecycle failed.",
                new ForbiddenException(ErrorCode.AUTH_UNAUTHENTICATED, "The cookie is gone.")));

        Assert.True(SessionExpiry.IsExpired(wrapped));
    }

    [Theory]
    [InlineData(ErrorCode.AUTH_FORBIDDEN)]
    [InlineData(ErrorCode.COMMON_NOT_FOUND)]
    [InlineData(ErrorCode.AUTH_INVALID_CREDENTIALS)]
    public void Another_contract_failure_is_not_an_expired_session(ErrorCode code)
    {
        // The branch has to be narrow. A refusal the caller could act on must not be reported as
        // "your session ended" and bounce them to sign-in for no reason.
        Assert.False(SessionExpiry.IsExpired(new ForbiddenException(code, "Refused.")));
    }

    [Fact]
    public void An_unrelated_failure_is_not_an_expired_session()
    {
        Assert.False(SessionExpiry.IsExpired(new InvalidOperationException("boom")));
        Assert.False(SessionExpiry.IsExpired(failure: null));
    }

    [Fact]
    public void Every_screens_body_is_inside_the_one_boundary()
    {
        // "One handler" is a claim about the layout, not about the component: a boundary nobody
        // wrapped @Body in would pass every assertion above and catch nothing.
        var layout = File.ReadAllText(Path.Combine(ComponentsDirectory, "Layout", "MainLayout.razor"));

        var start = layout.IndexOf("<SessionExpiryBoundary>", StringComparison.Ordinal);
        var body = layout.IndexOf("@Body", StringComparison.Ordinal);
        var end = layout.IndexOf("</SessionExpiryBoundary>", StringComparison.Ordinal);

        Assert.True(start >= 0, "MainLayout does not use the session-expiry boundary.");
        Assert.InRange(body, start, end);
    }

    [Fact]
    public void The_boundary_is_the_only_one_in_the_shell()
    {
        // FR-13's "no screen implements its own", as an absence. A second ErrorBoundary anywhere
        // under Components/ would catch a failure before the shared one ever saw it.
        var offenders = Directory
            .GetFiles(ComponentsDirectory, "*.razor", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals("SessionExpiryBoundary.razor", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("<ErrorBoundary", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void An_expired_session_is_reported_with_the_expired_notice()
    {
        // The choice itself, not the shape of the file that renders it. Swapping the two branches in
        // the markup would still have satisfied a source scan; it cannot satisfy this.
        Assert.Equal(
            SessionExpiry.ExpiredNoticeKey,
            SessionExpiry.NoticeKey(
                new ForbiddenException(ErrorCode.AUTH_UNAUTHENTICATED, "The cookie is gone.")));
    }

    [Fact]
    public void Anything_else_is_reported_with_the_unexpected_notice()
    {
        Assert.Equal(
            SessionExpiry.UnexpectedNoticeKey,
            SessionExpiry.NoticeKey(new InvalidOperationException("boom")));
        Assert.Equal(
            SessionExpiry.UnexpectedNoticeKey,
            SessionExpiry.NoticeKey(
                new ForbiddenException(ErrorCode.AUTH_FORBIDDEN, "Not yours.")));
    }

    [Fact]
    public void The_boundary_renders_the_notice_the_rule_chose_and_offers_the_way_back()
    {
        // What is left for a source assertion: that the markup asks SessionExpiry which notice to
        // show rather than deciding for itself, and that the expired branch carries the link back.
        var boundary = File.ReadAllText(
            Path.Combine(ComponentsDirectory, "Shared", "SessionExpiryBoundary.razor"));

        Assert.Contains("SessionExpiry.NoticeKey(failure)", boundary, StringComparison.Ordinal);
        Assert.Contains("notice == SessionExpiry.ExpiredNoticeKey", boundary, StringComparison.Ordinal);
        Assert.Contains("@Localizer[\"SessionExpired\"]", boundary, StringComparison.Ordinal);
        Assert.Contains("href=\"/sign-in\"", boundary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_boundary_recovers_when_the_location_changes()
    {
        // An ErrorBoundary latches and MainLayout keeps this one for the life of the circuit, so
        // without recovering it a single failure would replace every later screen's content too.
        var boundary = File.ReadAllText(
            Path.Combine(ComponentsDirectory, "Shared", "SessionExpiryBoundary.razor"));

        Assert.Contains("Navigation.LocationChanged += Recover", boundary, StringComparison.Ordinal);
        Assert.Contains("Navigation.LocationChanged -= Recover", boundary, StringComparison.Ordinal);
        Assert.Contains("_boundary?.Recover()", boundary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_screen_whose_caller_has_gone_raises_the_code_the_boundary_reads()
    {
        // The other end of the chain. The circuit outlives the request that opened it, so a screen
        // has to turn "there is no caller any more" into the contract's own code rather than into a
        // null reference or a redirect of its own.
        var profile = File.ReadAllText(Path.Combine(ComponentsDirectory, "Account", "Profile.razor"));

        Assert.Contains("!Caller.IsAuthenticated", profile, StringComparison.Ordinal);
        Assert.Contains("ErrorCode.AUTH_UNAUTHENTICATED", profile, StringComparison.Ordinal);
    }
}
