namespace DriveTrack.Application.Deliveries;

/// <summary>
/// What a proof's artefacts may be (NFR-28), in one place.
/// <para>
/// The figures are read by three different callers — the validator that refuses a bad upload, the
/// REST adapter that sizes its request limits, and the capture screen that opens a browser file at a
/// bounded length — and a limit stated three times is three limits waiting to disagree. The adapter
/// sizing its body cap from a number the validator does not use would refuse a legal photograph with
/// a framework 413 carrying no contract code at all, which is the specific failure this class exists
/// to prevent.
/// </para>
/// <para>
/// The type list is an allowlist rather than a denylist, and it is short on purpose: these are the
/// three raster formats every camera and every browser can produce and every browser can render.
/// Anything else — a PDF, an HEIC, an SVG whose script runs in whoever opens it — is refused before
/// a single byte reaches the store.
/// </para>
/// </summary>
public static class ProofAssetRules
{
    /// <summary>The MIME types a proof asset may declare.</summary>
    public static readonly IReadOnlySet<string> AllowedContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/png",
            "image/jpeg",
            "image/webp",
        };

    /// <summary>
    /// The largest a single asset may be: five mebibytes, which is a generous phone photograph and
    /// an absurd signature. Per asset rather than per capture, because the failure a caller can act
    /// on is "this picture is too big" and not "these six pictures are together too big".
    /// </summary>
    public const long MaximumAssetBytes = 5L * 1024 * 1024;

    /// <summary>
    /// How many photographs one proof may carry. FR-119 asks for at least one; the ceiling is here
    /// so a capture cannot become an unbounded upload, and five is more angles than a doorstep has.
    /// </summary>
    public const int MaximumPhotos = 5;

    /// <summary>
    /// The largest a whole capture request may be, which is what the REST adapter caps its body at.
    /// The signature plus the most photographs allowed, and a little room for the multipart frame
    /// and the text fields around them.
    /// </summary>
    public const long MaximumRequestBytes = MaximumAssetBytes * (MaximumPhotos + 1) + (64 * 1024);

    /// <summary>Whether a declared content type is one a proof asset may carry.</summary>
    /// <param name="contentType">The type as the caller declared it, or null when it declared none.</param>
    public static bool IsAllowedContentType(string? contentType) =>
        contentType is not null && AllowedContentTypes.Contains(contentType.Trim());

    /// <summary>
    /// Whether a declared length is one a proof asset may have: present, above zero and within
    /// <see cref="MaximumAssetBytes"/>. Zero is refused as well as excess — an empty file is not
    /// evidence of anything, and it is what a browser sends for a control nobody filled in.
    /// </summary>
    /// <param name="length">The length in bytes.</param>
    public static bool IsAllowedLength(long length) => length is > 0 and <= MaximumAssetBytes;
}
