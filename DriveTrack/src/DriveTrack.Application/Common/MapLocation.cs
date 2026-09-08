namespace DriveTrack.Application.Common;

/// <summary>
/// A point a map component can show, expressed once for every capability epic (AD-17).
/// <para>
/// AD-17 keeps a DTO beside the feature that owns it; this is the deliberate exception. The one
/// shared map is a UI component, and a component that bound to
/// <c>DriveTrack.Domain.Common.Location</c> would tie the shell to the domain model — so
/// coordinates cross the boundary as this pair instead, and every epic from 4 onward hands the
/// same shape to the same component.
/// </para>
/// <para>
/// The ranges are checked at construction rather than trusted, because the failure they prevent is
/// silent: a latitude of 500 raises nothing anywhere, it produces a map centred on nothing and a
/// marker nobody ever finds. The properties are get-only rather than <c>init</c> for the same
/// reason — a <c>with</c> expression would otherwise be a way around the constructor.
/// </para>
/// </summary>
public readonly record struct MapLocation
{
    /// <summary>Creates a location, rejecting coordinates no map could show.</summary>
    /// <param name="latitude">Latitude in decimal degrees, −90 to 90.</param>
    /// <param name="longitude">Longitude in decimal degrees, −180 to 180.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A coordinate falls outside the decimal-degree range, named by the component that is wrong.
    /// </exception>
    public MapLocation(double latitude, double longitude)
    {
        // Written as a negated range rather than two comparisons, so NaN — which fails every
        // comparison — is rejected by the same guard instead of slipping through as "not greater
        // than 90". A coordinate that is not a number is not a point on the map either.
        if (!(latitude >= -90 && latitude <= 90))
        {
            throw new ArgumentOutOfRangeException(
                nameof(latitude),
                latitude,
                "Latitude must be between -90 and 90 decimal degrees.");
        }

        if (!(longitude >= -180 && longitude <= 180))
        {
            throw new ArgumentOutOfRangeException(
                nameof(longitude),
                longitude,
                "Longitude must be between -180 and 180 decimal degrees.");
        }

        Latitude = latitude;
        Longitude = longitude;
    }

    /// <summary>Latitude in decimal degrees.</summary>
    public double Latitude { get; }

    /// <summary>Longitude in decimal degrees.</summary>
    public double Longitude { get; }

    /// <summary>Splits the pair, so a caller can name both halves in one statement.</summary>
    /// <param name="latitude">Latitude in decimal degrees.</param>
    /// <param name="longitude">Longitude in decimal degrees.</param>
    public void Deconstruct(out double latitude, out double longitude)
    {
        latitude = Latitude;
        longitude = Longitude;
    }
}
