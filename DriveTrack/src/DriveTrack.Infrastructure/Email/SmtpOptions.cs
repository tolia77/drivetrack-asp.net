namespace DriveTrack.Infrastructure.Email;

/// <summary>
/// The outbound-mail settings, bound from the <c>Smtp</c> configuration section (AD-19).
/// <para>
/// Every value arrives as an environment variable in the container — <c>Smtp__Host</c>,
/// <c>Smtp__Port</c>, <c>Smtp__UseStartTls</c>, <c>Smtp__User</c>, <c>Smtp__Password</c>,
/// <c>Smtp__FromAddress</c>, <c>Smtp__FromName</c> — and every one of them already appears in
/// <c>.env.example</c> and <c>compose.dev.yaml</c>. The property names match those spellings
/// exactly, because a binder that silently leaves a property at its default is the failure mode
/// this section is most likely to hit.
/// </para>
/// <para>
/// <see cref="Host"/> has no default, and its absence is not an error at startup. A deployment with
/// no relay configured is a deployment that sends no mail, and that has to be a running system
/// rather than one that refuses to start: FR-28's remediation for a notification that could not be
/// sent is a recorded attempt, not a dead application.
/// </para>
/// </summary>
public sealed class SmtpOptions
{
    /// <summary>The configuration section these settings bind from.</summary>
    public const string SectionName = "Smtp";

    /// <summary>
    /// Configuration key of the port, in the colon form. The environment-variable spelling is
    /// <c>Smtp__Port</c>, which is what the startup failure names.
    /// </summary>
    public const string PortConfigurationKey = "Smtp:Port";

    /// <summary>
    /// Configuration key of the TLS switch, in the colon form. The environment-variable spelling is
    /// <c>Smtp__UseStartTls</c>.
    /// </summary>
    public const string UseStartTlsConfigurationKey = "Smtp:UseStartTls";

    /// <summary>The relay's host name. Blank means no relay is configured and nothing is sent.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The relay's port. The submission port, which is what a development relay listens on.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Whether to upgrade the connection to TLS before authenticating.</summary>
    public bool UseStartTls { get; set; }

    /// <summary>The account to authenticate as, or blank for a relay that wants no credentials.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>That account's password. Never committed; supplied as an environment variable.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>The address the notice is sent from.</summary>
    public string FromAddress { get; set; } = "no-reply@drivetrack.local";

    /// <summary>The display name beside that address.</summary>
    public string FromName { get; set; } = "DriveTrack";
}
