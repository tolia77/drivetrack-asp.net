using System.Globalization;
using DriveTrack.Application.Common;

namespace DriveTrack.Web.Components.Pages;

/// <summary>
/// FR-94's fallback, in one place: how a stored point reads in a table cell when nothing has
/// resolved an address for it.
/// <para>
/// Story 5.2 owns the reverse geocoder, and story 5.1 has to work while there is none — so every
/// location this milestone stores has a null address, and a screen that rendered
/// <c>@location.Address</c> would show a column of empty cells and nothing would say why. The
/// coordinates are what the record actually holds (DR-11), so they are what is shown.
/// </para>
/// <para>
/// Shared by both delivery screens rather than written on one of them, because the fallback is the
/// same fallback: a driver reading their own delivery and a dispatcher reading the board must not
/// disagree about where a parcel is going.
/// </para>
/// </summary>
internal static class LocationText
{
    /// <summary>The separator between the two coordinates.</summary>
    /// <remarks>
    /// A comma would collide with the decimal mark: Ukrainian formats 50.4501 as <c>50,4501</c>,
    /// and "50,4501, 30,5234" is four numbers to a reader. Hence the invariant culture below and a
    /// semicolon here — a coordinate pair is a technical value, not a formatted quantity.
    /// </remarks>
    private const string Separator = "; ";

    /// <summary>
    /// Five decimal places: about a metre at these latitudes, which is finer than a map click and
    /// short enough to sit in a table cell.
    /// </summary>
    private const string CoordinateFormat = "0.#####";

    /// <summary>
    /// What a screen shows for a location: its resolved address, or its coordinates when there is
    /// none.
    /// </summary>
    /// <param name="location">The stored point.</param>
    internal static string Describe(LocationView location)
    {
        ArgumentNullException.ThrowIfNull(location);

        if (!string.IsNullOrWhiteSpace(location.Address))
        {
            return location.Address;
        }

        return location.Point.Latitude.ToString(CoordinateFormat, CultureInfo.InvariantCulture)
            + Separator
            + location.Point.Longitude.ToString(CoordinateFormat, CultureInfo.InvariantCulture);
    }
}
