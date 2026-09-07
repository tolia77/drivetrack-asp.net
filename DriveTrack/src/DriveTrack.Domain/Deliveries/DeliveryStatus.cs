namespace DriveTrack.Domain.Deliveries;

/// <summary>
/// The lifecycle of a delivery (FR-30). Persisted and serialized as the member name, never as
/// an ordinal (AD-21), so reordering this enum cannot silently remap existing rows.
/// </summary>
public enum DeliveryStatus
{
    /// <summary>Created, not yet picked up.</summary>
    Pending,

    /// <summary>Picked up and on the way.</summary>
    InTransit,

    /// <summary>Handed over successfully.</summary>
    Delivered,

    /// <summary>Could not be handed over.</summary>
    Failed,
}
