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
    /// The failures a single field-level control shows: those naming exactly that field.
    /// <para>
    /// Compared on the root the screen already normalized to, so a rule that fired on
    /// <c>Pickup.Latitude</c> is claimed by the control over the <c>Pickup</c> input - the same
    /// collapse <see cref="For"/> does, and the reason it happens there rather than here.
    /// </para>
    /// </summary>
    /// <param name="failures">Everything the screen collected.</param>
    /// <param name="field">The CLR property name the control was given, as a plain string.</param>
    /// <returns>The subset to render under that input; empty renders nothing.</returns>
    public static IReadOnlyList<ScreenFailure> ClaimedBy(
        IReadOnlyList<ScreenFailure>? failures,
        string? field)
    {
        if (failures is null || failures.Count == 0 || string.IsNullOrWhiteSpace(field))
        {
            return [];
        }

        var wanted = field.Trim();

        return [.. failures.Where(failure =>
            string.Equals(failure.Field, wanted, StringComparison.Ordinal))];
    }

    /// <summary>
    /// The failures no field-level control on the screen has claimed - what the banner still has to
    /// show.
    /// <para>
    /// FR-83 is the whole point of the subtraction being stated this way round. A screen names the
    /// fields it renders an input for, and <em>everything else</em> falls through: a failure on a
    /// field the open dialog does not show, a 403, a 404, a 409 carrying no field at all. The
    /// alternative - a banner that hides whatever any field might have claimed - would make a
    /// refusal invisible the first time a form dropped an input.
    /// </para>
    /// </summary>
    /// <param name="failures">Everything the screen collected.</param>
    /// <param name="fields">
    /// The fields rendered inline, comma-separated, exactly as the <c>DtFieldError</c> controls on
    /// the same screen spell them. Null or blank claims nothing, which is why a screen that has not
    /// been given per-field messages still shows every refusal.
    /// </param>
    /// <returns>The subset the banner renders.</returns>
    public static IReadOnlyList<ScreenFailure> Unclaimed(
        IReadOnlyList<ScreenFailure>? failures,
        string? fields)
    {
        if (failures is null || failures.Count == 0)
        {
            return [];
        }

        var claimed = Names(fields);

        return claimed.Count == 0
            ? failures
            : [.. failures.Where(failure =>
                failure.Field is not { } field || !claimed.Contains(field))];
    }

    /// <summary>
    /// The field names in a comma-separated attribute value, blanks dropped. Public so the source
    /// scan that pairs a banner's list against the screen's own <c>DtFieldError</c> controls reads
    /// the list the same way the banner does.
    /// </summary>
    /// <param name="fields">The attribute value, or null.</param>
    /// <returns>The names, without duplicates.</returns>
    public static IReadOnlySet<string> Names(string? fields) =>
        string.IsNullOrWhiteSpace(fields)
            ? new HashSet<string>(StringComparer.Ordinal)
            : fields
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

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
