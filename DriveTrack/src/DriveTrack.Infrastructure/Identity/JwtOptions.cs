namespace DriveTrack.Infrastructure.Identity;

/// <summary>
/// The bearer-token settings, bound from the <c>Jwt</c> configuration section (AD-19).
/// <para>
/// Every value arrives as an environment variable in the container — <c>Jwt__SigningKey</c>,
/// <c>Jwt__Issuer</c>, <c>Jwt__Audience</c>, <c>Jwt__LifetimeMinutes</c> — and appears in
/// <c>.env.example</c> and <c>compose.prod.yaml</c>. Nothing here has a committed default that
/// would still work: a signing key with a fallback is a signing key everyone knows.
/// </para>
/// </summary>
public sealed class JwtOptions
{
    /// <summary>The configuration section these settings bind from.</summary>
    public const string SectionName = "Jwt";

    /// <summary>
    /// Configuration key of the signing key, in the colon form. The environment-variable spelling is
    /// <c>Jwt__SigningKey</c>, which is what the startup failure names.
    /// </summary>
    public const string SigningKeyConfigurationKey = "Jwt:SigningKey";

    /// <summary>
    /// The shortest key HMAC-SHA256 accepts without the library padding it: 256 bits. A shorter key
    /// is not a weaker token, it is a token the handler refuses to sign at the first request, which
    /// is exactly the failure that must happen at startup instead.
    /// </summary>
    public const int MinimumSigningKeyBytes = 32;

    /// <summary>
    /// Configuration key of the token lifetime, in the colon form. The environment-variable
    /// spelling is <c>Jwt__LifetimeMinutes</c>, which is what the startup failure names.
    /// </summary>
    public const string LifetimeMinutesConfigurationKey = "Jwt:LifetimeMinutes";

    /// <summary>The HMAC-SHA256 signing key. Required.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>The token issuer, checked on validation.</summary>
    public string Issuer { get; set; } = "DriveTrack";

    /// <summary>The token audience, checked on validation.</summary>
    public string Audience { get; set; } = "DriveTrack";

    /// <summary>How long an issued token stays valid, in minutes.</summary>
    public int LifetimeMinutes { get; set; } = 60;
}
