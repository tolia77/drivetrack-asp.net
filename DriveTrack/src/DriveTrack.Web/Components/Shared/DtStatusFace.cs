namespace DriveTrack.Web.Components.Shared;

/// <summary>
/// The faces a <c>DtStatusLabel</c> can wear: the four delivery statuses and the two shift states.
/// <para>
/// One enum for two vocabularies, and deliberately not one shared with either. A delivery status
/// and a shift state answer different questions — where a parcel is, and whether a window of work
/// is over — so the design gives them the same shape and keeps their words apart. What they do
/// share is a fill, a shape and a word, which is what this enum names: it says how a label looks,
/// never what it means. The meaning stays with the two components that own the closed sets
/// (<c>DeliveryStatusLabel</c> and <c>ShiftStateLabel</c>), and a driver's present-tense duty is a
/// third claim that wears the plain Badge instead.
/// </para>
/// </summary>
public enum DtStatusFace
{
    /// <summary>Awaiting collection: a hollow circle on the neutral fill.</summary>
    Pending,

    /// <summary>On its way: a triangle on the brand tint.</summary>
    Transit,

    /// <summary>Handed over: a filled circle on the success tint.</summary>
    Delivered,

    /// <summary>Not delivered: a cross on the danger tint.</summary>
    Failed,

    /// <summary>A shift still open: a filled circle on the success tint.</summary>
    Running,

    /// <summary>A shift that has ended: a square on the neutral fill.</summary>
    Finished,
}
