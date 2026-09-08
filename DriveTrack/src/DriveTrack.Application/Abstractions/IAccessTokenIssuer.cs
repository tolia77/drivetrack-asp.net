namespace DriveTrack.Application.Abstractions;

/// <summary>
/// Issues the bearer token the REST adapter authenticates with (FR-4).
/// <para>
/// A port, because AD-1 forbids Application naming a JWT type: the signing algorithm, the key, the
/// claim names and the lifetime are all adapter detail. This layer knows only that an account can
/// be exchanged for an opaque string a client sends back.
/// </para>
/// </summary>
public interface IAccessTokenIssuer
{
    /// <summary>The signed access token for <paramref name="account"/>.</summary>
    string Issue(UserAccount account);
}
