namespace DriveTrack.Application.Common;

/// <summary>
/// The closed failure vocabulary (AD-8). Every member is <c>SCREAMING_SNAKE</c> and carries the
/// namespace prefix of the capability that owns it; a capability may only mint codes in its own
/// namespace, and the enum grows one capability at a time rather than being invented per endpoint.
/// <para>
/// Two rules hold every member together and are asserted by <c>ErrorContractTests</c>: each has a
/// resource key of the same name in <c>ErrorMessages.resx</c> (AD-18), and each has a status arm in
/// the adapter's status map (AD-7). Nothing is minted here that no shipped code path can produce.
/// </para>
/// </summary>
#pragma warning disable CA1707 // SCREAMING_SNAKE is the contract (AD-8); the wire sees these names.
public enum ErrorCode
{
    /// <summary>Anything that escaped unhandled. 500, and the only code whose cause is not modelled.</summary>
    COMMON_UNEXPECTED_ERROR,

    /// <summary>The addressed resource does not exist, or the caller may not learn that it does. 404.</summary>
    COMMON_NOT_FOUND,

    /// <summary>The request was understood and refused on its content. 422 (NFR-4).</summary>
    COMMON_VALIDATION_FAILED,

    /// <summary>The request collides with the current state of another row or a domain rule. 409.</summary>
    COMMON_CONFLICT,

    /// <summary>No usable credentials were presented. 401; FR-13 branches on this single code.</summary>
    AUTH_UNAUTHENTICATED,

    /// <summary>Credentials were presented and do not permit the operation. 403.</summary>
    AUTH_FORBIDDEN,

    /// <summary>PostgreSQL SQLSTATE 23505, translated in Infrastructure. 409 (AD-8).</summary>
    PERSISTENCE_UNIQUE_VIOLATION,

    /// <summary>PostgreSQL SQLSTATE 23514, translated in Infrastructure. 422 (AD-8).</summary>
    PERSISTENCE_CHECK_VIOLATION,
}
#pragma warning restore CA1707
