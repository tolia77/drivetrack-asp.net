using DriveTrack.Application.Common;

namespace DriveTrack.Web.Account;

/// <summary>
/// Turns one of AD-8's typed failures into the resource keys a screen shows.
/// <para>
/// The REST adapter already does this, in <c>EnvelopeWriter</c>: the code becomes
/// <c>error.message</c> and each field error becomes an entry in <c>error.fields</c>. The Blazor
/// forms need the same answer in a different shape, so the rule lives here rather than being
/// written out twice — a screen that resolved its own message would be the second place NFR-3 is
/// decided.
/// </para>
/// </summary>
internal static class FailureKeys
{
    /// <summary>
    /// The message keys for a failure: one per offending field on a validation failure, otherwise
    /// the code itself. Distinct, because five fields keyed the same way is one sentence, not five.
    /// </summary>
    public static IReadOnlyList<string> For(DriveTrackException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure is ValidationException validation && validation.FieldErrors.Count > 0)
        {
            return validation.FieldErrors
                .Select(field => field.MessageKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        return [failure.Code.ToString()];
    }
}
