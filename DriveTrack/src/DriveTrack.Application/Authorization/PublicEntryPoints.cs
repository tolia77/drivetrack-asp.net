namespace DriveTrack.Application.Authorization;

/// <summary>
/// AD-2's allowlist: the closed, documented set of Application service methods that reach no
/// <see cref="IAccessGuard"/> call because they are the operations an anonymous caller performs.
/// <para>
/// It is declared here and nowhere else. <c>GuardCoverageTests</c> reads it in both directions: a
/// service method that neither calls the guard nor appears here fails the build, and an entry here
/// that resolves to no method fails it too, so the allowlist cannot rot into a list of names that
/// used to mean something.
/// </para>
/// <para>
/// FR-99's landing page is anonymous as well, and is deliberately absent: it renders static text
/// and reaches no application service at all, so there is nothing here to allow.
/// </para>
/// </summary>
public static class PublicEntryPoints
{
    /// <summary>
    /// <c>TypeName.MethodName</c> for each allowlisted method. Two entries, both of them the
    /// operations a caller performs before they have any credentials to be judged on.
    /// </summary>
    public static readonly IReadOnlySet<string> Methods = new HashSet<string>(StringComparer.Ordinal)
    {
        // FR-1: registration creates the account the caller would otherwise be authorized as.
        "UserService.RegisterClientAsync",

        // FR-4: sign-in is how credentials are presented in the first place.
        "UserService.SignInAsync",
    };
}
