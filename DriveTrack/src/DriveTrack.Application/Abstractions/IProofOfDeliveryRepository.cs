using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Abstractions;

/// <summary>
/// Persistence for <see cref="ProofOfDelivery"/>. No <c>Remove</c>: a proof is removed only by
/// the cascade from its delivery (DR-9).
/// </summary>
public interface IProofOfDeliveryRepository
{
    /// <summary>Loads a proof, or null when there is none with that id.</summary>
    Task<ProofOfDelivery?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Stages a new proof for the next commit.</summary>
    void Add(ProofOfDelivery proof);
}
