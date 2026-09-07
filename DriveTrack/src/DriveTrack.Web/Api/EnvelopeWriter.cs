using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace DriveTrack.Web.Api;

/// <summary>
/// Builds and writes the failure envelope. One writer, used by the result filter, the exception
/// middleware and the challenge and forbid events alike, so a 401 produced by an authentication
/// handler is byte-identical to a 404 produced by an action (NFR-1).
/// <para>
/// It resolves its dependencies from <c>HttpContext.RequestServices</c> rather than taking them in a
/// constructor because AD-7's third suppression is a pair of static, scheme-agnostic methods that an
/// authentication handler's events call - a handler has a context and no injection point.
/// </para>
/// </summary>
internal static class EnvelopeWriter
{
    /// <summary>
    /// The failure envelope for a code, with its message read from the localized catalogue.
    /// </summary>
    /// <remarks>
    /// NFR-3: the message on the wire is the localized resource string for the code and nothing
    /// else. The body never reads an exception, so no stack frame, SQL fragment or constraint name
    /// has a path to a client - the guarantee is structural, not a matter of discipline.
    /// </remarks>
    public static ApiResponse Build(
        HttpContext context,
        ErrorCode code,
        IReadOnlyList<FieldError>? fieldErrors = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var localizer = context.RequestServices.GetRequiredService<IStringLocalizer<ErrorMessages>>();

        return ApiResponse.Failure(
            code,
            localizer[code.ToString()].Value,
            BuildFields(localizer, fieldErrors, SerializerOptions(context).PropertyNamingPolicy));
    }

    /// <summary>
    /// Writes the failure envelope for <paramref name="code"/> at the status AD-7 assigns it.
    /// </summary>
    public static Task WriteAsync(
        HttpContext context,
        ErrorCode code,
        IReadOnlyList<FieldError>? fieldErrors = null) =>
        WriteAsync(context, ErrorContract.StatusFor(code), code, fieldErrors);

    /// <summary>
    /// Writes the failure envelope for <paramref name="code"/> at an explicit status - used where the
    /// status came from the framework and the code was derived from it, so the two are not
    /// re-derived from each other.
    /// </summary>
    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        ErrorCode code,
        IReadOnlyList<FieldError>? fieldErrors = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Response.HasStarted)
        {
            // Nothing can be written once the headers are out: Clear, the status and the content type
            // below all throw on a started response. The middleware reaches this only after its own
            // HasStarted branch has logged the failure it could not answer; the challenge and forbid
            // events have no logger and no caller to rethrow to, so returning is the only answer that
            // does not turn an unanswerable request into an unhandled exception.
            return;
        }

        var envelope = Build(context, code, fieldErrors);

        // A failure can arrive after something has already described a different body - a
        // Content-Length from an action that got part-way, a Content-Disposition from a download
        // that then threw. Those headers would go out describing a body that no longer exists.
        // Clear resets the status too, so it has to come before the assignment below.
        context.Response.Clear();

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            envelope,
            SerializerOptions(context),
            context.RequestAborted);
    }

    /// <summary>
    /// The serializer settings the controllers use, so an envelope written by middleware matches one
    /// written through MVC - the enum-as-name converter (AD-21) above all.
    /// </summary>
    private static JsonSerializerOptions SerializerOptions(HttpContext context) =>
        context.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value
            .JsonSerializerOptions;

    /// <summary>
    /// Turns the offending fields into <c>{ field: [localized message] }</c>.
    /// <para>
    /// NFR-4 wants the caller to be able to attach each message to the input that produced it, so
    /// the key has to be the name the caller <em>sent</em>. A <see cref="FieldError"/> carries the
    /// CLR property name (<c>Rating</c>) while the payload spelled it camelCase (<c>rating</c>);
    /// passing it through verbatim would hand a form keys matching none of its fields. Running it
    /// through the same naming policy the serializer uses on the rest of the response is what makes
    /// the two agree, whatever that policy is later set to.
    /// </para>
    /// <para>
    /// Each message key is resolved through the same catalogue as the code, and a key the catalogue
    /// does not hold falls back to the generic validation text rather than putting the raw key in
    /// front of a user.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string[]>? BuildFields(
        IStringLocalizer localizer,
        IReadOnlyList<FieldError>? fieldErrors,
        JsonNamingPolicy? namingPolicy)
    {
        if (fieldErrors is null || fieldErrors.Count == 0)
        {
            return null;
        }

        var fallback = localizer[nameof(ErrorCode.COMMON_VALIDATION_FAILED)].Value;

        return fieldErrors
            .GroupBy(field => WireName(field.Field, namingPolicy), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(field => Localize(localizer, field.MessageKey, fallback)).ToArray(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// The field name as the wire spells it. Converted per dotted segment, because a nested failure
    /// arrives as <c>Address.City</c> and only the segments are property names.
    /// </summary>
    private static string WireName(string field, JsonNamingPolicy? namingPolicy) =>
        namingPolicy is null
            ? field
            : string.Join('.', field.Split('.').Select(namingPolicy.ConvertName));

    private static string Localize(IStringLocalizer localizer, string messageKey, string fallback)
    {
        var localized = localizer[messageKey];

        return localized.ResourceNotFound ? fallback : localized.Value;
    }
}
