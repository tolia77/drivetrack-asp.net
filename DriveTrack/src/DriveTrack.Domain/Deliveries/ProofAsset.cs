namespace DriveTrack.Domain.Deliveries;

/// <summary>
/// One stored artefact belonging to a proof of delivery.
/// <para>
/// It holds an opaque object-store key and a content type — never bytes, never a filesystem
/// path and never a URL (AD-26, DR-14). A URL would move the "who may see this proof" decision
/// from FR-122 to whoever holds the link.
/// </para>
/// </summary>
public sealed class ProofAsset
{
    /// <summary>Surrogate key.</summary>
    public int Id { get; set; }

    /// <summary>The proof this asset belongs to. The asset dies with it.</summary>
    public int ProofOfDeliveryId { get; set; }

    /// <summary>Whether this is the signature or a photo (FR-119).</summary>
    public ProofAssetKind Kind { get; set; }

    /// <summary>Opaque key in the object store. Meaningless outside it, by design.</summary>
    public required string StorageKey { get; set; }

    /// <summary>MIME type of the stored bytes, so a read can be served without sniffing.</summary>
    public required string ContentType { get; set; }
}
