using System.Globalization;

namespace DriveTrack.Web.Components.Pages;

/// <summary>
/// The readings a table cell needs that more than one delivery screen has to agree on: the dash
/// that stands for nothing at all, and the figures of a delivery window.
/// <para>
/// Shared for the reason <see cref="LocationText"/> is. The dispatch board and the own-deliveries
/// screen render the same window out of two different DTOs, and a second copy of the four-shape
/// reading is the copy that drifts — a dispatcher and the driver carrying the parcel would then
/// disagree about when it is due, in a product where that is the whole promise.
/// </para>
/// </summary>
internal static class CellText
{
    /// <summary>
    /// What a cell shows where there is no value at all. An em dash rather than an empty cell: a
    /// blank reads as a value that failed to render, and it carries no Latin letters for NFR-14's
    /// scan to object to.
    /// </summary>
    internal const string NoValue = "—";

    /// <summary>What sits between the two bounds of a window that has both.</summary>
    private const string WindowSeparator = " – ";

    /// <summary>
    /// The figures of a delivery window as a first column shows them (FR-100), in local time.
    /// </summary>
    /// <remarks>
    /// FR-100 lets either bound stand alone and lets both be absent, so all four shapes are answered
    /// rather than assumed. A pair inside one local day says the date once and lets the two times
    /// bound it, which is how a window is read aloud; a pair that crosses midnight carries the date
    /// on both sides, or the second figure would silently claim the first one's day. A single bound
    /// is the figure alone — the calling cell's markup puts the word in front of it that says which
    /// end it is, because a separator with nothing on one side of it is not a range, and that word
    /// stays in markup so the closed-catalogue test can see the key it resolves.
    /// <para>
    /// Local is the server's configured zone: the shell renders interactively on the server, so
    /// <c>ToLocalTime</c> resolves against the host (AD-13 stores at offset zero, and the conversion
    /// happens here, in the presentation layer, and nowhere else).
    /// </para>
    /// </remarks>
    /// <param name="earliest">The near bound, or null.</param>
    /// <param name="latest">The far bound, or null.</param>
    internal static string Window(DateTimeOffset? earliest, DateTimeOffset? latest)
    {
        var from = earliest?.ToLocalTime();
        var to = latest?.ToLocalTime();

        if (from is { } start && to is { } end)
        {
            // The same local day, so the far bound needs only its time.
            if (start.Date == end.Date)
            {
                return Moment(start) + WindowSeparator + end.ToString("t", CultureInfo.CurrentCulture);
            }

            return Moment(start) + WindowSeparator + Moment(end);
        }

        if (from is { } only)
        {
            return Moment(only);
        }

        if (to is { } bound)
        {
            return Moment(bound);
        }

        return NoValue;
    }

    /// <summary>One bound of a window, written out in full.</summary>
    internal static string Moment(DateTimeOffset instant) =>
        instant.ToString("g", CultureInfo.CurrentCulture);
}
