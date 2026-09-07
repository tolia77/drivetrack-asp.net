using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DriveTrack.Web.Api;

/// <summary>
/// Wraps every controller outcome in the envelope (NFR-1). Registered globally, so an action cannot
/// opt out by forgetting to.
/// <para>
/// A result filter is the only thing in the pipeline that can see an action's return value, which is
/// why the success shape lives here and not in middleware. It also owns the value-less client-error
/// results - <c>NotFound()</c>, <c>Conflict()</c> - which <c>SuppressMapClientErrors</c> stops the
/// framework from rewriting into a <c>ProblemDetails</c> at exactly the codes this contract governs.
/// Everything a filter cannot reach belongs to <see cref="ApiEnvelopeMiddleware"/>.
/// </para>
/// </summary>
internal sealed class EnvelopeResultFilter : IAsyncResultFilter
{
    /// <inheritdoc />
    public async Task OnResultExecutionAsync(
        ResultExecutingContext context,
        ResultExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        context.Result = Wrap(context.HttpContext, context.Result);

        await next();
    }

    private static IActionResult Wrap(HttpContext httpContext, IActionResult result) => result switch
    {
        // Already an envelope: the middleware and the auth events write one directly, and an action
        // may return one deliberately. Re-wrapping would produce {success:true,data:{success:false}}.
        ObjectResult { Value: ApiResponse } => result,
        JsonResult { Value: ApiResponse } => result,

        // 204, 205 and 304 forbid a body. NoContent() is a StatusCodeResult, so without this arm it
        // would leave as a 204 carrying {"success":true,...} - a response no conforming client is
        // allowed to read. Uniformity stops where HTTP does.
        ObjectResult { StatusCode: int objectStatus } when CannotCarryABody(objectStatus) => result,
        StatusCodeResult { StatusCode: var bodilessStatus } when CannotCarryABody(bodilessStatus) => result,

        ObjectResult objectResult => Envelope(
            httpContext,
            objectResult.StatusCode ?? StatusCodes.Status200OK,
            objectResult.Value),

        // Json(dto) is not an ObjectResult, so without this arm it would fall through unenveloped -
        // the second wire shape NFR-1 exists to close, arriving through the one helper that looks
        // most like it is already doing the right thing.
        JsonResult jsonResult => Envelope(
            httpContext,
            jsonResult.StatusCode ?? StatusCodes.Status200OK,
            jsonResult.Value),

        // NotFound(), Conflict(), Unauthorized(): a status and no body at all. Without this arm they
        // would leave as an empty response, which is a second wire shape (NFR-1).
        StatusCodeResult statusCodeResult => Envelope(
            httpContext,
            statusCodeResult.StatusCode,
            value: null),

        // A void action. The success envelope with a null payload keeps the shape uniform.
        EmptyResult => Envelope(httpContext, StatusCodes.Status200OK, value: null),

        // Deliberately untouched, because the envelope is a JSON contract and these are not JSON:
        // FileResult (a download), the redirect results (a Location header and no body), ContentResult
        // (the caller chose the media type) and ChallengeResult / ForbidResult (the authentication
        // handler answers, through EnvelopeAuthenticationEvents). An endpoint returning one of these
        // is opting out of the envelope on purpose; anything else new lands here and should be
        // classified rather than left to fall through.
        _ => result,
    };

    /// <summary>Statuses HTTP forbids a body on.</summary>
    private static bool CannotCarryABody(int statusCode) =>
        statusCode is StatusCodes.Status204NoContent
            or StatusCodes.Status205ResetContent
            or StatusCodes.Status304NotModified;

    /// <summary>
    /// The envelope for one status and one action value. Below 400 the value is the payload; at 400
    /// and above the value is dropped and the message comes from the localized catalogue for the
    /// code the status implies - a <c>ProblemDetails</c> or a diagnostic string an action passed
    /// alongside a 4xx is untranslated by construction and must not reach a client (NFR-3, NFR-14).
    /// </summary>
    private static ObjectResult Envelope(HttpContext httpContext, int statusCode, object? value)
    {
        var envelope = statusCode >= StatusCodes.Status400BadRequest
            ? EnvelopeWriter.Build(httpContext, ErrorContract.DefaultCodeFor(statusCode))
            : ApiResponse.Ok(value);

        return new ObjectResult(envelope)
        {
            StatusCode = statusCode,
            DeclaredType = typeof(ApiResponse),
        };
    }
}
