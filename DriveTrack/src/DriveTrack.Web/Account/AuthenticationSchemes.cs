namespace DriveTrack.Web.Account;

/// <summary>
/// The scheme names, in one place because three files have to agree on them: the composition root
/// registers them, the sign-in pages sign a principal into the cookie one by name, and the sign-out
/// endpoint clears it by name.
/// <para>
/// Signing in or out "on the default scheme" would resolve to <see cref="Selector"/>, which forwards
/// by path and has nothing of its own to write - so every one of those calls names
/// <see cref="Cookie"/> explicitly.
/// </para>
/// </summary>
internal static class AuthenticationSchemes
{
    /// <summary>The policy scheme that forwards by path. Also the application's default scheme.</summary>
    public const string Selector = "DriveTrack";

    /// <summary>The cookie scheme, which serves everything that is not <c>/api</c>.</summary>
    public const string Cookie = "DriveTrack.Cookie";
}
