namespace DriveTrack.Application.Common;

/// <summary>
/// The base of AD-8's closed failure set. Failure travels as one of exactly five subclasses, each
/// carrying an <see cref="ErrorCode"/>, and nothing catches and reshapes one between the throw site
/// and the adapter.
/// <para>
/// The shared base is what makes the adapter's status map total and reflection-checkable: the
/// adapter switches on the code, never on the exception type, so a new subclass cannot introduce a
/// status the map does not cover.
/// </para>
/// <para>
/// <see cref="Exception.Message"/> is for logs only. The wire message is always the localized
/// resource string for <see cref="Code"/> (NFR-3), so no stack trace, SQL text or constraint name
/// can reach a client through this type.
/// </para>
/// </summary>
public abstract class DriveTrackException : Exception
{
    /// <summary>Creates a failure carrying a code and a log-only message.</summary>
    protected DriveTrackException(ErrorCode code, string message)
        : base(message) => Code = code;

    /// <summary>Creates a failure carrying a code, a log-only message and the cause.</summary>
    protected DriveTrackException(ErrorCode code, string message, Exception? innerException)
        : base(message, innerException) => Code = code;

    /// <summary>The contract code. The adapter maps this to a status and to a localized message.</summary>
    public ErrorCode Code { get; }
}
