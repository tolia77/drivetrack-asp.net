using DriveTrack.Application.Common;

namespace DriveTrack.Web.Components.Pages.Admin;

/// <summary>
/// FR-50's rule, in one place: what an administrator typed into a password box, as the absent-or-
/// present answer an update command carries (AD-23).
/// <para>
/// It lives in a class rather than in each screen's <c>@code</c> block for the reason
/// <c>SessionExpiry.NoticeKey</c> does: this is the decision the whole story turns on, and a private
/// method inside a generated component class is reachable by no test. Both screens call this and
/// hold only the markup, so replacing <see cref="Optional{T}.Absent"/> with a present value here
/// fails a test instead of shipping a silently cleared password.
/// </para>
/// </summary>
internal static class PasswordBox
{
    /// <summary>
    /// <see cref="Optional{T}.Absent"/> — leave the stored password alone — for a box the
    /// administrator did not fill in, and the typed value otherwise.
    /// </summary>
    /// <param name="typed">The bound value of the password input; null before anything is typed.</param>
    /// <remarks>
    /// Whitespace counts as empty. A box holding three spaces is somebody who typed nothing they
    /// meant, and treating it as a password would set one nobody could reproduce; treating it as
    /// absent leaves the account exactly as it was, which is what the field's own hint promises.
    /// The value is otherwise passed through untouched — leading and trailing space is part of a
    /// password the caller did mean, so it is never trimmed away.
    /// </remarks>
    public static Optional<string?> ToOptional(string? typed) =>
        string.IsNullOrWhiteSpace(typed)
            ? Optional<string?>.Absent
            : Optional<string?>.Present(typed);
}
