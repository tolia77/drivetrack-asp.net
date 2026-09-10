using DriveTrack.Application.Common;
using Microsoft.AspNetCore.Http;

namespace DriveTrack.Web.Api;

/// <summary>
/// The one status table (AD-7). NFR-2 - equivalent failures return the same status code everywhere -
/// is only true if there is exactly one place that decides, so every writer in this folder asks
/// here and nothing maps a status inline.
/// </summary>
internal static class ErrorContract
{
    /// <summary>
    /// The HTTP status for a contract code.
    /// <para>
    /// The throwing default arm makes this switch exhaustive to the compiler, so CS8509 can never
    /// fire here: the gate on a member added without a status is
    /// <c>ErrorContractTests.Every_error_code_has_a_status</c>, which calls this for every declared
    /// member. Throwing rather than returning 500 is what makes that gate possible - a plausible
    /// fallback would pass the test and answer the wrong status in production.
    /// </para>
    /// </summary>
    public static int StatusFor(ErrorCode code) => code switch
    {
        ErrorCode.COMMON_UNEXPECTED_ERROR => StatusCodes.Status500InternalServerError,
        ErrorCode.COMMON_NOT_FOUND => StatusCodes.Status404NotFound,
        ErrorCode.COMMON_VALIDATION_FAILED => StatusCodes.Status422UnprocessableEntity,
        ErrorCode.COMMON_CONFLICT => StatusCodes.Status409Conflict,
        ErrorCode.AUTH_UNAUTHENTICATED => StatusCodes.Status401Unauthorized,
        ErrorCode.AUTH_FORBIDDEN => StatusCodes.Status403Forbidden,

        // 401, not 403: no usable credentials were established, and the caller's next move is to
        // present some. The status comes from the code, never from the exception type - which is
        // why a ForbiddenException carrying this code still answers 401.
        ErrorCode.AUTH_INVALID_CREDENTIALS => StatusCodes.Status401Unauthorized,

        // A genuine collision with a row that already exists, like every other 409 here.
        ErrorCode.AUTH_EMAIL_ALREADY_IN_USE => StatusCodes.Status409Conflict,

        // Four refusals on the content of the request (NFR-4).
        ErrorCode.AUTH_EMAIL_INVALID => StatusCodes.Status422UnprocessableEntity,
        ErrorCode.AUTH_PHONE_NUMBER_INVALID => StatusCodes.Status422UnprocessableEntity,
        ErrorCode.AUTH_PASSWORD_TOO_WEAK => StatusCodes.Status422UnprocessableEntity,
        ErrorCode.AUTH_PASSWORD_CONFIRMATION_MISMATCH => StatusCodes.Status422UnprocessableEntity,

        // Three genuine collisions with the current state of another row (FR-41, FR-43, FR-44).
        // Each is a refusal the caller can act on - free the vehicle, unassign the driver, choose
        // another plate - which is why they are distinct codes rather than one COMMON_CONFLICT.
        ErrorCode.FLEET_VEHICLE_ALREADY_ASSIGNED => StatusCodes.Status409Conflict,
        ErrorCode.FLEET_VEHICLE_IN_USE => StatusCodes.Status409Conflict,
        ErrorCode.FLEET_LICENSE_PLATE_IN_USE => StatusCodes.Status409Conflict,

        // FR-29: a delivery named a party that is not there. 404 rather than 422, because the
        // failure is a row that does not exist and not a value that is malformed - and distinct
        // from COMMON_NOT_FOUND, because the resource the caller addressed does exist. A dispatcher
        // told "driver not found" knows to pick another driver; one told "not found" does not know
        // whether the delivery itself is gone.
        ErrorCode.DELIVERY_DRIVER_NOT_FOUND => StatusCodes.Status404NotFound,
        ErrorCode.DELIVERY_CLIENT_NOT_FOUND => StatusCodes.Status404NotFound,

        // FR-103, and a 409 for the same reason the three fleet codes are: the request collides
        // with the current state of another row - the vehicle the driver holds - and the caller's
        // next move is to change one of the two figures.
        ErrorCode.DELIVERY_EXCEEDS_VEHICLE_CAPACITY => StatusCodes.Status409Conflict,

        // AD-8 splits the two constraint kinds where AD-7 folds them together, and AD-8 is the more
        // specific rule: a unique violation is a genuine conflict with another row, a check
        // violation is a value the caller should not have sent. That split is what keeps NFR-2
        // honest - a rating of 6 is 422 whether the validator or the database catches it.
        ErrorCode.PERSISTENCE_UNIQUE_VIOLATION => StatusCodes.Status409Conflict,
        ErrorCode.PERSISTENCE_CHECK_VIOLATION => StatusCodes.Status422UnprocessableEntity,

        _ => throw new ArgumentOutOfRangeException(
            nameof(code),
            code,
            "No status is mapped for this error code. AD-7's status map is total: add an arm here "
                + "and a resource key in ErrorMessages.resx when minting a code."),
    };

    /// <summary>
    /// The contract code for a status the framework produced on its own - a router 404, an
    /// authorization 403, a status set by an action with no value. Everything unrecognised is
    /// <c>COMMON_UNEXPECTED_ERROR</c>, because an unmodelled failure is a defect, not a client error.
    /// </summary>
    public static ErrorCode DefaultCodeFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status401Unauthorized => ErrorCode.AUTH_UNAUTHENTICATED,
        StatusCodes.Status403Forbidden => ErrorCode.AUTH_FORBIDDEN,
        StatusCodes.Status404NotFound => ErrorCode.COMMON_NOT_FOUND,
        StatusCodes.Status409Conflict => ErrorCode.COMMON_CONFLICT,
        StatusCodes.Status400BadRequest => ErrorCode.COMMON_VALIDATION_FAILED,
        StatusCodes.Status422UnprocessableEntity => ErrorCode.COMMON_VALIDATION_FAILED,
        _ => ErrorCode.COMMON_UNEXPECTED_ERROR,
    };
}
