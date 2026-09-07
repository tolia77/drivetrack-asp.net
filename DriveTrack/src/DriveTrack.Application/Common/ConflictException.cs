namespace DriveTrack.Application.Common;

/// <summary>
/// The request collides with a row that already exists (409). Unique-constraint violations are
/// translated into this by <c>PostgresConstraintTranslator</c> (AD-8).
/// </summary>
public sealed class ConflictException : DriveTrackException
{
    /// <summary>Creates a conflict failure.</summary>
    public ConflictException(ErrorCode code, string message)
        : base(code, message)
    {
    }

    /// <summary>Creates a conflict failure with a cause.</summary>
    public ConflictException(ErrorCode code, string message, Exception? innerException)
        : base(code, message, innerException)
    {
    }
}
