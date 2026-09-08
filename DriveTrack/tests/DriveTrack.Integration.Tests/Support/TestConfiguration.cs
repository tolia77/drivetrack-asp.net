namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// The configuration every test host and every hostless registration test needs.
/// <para>
/// <c>AddInfrastructure</c> aborts startup on a missing or short signing key (AD-19), which is the
/// behaviour the identity story wants and which every existing suite would otherwise trip over.
/// One constant here is what keeps that failure a real assertion in one test rather than a
/// coincidence every other test has to work around.
/// </para>
/// </summary>
internal static class TestConfiguration
{
    /// <summary>A signing key comfortably over the 32-byte floor. Test-only, and obviously so.</summary>
    public const string JwtSigningKey = "drivetrack-tests-signing-key-0123456789-abcdefghij";

    /// <summary>The issuer the test host signs and validates with.</summary>
    public const string JwtIssuer = "DriveTrack.Tests";

    /// <summary>The audience the test host signs and validates with.</summary>
    public const string JwtAudience = "DriveTrack.Tests";

    /// <summary>The seeded administrator's email in a test host.</summary>
    public const string AdminEmail = "admin@drivetrack.test";

    /// <summary>The seeded administrator's password in a test host.</summary>
    public const string AdminPassword = "Admin-Passw0rd";

    /// <summary>The settings a host needs beyond its connection string.</summary>
    public static Dictionary<string, string?> Defaults() => new(StringComparer.Ordinal)
    {
        ["Jwt:SigningKey"] = JwtSigningKey,
        ["Jwt:Issuer"] = JwtIssuer,
        ["Jwt:Audience"] = JwtAudience,
        ["Jwt:LifetimeMinutes"] = "60",
        ["Admin:Email"] = AdminEmail,
        ["Admin:Password"] = AdminPassword,
        ["Admin:FirstName"] = "Адміністратор",
        ["Admin:LastName"] = "Системи",
    };
}
