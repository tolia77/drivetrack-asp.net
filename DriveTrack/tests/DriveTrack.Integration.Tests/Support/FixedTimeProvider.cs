namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// A clock that stands still at a chosen instant (AD-13).
/// <para>
/// The reason the token issuer takes a <c>TimeProvider</c> at all is so a lifetime rule can be
/// tested by moving a clock rather than by waiting an hour. This is the smallest thing that
/// makes that possible: back-date the host's clock, issue a token, and the token is already
/// expired against the real clock the bearer handler validates with.
/// </para>
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    /// <summary>The instant every read returns. Settable, so one host can span two moments.</summary>
    public DateTimeOffset Now { get; set; } = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;
}
