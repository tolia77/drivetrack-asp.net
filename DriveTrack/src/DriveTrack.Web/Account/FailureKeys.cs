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
    /// The failures a screen renders: one per offending field on a validation failure, otherwise a
    /// single entry naming no field and carrying the code itself.
    /// <para>
    /// The field travels with the key, because most rules share <c>COMMON_VALIDATION_FAILED</c> and
    /// the key alone cannot say which input was refused. <c>Distinct</c> is on the pair, so two
    /// oversized assets are still one sentence while a weight and a note are two.
    /// </para>
    /// <para>
    /// Each field is normalized to its root segment: the wire envelope keeps the full path, but a
    /// form has an input called <c>Pickup</c>, not one called <c>Pickup.Latitude</c>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ScreenFailure> For(DriveTrackException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure is ValidationException validation && validation.FieldErrors.Count > 0)
        {
            return validation.FieldErrors
                .Select(field => new ScreenFailure(RootOf(field.Field), field.MessageKey))
                .Distinct()
                .ToArray();
        }

        return [new ScreenFailure(null, failure.Code.ToString())];
    }

    /// <summary>
    /// The first segment of a field path: everything before the first <c>.</c> or <c>[</c>. Null
    /// when nothing usable is left, so a blank name falls back to the message alone rather than
    /// asking the catalogue for an empty key.
    /// </summary>
    private static string? RootOf(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return null;
        }

        var end = field.IndexOfAny(['.', '[']);
        var root = (end < 0 ? field : field[..end]).Trim();

        return root.Length == 0 ? null : root;
    }
}
