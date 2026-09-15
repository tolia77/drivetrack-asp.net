namespace DriveTrack.Application.Abstractions;

/// <summary>
/// AD-26's object-storage port: a key minted on request, bytes written under it, the bytes again.
/// <para>
/// Three members and no fourth, and the absences are still the design. There is no <c>UrlFor</c>, no
/// <c>PresignAsync</c> and no member returning anything a browser could fetch on its own, because a
/// URL moves the "who may see this proof" decision from FR-122 to whoever holds the link — and a
/// presigned URL that leaks is a leak with an expiry date rather than a leak that was prevented.
/// Every read of a stored object therefore passes through an authenticated route that asks the
/// capability first. There is no delete either, and the reason is FR-123 rather than housekeeping: a
/// proof is immutable, so nothing in the system has a reason to remove an object a proof names, and a
/// port that could would be a way to destroy evidence. Whether an unreferenced object is tolerable is
/// a separate question, and the ordering below is what stops one being created in the first place.
/// </para>
/// <para>
/// The third member is not a delete and does not weaken that list. <see cref="NewKey"/> splits
/// <em>naming</em> an object from <em>writing</em> one, which is the whole of what the ordering below
/// needs: a key can be committed before it holds anything. Minting stays inside the adapter, so a key
/// is still something only the store can compose (DR-14) and nothing above it knows how to spell one.
/// What the split does move onto the caller is the discipline of writing under a key this port
/// minted: <see cref="SaveAsync"/> writes wherever it is told, and only <c>NewKey</c> promises the
/// result is opaque.
/// </para>
/// <para>
/// <b>Ordering.</b> Every call here still happens with no unit of work open — that half of AD-26 is
/// untouched — but the store is now written on the far side of the commit rather than the near one.
/// The keys are minted (no I/O, nothing created), the row that names them is committed, and only then
/// are the bytes written. So a capture refused by a guard, a validator, a re-check or a unique index
/// leaves the bucket exactly as it was found, and every object that ever reaches the store is named
/// by a row that is already safe. The caller is responsible for that ordering — this interface cannot
/// enforce it — and <c>ProofOfDeliveryService.CaptureAsync</c> is the one place in the system that
/// has to.
/// </para>
/// <para>
/// <b>The failure contract, and why it is the opposite of <see cref="IGeocoder"/>'s.</b> A geocoder
/// that cannot answer returns null, because an address is a cache DR-11 makes optional. A store that
/// cannot write <em>throws</em>, because a capture is only as good as its evidence and a 200 whose
/// assets do not resolve would be the system asserting evidence it does not hold. The exception
/// propagates, and the row committed a moment earlier is deliberately kept: it is what names the
/// bytes that <em>were</em> written before the failure, and it turns the residue of a half-written
/// capture into a state the system already models. A row whose object is absent is not a crash —
/// <see cref="OpenAsync"/> answers null for it, and the asset route turns that into a 404.
/// </para>
/// </summary>
public interface IAssetStore
{
    /// <summary>
    /// Mints a key nothing is yet stored under.
    /// </summary>
    /// <param name="contentType">
    /// The MIME type the object will be written with. Read only to decorate the key — the key is
    /// opaque either way, and nothing ever parses it back.
    /// </param>
    /// <returns>
    /// The key: minted by the store, opaque to everything above it, and meaningless outside it.
    /// Never a path and never a URL (DR-14).
    /// </returns>
    /// <remarks>
    /// Naming an object is not creating one. Nothing reaches the store here, so a caller that mints
    /// a key and then refuses the request has left no trace at all — which is exactly why the capture
    /// mints before it opens its transaction. A store that is not configured still refuses here
    /// rather than later, so an unusable deployment is found before a row is committed against it.
    /// </remarks>
    /// <exception cref="Exception">
    /// No store is configured. Deliberately unmodelled and deliberately fatal, for the same reason
    /// <see cref="SaveAsync"/>'s failure is.
    /// </exception>
    string NewKey(string contentType);

    /// <summary>
    /// Writes the bytes under a key <see cref="NewKey"/> minted.
    /// </summary>
    /// <param name="key">The key to write under, from <see cref="NewKey"/>.</param>
    /// <param name="content">
    /// The bytes. Read to the end by the adapter; the caller owns the stream and disposes it.
    /// </param>
    /// <param name="contentType">
    /// The MIME type to store alongside them, so a later read can be served without sniffing.
    /// Already validated by the caller (NFR-28) — the store does not judge what it is handed.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Exception">
    /// The object was not stored. Deliberately unmodelled and deliberately fatal: the caller is
    /// asserting that a hand-over happened and has just failed to record the evidence for it, so the
    /// only honest answer is a failed request — which is what an exception here guarantees and what
    /// a null return would not.
    /// </exception>
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the object stored under <paramref name="key"/>, or answers null when nothing is.
    /// </summary>
    /// <param name="key">A key <see cref="NewKey"/> minted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The bytes, as a stream the caller disposes, or null when the key resolves to nothing. Null is
    /// a 404 rather than a 500: the row survives its object being removed out of band — or never
    /// written, when the store refused the write that followed the commit — and the honest answer to
    /// "show me this image" is then that there is none.
    /// </returns>
    Task<Stream?> OpenAsync(string key, CancellationToken cancellationToken);
}
