using DriveTrack.Application.Common;

namespace DriveTrack.Web.Account;

/// <summary>
/// FR-13's recognition rule, in one place.
/// <para>
/// It lives in a class rather than in <c>SessionExpiryBoundary.razor</c>'s <c>@code</c> block for one
/// reason: a rule that decides whether a user is sent back to sign-in is worth asserting directly,
/// and a private static method inside a generated component class is reachable by no test. The
/// boundary calls this and holds only the markup.
/// </para>
/// </summary>
internal static class SessionExpiry
{
    /// <summary>Resource key of the notice shown when the session has ended.</summary>
    public const string ExpiredNoticeKey = "SessionExpired";

    /// <summary>Resource key of the notice shown for anything else the boundary caught.</summary>
    public const string UnexpectedNoticeKey = "CircuitError";

    /// <summary>
    /// The key of the notice <paramref name="failure"/> should be reported with.
    /// <para>
    /// The choice is a function rather than a pair of conditions in markup for the same reason
    /// <see cref="IsExpired"/> is: a test can call this and be sure which notice a given failure
    /// produces, where a test that matched the component's source could only say the two branches
    /// exist and not which is which.
    /// </para>
    /// </summary>
    public static string NoticeKey(Exception? failure) =>
        IsExpired(failure) ? ExpiredNoticeKey : UnexpectedNoticeKey;

    /// <summary>
    /// True when <paramref name="failure"/> is, or wraps, the contract's one unauthenticated code.
    /// <para>
    /// The cause chain is walked because a failure raised in a component lifecycle method reaches an
    /// <c>ErrorBoundary</c> wrapped, and a branch that only looked at the outermost exception would
    /// miss exactly the case FR-13 is about.
    /// </para>
    /// </summary>
    public static bool IsExpired(Exception? failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is DriveTrackException { Code: ErrorCode.AUTH_UNAUTHENTICATED })
            {
                return true;
            }
        }

        return false;
    }
}
