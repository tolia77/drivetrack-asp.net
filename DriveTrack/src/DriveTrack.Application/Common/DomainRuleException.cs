namespace DriveTrack.Application.Common;

/// <summary>
/// A domain rule refused the operation - an illegal status transition being the archetype (AD-10).
/// Maps to 409, the same status as <see cref="ConflictException"/>: NFR-2 makes one kind of failure
/// one status everywhere, and both of these are "the current state does not permit this".
/// </summary>
public sealed class DomainRuleException : DriveTrackException
{
    /// <summary>Creates a domain-rule failure.</summary>
    public DomainRuleException(ErrorCode code, string message)
        : base(code, message)
    {
    }

    /// <summary>Creates a domain-rule failure with a cause.</summary>
    public DomainRuleException(ErrorCode code, string message, Exception? innerException)
        : base(code, message, innerException)
    {
    }
}
