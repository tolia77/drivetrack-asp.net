using DriveTrack.Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DriveTrack.Web.Api;

/// <summary>
/// The envelope backstop for everything the result filter cannot reach: an exception thrown before
/// or outside the action pipeline, and a status the router produced with no body at all - an
/// unmatched <c>/api</c> route being the case <c>UseStatusCodePagesWithReExecute</c> would otherwise
/// answer with the HTML not-found page (NFR-1).
/// <para>
/// Branched onto <c>/api</c> only, so the Blazor shell keeps its HTML error and not-found pages.
/// This is also the single catch site for AD-8's typed failures: nothing between a throw site and
/// here catches and reshapes one, which is what makes "the code the service threw is the code the
/// client sees" true rather than customary.
/// </para>
/// </summary>
internal sealed class ApiEnvelopeMiddleware(RequestDelegate next, ILogger<ApiEnvelopeMiddleware> logger)
{
    /// <inheritdoc cref="RequestDelegate" />
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context);
        }
        catch (DriveTrackException exception)
        {
            // A modelled failure. Logged at Warning with the exception, because the message carries
            // the diagnostic detail - the constraint name, the offending fields - that the envelope
            // deliberately never shows a client.
            logger.LogWarning(
                exception,
                "Request {Method} {Path} failed with {ErrorCode}.",
                context.Request.Method,
                context.Request.Path,
                exception.Code);

            if (await AbortIfResponseStarted(context, exception))
            {
                return;
            }

            var fieldErrors = exception is ValidationException validation
                ? validation.FieldErrors
                : null;

            await EnvelopeWriter.WriteAsync(context, exception.Code, fieldErrors);

            return;
        }
        catch (Exception exception)
        {
            // Unmodelled: a defect. AD-7 assigns it no status, and NFR-3 still demands a structured
            // error, so it becomes 500 plus COMMON_UNEXPECTED_ERROR. Logged once, at Error, with the
            // exception - once, because nothing above rethrows it.
            logger.LogError(
                exception,
                "Request {Method} {Path} failed unexpectedly.",
                context.Request.Method,
                context.Request.Path);

            if (await AbortIfResponseStarted(context, exception))
            {
                return;
            }

            await EnvelopeWriter.WriteAsync(context, ErrorCode.COMMON_UNEXPECTED_ERROR);

            return;
        }

        await BackstopAsync(context);
    }

    /// <summary>
    /// Covers a status produced with no body: an unmatched route, an authorization short-circuit no
    /// challenge writer handled, a status set by a terminal middleware. A response that already
    /// carries a body or a content type is left alone - it is someone else's shape, and the two
    /// concatenated would be neither.
    /// </summary>
    private static async Task BackstopAsync(HttpContext context)
    {
        if (context.Response.HasStarted
            || context.Response.StatusCode < StatusCodes.Status400BadRequest
            || context.Response.ContentType is not null
            || context.Response.ContentLength is > 0)
        {
            return;
        }

        await EnvelopeWriter.WriteAsync(
            context,
            context.Response.StatusCode,
            ErrorContract.DefaultCodeFor(context.Response.StatusCode));
    }

    /// <summary>
    /// A response already on the wire cannot be replaced by an envelope. Rethrowing would tear the
    /// connection down without a log line explaining why, so the failure is recorded and the request
    /// ends here.
    /// </summary>
    private async Task<bool> AbortIfResponseStarted(HttpContext context, Exception exception)
    {
        if (!context.Response.HasStarted)
        {
            return false;
        }

        logger.LogError(
            exception,
            "Cannot write the error envelope for {Method} {Path}: the response had already started.",
            context.Request.Method,
            context.Request.Path);

        await context.Response.CompleteAsync();

        return true;
    }
}
