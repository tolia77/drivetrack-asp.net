namespace DriveTrack.Domain.Deliveries;

/// <summary>
/// What a stored proof asset is (FR-119). Persisted as the member name (AD-21).
/// </summary>
public enum ProofAssetKind
{
    /// <summary>The recipient's signature.</summary>
    Signature,

    /// <summary>A photograph taken at the door.</summary>
    Photo,
}
