using DriveTrack.Application.Common;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DriveTrack.Web.Api;

/// <summary>
/// Answers 422 for a request body that never bound - absent, syntactically invalid, or carrying a
/// field of a type the command cannot hold. Registered globally, beside
/// <see cref="EnvelopeResultFilter"/>.
/// <para>
/// AD-7's first suppression (<c>SuppressModelStateInvalidFilter</c>, <c>Program.cs</c>) means a
/// failed body binding does not short-circuit: the argument is simply omitted, the action runs with
/// a null command, and the <c>ArgumentNullException.ThrowIfNull</c> that opens every Application
/// write method becomes an unmodelled failure - 500 <c>COMMON_UNEXPECTED_ERROR</c> for what is
/// plainly the caller's own malformed bytes. Refusing here turns all of that into one modelled
/// <see cref="ValidationException"/>, which <see cref="ApiEnvelopeMiddleware"/> already envelopes as
/// 422 - the same answer <see cref="OptionalJsonConverter{T}"/> gives for an explicit null on a
/// non-nullable field.
/// </para>
/// <para>
/// An action filter and not middleware, because only the MVC filter pipeline can see which
/// parameters bound and from where: middleware sees bytes, a model binder sees one parameter. It
/// runs after the authorization filters, so <c>[Authorize]</c>'s 401 still wins, and after the
/// framework's own <c>UnsupportedContentTypeFilter</c>, which short-circuits an unreadable media
/// type into 415 before this is reached.
/// </para>
/// <para>
/// 403 is the other way round here, deliberately. Every <c>[Authorize]</c> in this adapter is bare
/// - no role, no policy - so the attribute can only ever answer 401; the product's every 403 is
/// <c>IAccessGuard</c>'s, raised <em>inside</em> an Application service, which is downstream of
/// this refusal. An authenticated caller who may not perform the operation therefore meets this
/// 422 first, and that is the only available answer: there is no command to hand the service. AD-3
/// is not weakened by it - what that rule protects is an unauthorized caller learning something
/// about stored state, and "your JSON does not parse" is a fact about the caller's own bytes. Both
/// directions are pinned in <c>RequestBodyBindingTests</c>.
/// </para>
/// <para>
/// AD-9 is untouched: this reports an adapter-level binding failure and nothing else. It never
/// inspects a bound command's values and never runs a validator - what a well-formed payload
/// <em>means</em> is still the service's question.
/// </para>
/// </summary>
internal sealed class RequestBodyBindingFilter : IActionFilter
{
    /// <summary>The prefix <c>System.Text.Json</c> puts in front of a property's JSON path.</summary>
    private const string JsonPathPrefix = "$.";

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var parameter in context.ActionDescriptor.Parameters)
        {
            // Only the body. A [FromForm] action - POST /api/deliveries/{id}/proof - reads a
            // multipart form, whose absence is the form binder's business and not this one's.
            if (parameter.BindingInfo?.BindingSource?.CanAcceptDataFrom(BindingSource.Body) != true)
            {
                continue;
            }

            // A binding that failed omits the argument entirely rather than storing null, so the
            // missing key is the signal. The null arm covers the literal `null` body, which binds
            // successfully to nothing.
            if (context.ActionArguments.TryGetValue(parameter.Name, out var argument)
                && argument is not null)
            {
                continue;
            }

            throw new ValidationException(
                ErrorCode.COMMON_VALIDATION_FAILED,
                "The request body for parameter '"
                    + parameter.Name
                    + "' of "
                    + context.ActionDescriptor.DisplayName
                    + " could not be bound: it was absent, malformed, or carried a field of the "
                    + "wrong JSON type.",
                FieldsFrom(context.ModelState));
        }
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
        // Nothing to do: the refusal happens before the action, and anything the action itself
        // throws belongs to ApiEnvelopeMiddleware.
    }

    /// <summary>
    /// The offending properties, read off the model state the suppression left behind.
    /// <para>
    /// NFR-4 wants the caller's own name for the input, and the JSON path the input formatter
    /// recorded is the only place in the pipeline that holds it - <c>$.mileage</c> for a wrong-typed
    /// <c>mileage</c>. Only the path is taken: the framework's prose, the parser's message and the
    /// exception behind it stay off the wire (NFR-3, NFR-14), and
    /// <see cref="EnvelopeWriter"/> resolves the message from the catalogue instead.
    /// </para>
    /// <para>
    /// Empty when no key carries a path - an absent body names no field, and a key invented from
    /// the CLR type would name an input the caller does not have.
    /// </para>
    /// </summary>
    private static List<FieldError> FieldsFrom(ModelStateDictionary modelState)
    {
        var fields = new List<FieldError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in modelState)
        {
            // Anchored, not searched: the prefix is what marks a key as a JSON path, and a key from
            // some other binding source that merely contains those two characters would otherwise
            // have a field name sliced out of its middle and handed back as the caller's own input.
            if (!entry.Key.StartsWith(JsonPathPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var field = entry.Key[JsonPathPrefix.Length..];

            // The one FieldError in the codebase whose name is already wire-spelled. Everywhere
            // else Application raises the CLR name (Rating) and EnvelopeWriter applies the naming
            // policy to reach the wire name (rating); here the name was read off the payload's own
            // JSON path, so it is the caller's spelling to begin with. Passing it through anyway is
            // correct rather than merely harmless: camelCase of an already-camelCase name is the
            // same name, and short-circuiting the policy would make this the one key on the wire
            // that does not follow whatever policy the adapter is later configured with.
            if (field.Length > 0 && seen.Add(field))
            {
                fields.Add(new FieldError(field, nameof(ErrorCode.COMMON_VALIDATION_FAILED)));
            }
        }

        return fields;
    }
}
