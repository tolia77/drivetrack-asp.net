namespace DriveTrack.Application.Deliveries;

/// <summary>
/// Proof of delivery (FR-119 to FR-123): the only writer of <c>proof_of_deliveries</c>, and the only
/// route to a stored asset's bytes.
/// <para>
/// Three members, and the ones that are missing are the design. There is no update, no replace and
/// no delete: FR-123 makes a proof immutable, so a second capture is refused rather than merged, and
/// a proof is removed only by the cascade from its delivery (DR-9). Evidence that can be edited is
/// not evidence.
/// </para>
/// <para>
/// It is a capability of its own rather than three more members on <see cref="IDeliveryService"/>
/// because it owns a different outbound port — <c>IAssetStore</c> — and a different ordering rule:
/// a capture mints its keys before its transaction and writes the bytes after the commit, so it is
/// three steps around a unit of work rather than one. Folding that into the delivery service would
/// put an object-store call inside a type whose every other method is one unit of work.
/// </para>
/// </summary>
public interface IProofOfDeliveryService
{
    /// <summary>
    /// Records the hand-over (FR-119): one proof row and its assets commit together, and the bytes
    /// are written into the store afterwards, under keys the row already names.
    /// <para>
    /// Reserved to the assigned driver and to dispatch, which is the same predicate the status
    /// change uses — a client may never capture proof of their own delivery. It is allowed on a
    /// delivery that has already been marked delivered (FR-121): the parcel arrived before anyone
    /// had a chance to photograph it, and refusing the evidence afterwards would leave the gap this
    /// capability exists to close.
    /// </para>
    /// </summary>
    /// <param name="deliveryId">The delivery being proved.</param>
    /// <param name="command">Recipient, capture point and artefacts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller has no session, or is not the assigned driver and does not run dispatch.
    /// </exception>
    /// <exception cref="Common.NotFoundException">No delivery has that id.</exception>
    /// <exception cref="Common.ValidationException">
    /// The command describes a proof that may not exist: no recipient, no coordinates, no signature,
    /// no photograph, too many photographs, a disallowed type or an oversized asset (NFR-28).
    /// </exception>
    /// <exception cref="Common.ConflictException">
    /// The delivery already has a proof (<c>DELIVERY_PROOF_ALREADY_CAPTURED</c>, FR-123).
    /// </exception>
    /// <exception cref="Exception">
    /// The object store refused a write. The one failure here that does not mean "nothing happened":
    /// the bytes go in after the row is committed, so this may be raised with the proof already
    /// saved and some or none of its assets stored. Every exception above leaves the delivery
    /// exactly as it was found; this one does not, and a caller that reports a failed capture by
    /// redrawing the delivery has to refetch it rather than assume. The proof is deliberately kept —
    /// it is what names whatever did reach the store — and an asset whose object is missing is read
    /// back as a 404 by <see cref="OpenAssetAsync"/>, not as an error.
    /// </exception>
    Task<ProofOfDeliveryView> CaptureAsync(
        int deliveryId,
        CaptureProofCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// The proof of a delivery this caller may see (FR-122), with the capturer's name present only
    /// for an administrator or a dispatcher (AD-17).
    /// </summary>
    /// <param name="deliveryId">The delivery whose proof is wanted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.ForbiddenException">The caller has no session, or no row scope.</exception>
    /// <exception cref="Common.NotFoundException">
    /// There is no such delivery, this caller may not see it, or it has no proof — one answer for
    /// all three, so a client cannot learn that somebody else's delivery exists by asking.
    /// </exception>
    Task<ProofOfDeliveryView> GetAsync(int deliveryId, CancellationToken cancellationToken);

    /// <summary>
    /// One asset's bytes, for the authenticated route an <c>&lt;img&gt;</c> points at.
    /// <para>
    /// The scope narrows in the query, so an asset belonging to another client's delivery is not
    /// found rather than refused — the same non-disclosing answer as an id that never existed.
    /// </para>
    /// </summary>
    /// <param name="assetId">The asset row's id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.ForbiddenException">The caller has no session, or no row scope.</exception>
    /// <exception cref="Common.NotFoundException">
    /// No such asset, not this caller's to read, or the object is no longer in the store.
    /// </exception>
    Task<ProofAssetContent> OpenAssetAsync(int assetId, CancellationToken cancellationToken);
}
