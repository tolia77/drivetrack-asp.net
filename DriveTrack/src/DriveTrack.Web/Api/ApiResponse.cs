using System.Text.Json.Serialization;
using DriveTrack.Application.Common;

namespace DriveTrack.Web.Api;

/// <summary>
/// The failure half of the envelope.
/// </summary>
/// <param name="Code">
/// The contract code, serialized as its member name rather than an ordinal (AD-21). This is what a
/// client branches on - FR-13 keys the session-expiry flow off <c>AUTH_UNAUTHENTICATED</c>.
/// </param>
/// <param name="Message">
/// The localized message for <paramref name="Code"/>, always read from the resource catalogue and
/// never from an exception (NFR-3).
/// </param>
/// <param name="Fields">
/// Offending field names mapped to their localized messages, present only on a validation failure
/// (NFR-4). The keys are spelled the way the caller's payload spelled them, so a form can attach
/// each message to the input that produced it.
/// </param>
public sealed record ApiError(
    ErrorCode Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string[]>? Fields);

/// <summary>
/// NFR-1's one documented envelope, expressed once. Every REST response - success or failure, 401
/// and 403 included - has this shape, which is the single defect this contract exists to close: the
/// original system returned a bare object here, a <c>{detail}</c> there and an HTML page for a
/// missing route.
/// </summary>
/// <param name="Success">True exactly when <paramref name="Error"/> is null.</param>
/// <param name="Data">The payload on success, null on failure.</param>
/// <param name="Error">The failure on error, null on success.</param>
public sealed record ApiResponse(bool Success, object? Data, ApiError? Error)
{
    /// <summary>Wraps an action's return value.</summary>
    public static ApiResponse Ok(object? data) => new(true, data, null);

    /// <summary>Builds the failure envelope for a code and its already-localized message.</summary>
    public static ApiResponse Failure(
        ErrorCode code,
        string message,
        IReadOnlyDictionary<string, string[]>? fields = null) =>
        new(false, null, new ApiError(code, message, fields));
}
