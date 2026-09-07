namespace DriveTrack.Application.Common;

/// <summary>
/// The caller is known and may not do this (403). AD-2's guard throws this rather than returning a
/// boolean, so a forgotten check cannot be mistaken for a passed one.
/// </summary>
public sealed class ForbiddenException : DriveTrackException
{
    /// <summary>Creates a forbidden failure.</summary>
    public ForbiddenException(ErrorCode code, string message)
        : base(code, message)
    {
    }

    /// <summary>Creates a forbidden failure with a cause.</summary>
    public ForbiddenException(ErrorCode code, string message, Exception? innerException)
        : base(code, message, innerException)
    {
    }
}
