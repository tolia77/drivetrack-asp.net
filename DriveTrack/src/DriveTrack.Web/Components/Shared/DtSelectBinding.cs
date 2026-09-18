using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace DriveTrack.Web.Components.Shared;

/// <summary>
/// The one decision <c>DtSelect</c> takes: what the model should hold once the browser has said
/// which option is picked.
/// <para>
/// Lifted out of the component for the reason <c>ReviewViews.ClassFor</c> is: a static render
/// dispatches no events, so a rule written inside a
/// change handler is a rule no test in this solution can reach. The demonstration is concrete —
/// dropping the empty-string arm makes the dispatch board's status filter unclearable and a driver
/// unassignable, and leaves every component test green.
/// </para>
/// </summary>
internal static class DtSelectBinding
{
    /// <summary>
    /// Reads the browser's answer as a value of <typeparamref name="TValue"/>.
    /// </summary>
    /// <typeparam name="TValue">What the consumer's model holds.</typeparam>
    /// <param name="text">The picked option's value, as the change event carried it.</param>
    /// <param name="value">
    /// What the model should now hold. Only meaningful when this method answers <c>true</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> when <paramref name="value"/> is what the model should now hold; <c>false</c>
    /// when the answer is one this type cannot express, in which case the model keeps what it had.
    /// </returns>
    /// <remarks>
    /// Three answers, and the two refusals are the point.
    /// <list type="bullet">
    /// <item>
    /// An empty option means absence — <c>&lt;option value=""&gt;</c> is how every picker on this
    /// product spells "no driver" and "all statuses", and <c>BindConverter</c> refuses <c>""</c> for
    /// an <c>int?</c> rather than reading it as null. Absence is only written when the type can hold
    /// it: on a non-nullable <typeparamref name="TValue"/> the default is <c>0</c>, or an enum's
    /// zero member, and writing one of those would be recording a real choice nobody made.
    /// </item>
    /// <item>
    /// A value this type cannot parse is nothing the model can hold, so it keeps what it had. No
    /// option produces one, so the honest answer is not to clear a picker the reader did not clear.
    /// </item>
    /// </list>
    /// </remarks>
    internal static bool TryRead<TValue>(string? text, out TValue? value)
    {
        if (string.IsNullOrEmpty(text))
        {
            value = default;

            // `default(TValue) is null` is the question "can this type say nothing at all?" asked
            // without a type constraint the consumers could not all satisfy: a nullable or a
            // reference type answers null, and every other value type answers its zero.
            return default(TValue) is null;
        }

        if (BindConverter.TryConvertTo<TValue>(text, CultureInfo.CurrentCulture, out var parsed))
        {
            value = parsed;

            return true;
        }

        value = default;

        return false;
    }
}
