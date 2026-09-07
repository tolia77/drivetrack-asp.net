using DriveTrack.Domain.Common;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Domain.Deliveries;

/// <summary>
/// Evidence that a parcel changed hands (FR-119). Exactly one exists per delivery, enforced by
/// a unique index on <c>proof_of_deliveries.delivery_id</c>.
/// </summary>
public sealed class ProofOfDelivery
{
    /// <summary>Surrogate key.</summary>
    public int Id { get; set; }

    /// <summary>The delivery this proves. Unique; the proof dies with the delivery (DR-9).</summary>
    public int DeliveryId { get; set; }

    /// <summary>Who took the parcel at the door.</summary>
    public required string RecipientName { get; set; }

    /// <summary>Where the hand-over happened. Embedded in this row (AD-11).</summary>
    public required Location CaptureLocation { get; set; }

    /// <summary>When the hand-over happened, at offset zero (AD-13).</summary>
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>
    /// Who captured the proof, or null once that user is deleted (AD-20 set-null). Cascade here
    /// would destroy the evidence that a delivery was completed because the driver's account was
    /// closed; restrict would make closing it impossible.
    /// </summary>
    public UserId? CapturedByUserId { get; set; }

    /// <summary>
    /// The stored artefacts. FR-119 needs a signature <em>and</em> at least one photo, so a
    /// proof owns many assets rather than a single file reference.
    /// </summary>
    public ICollection<ProofAsset> Assets { get; } = [];
}
