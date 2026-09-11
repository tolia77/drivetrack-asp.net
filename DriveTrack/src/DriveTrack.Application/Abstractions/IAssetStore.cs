namespace DriveTrack.Application.Abstractions;

/// <summary>
/// AD-26's object-storage port: bytes in, an opaque key out; the key back, the bytes again.
/// <para>
/// Two members and no third, and the absence is the design. There is no <c>UrlFor</c>, no
/// <c>PresignAsync</c> and no member returning anything a browser could fetch on its own, because a
/// URL moves the "who may see this proof" decision from FR-122 to whoever holds the link — and a
/// presigned URL that leaks is a leak with an expiry date rather than a leak that was prevented.
/// Every read of a stored object therefore passes through an authenticated route that asks the
/// capability first. There is no delete either: a proof is immutable (FR-123), and an object no row
/// references is garbage rather than an error.
/// </para>
/// <para>
/// <b>The failure contract, and why it is the opposite of <see cref="IGeocoder"/>'s.</b> A geocoder
/// that cannot answer returns null, because an address is a cache DR-11 makes optional. A store that
/// cannot write <em>throws</em>, because the row that is about to be committed would name a key that
/// resolves to nothing — evidence that says it exists and does not. So a failed
/// <see cref="SaveAsync"/> is an exception, the transaction that would have referenced the key is
/// never opened, and nothing commits.
/// </para>
/// <para>
/// <b>Ordering.</b> AD-26 fixes it: every call here happens with no unit of work open. The asset is
/// written and confirmed before the transaction that references it begins, so a committed key always
/// resolves. The caller is responsible for that ordering — this interface cannot enforce it — and
/// <c>ProofOfDeliveryService.CaptureAsync</c> is the one place in the system that has to.
/// </para>
/// </summary>
public interface IAssetStore
{
    /// <summary>
    /// Stores the bytes and answers the key they were stored under.
    /// </summary>
    /// <param name="content">
    /// The bytes. Read to the end by the adapter; the caller owns the stream and disposes it.
    /// </param>
    /// <param name="contentType">
    /// The MIME type to store alongside them, so a later read can be served without sniffing.
    /// Already validated by the caller (NFR-28) — the store does not judge what it is handed.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The key the object was stored under: minted by the store, opaque to everything above it, and
    /// meaningless outside it. Never a path and never a URL (DR-14).
    /// </returns>
    /// <exception cref="Exception">
    /// The object was not stored. Deliberately unmodelled and deliberately fatal: the only correct
    /// response is that nothing referencing this key is committed, which is what an exception here
    /// guarantees and what a null return would not.
    /// </exception>
    Task<string> SaveAsync(Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the object stored under <paramref name="key"/>, or answers null when nothing is.
    /// </summary>
    /// <param name="key">A key <see cref="SaveAsync"/> minted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The bytes, as a stream the caller disposes, or null when the key resolves to nothing. Null is
    /// a 404 rather than a 500: the row survives its object being removed out of band, and the
    /// honest answer to "show me this image" is then that there is none.
    /// </returns>
    Task<Stream?> OpenAsync(string key, CancellationToken cancellationToken);
}
