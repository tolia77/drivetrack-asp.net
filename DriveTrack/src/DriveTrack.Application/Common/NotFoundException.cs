namespace DriveTrack.Application.Common;

/// <summary>
/// The addressed resource does not exist, or the caller may not learn that it does (404).
/// </summary>
public sealed class NotFoundException : DriveTrackException
{
    /// <summary>Creates a not-found failure.</summary>
    public NotFoundException(ErrorCode code, string message)
        : base(code, message)
    {
    }

    /// <summary>Creates a not-found failure with a cause.</summary>
    public NotFoundException(ErrorCode code, string message, Exception? innerException)
        : base(code, message, innerException)
    {
    }
}
