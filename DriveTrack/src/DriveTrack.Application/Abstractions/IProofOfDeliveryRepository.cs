using DriveTrack.Application.Authorization;
using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Abstractions;

/// <summary>
/// Persistence for <see cref="ProofOfDelivery"/>. No <c>Remove</c>: a proof is removed only by
/// the cascade from its delivery (DR-9).
/// <para>
/// No <c>Update</c> either, and for a different reason: FR-123 makes a proof immutable, so the
/// absence of the method is the rule. A path that does not exist cannot be taken by mistake.
/// </para>
/// <para>
/// The two visibility reads take an <see cref="AccessScope"/> because AD-3 puts the narrowing in the
/// <c>WHERE</c> rather than after the fact — a proof a caller may not see must be a row that was
/// never returned, so the answer is 404 and nothing about its existence is disclosed.
/// </para>
/// </summary>
public interface IProofOfDeliveryRepository
{
    /// <summary>Loads a proof, or null when there is none with that id.</summary>
    Task<ProofOfDelivery?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the delivery already has a proof (FR-123). A boolean rather than a load, because the
    /// caller wants the answer and not the row — and because the two callers of this ask it inside
    /// different transactions, one of which is about to insert.
    /// </summary>
    /// <param name="deliveryId">The delivery in question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> ExistsForDeliveryAsync(int deliveryId, CancellationToken cancellationToken);

    /// <summary>
    /// The proof of a delivery this caller may see, with its assets, or null when there is none they
    /// may see (FR-122).
    /// </summary>
    /// <param name="deliveryId">The delivery whose proof is wanted.</param>
    /// <param name="scope">The narrowing the guard answered; applied in the query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ProofOfDelivery?> FindVisibleByDeliveryAsync(
        int deliveryId,
        AccessScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// One asset this caller may read, or null. The scope reaches the asset through its proof and
    /// that proof's delivery, so "whose asset is this" is the same question as "whose delivery".
    /// </summary>
    /// <param name="assetId">The asset row's id.</param>
    /// <param name="scope">The narrowing the guard answered; applied in the query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ProofAsset?> FindVisibleAssetAsync(
        int assetId,
        AccessScope scope,
        CancellationToken cancellationToken);

    /// <summary>Stages a new proof for the next commit.</summary>
    void Add(ProofOfDelivery proof);
}
