using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using FluentValidation;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// One artefact on its way into the store: what it is, what it claims to be, how big it claims to
/// be, and the bytes themselves.
/// <para>
/// <see cref="Content"/> is a <see cref="Stream"/> and not a byte array, so a five-megabyte
/// photograph is not copied onto the large-object heap on its way through three layers. It is an
/// <em>ASP.NET-free</em> stream: <c>IFormFile</c> and <c>IBrowserFile</c> stop at the adapter, which
/// is what keeps this command — and everything below it — free of a web framework (AD-1).
/// </para>
/// <para>
/// <see cref="Length"/> is declared rather than measured, because the streams this carries are
/// forward-only and a validator that measured one would consume the bytes the store is about to
/// write. Both adapters know the length before they open the stream, and NFR-28's check has to run
/// before the first byte reaches the store rather than after.
/// </para>
/// <para>
/// Because a stream is consumed by being read, a command is single-use: it is a message, not a value
/// to keep. Nothing retries one.
/// </para>
/// </summary>
/// <param name="Kind">Whether this is the signature or a photograph (FR-119).</param>
/// <param name="ContentType">The MIME type the caller declared, or null when it declared none.</param>
/// <param name="Length">The length in bytes the caller declared.</param>
/// <param name="Content">The bytes. The caller owns the stream and disposes it.</param>
public sealed record ProofAssetUpload(
    ProofAssetKind Kind,
    string? ContentType,
    long Length,
    Stream Content);

/// <summary>
/// A request to record that a parcel changed hands (FR-119).
/// <para>
/// There is no delivery id here: the id is the route, as it is for every other single-row operation
/// in this capability. There is no captured-at either — AD-13's clock supplies it, and a caller who
/// could name the instant could name any instant, which is the one thing evidence must not permit.
/// </para>
/// <para>
/// The coordinates arrive as a <see cref="LocationInput"/> and have no fallback: FR-119 records
/// where the hand-over happened, read from the device rather than typed, and inventing a substitute —
/// the dropoff point, say — would record a place the capture did not happen.
/// </para>
/// </summary>
/// <param name="RecipientName">Who took the parcel at the door.</param>
/// <param name="CaptureLocation">Where the hand-over happened.</param>
/// <param name="Assets">The signature and the photographs, in any order.</param>
public sealed record CaptureProofCommand(
    string? RecipientName,
    LocationInput? CaptureLocation,
    IReadOnlyList<ProofAssetUpload>? Assets);

/// <summary>
/// FR-119 and NFR-28's rules for a capture, applied before a single byte reaches the store.
/// <para>
/// Every rule here is one nothing downstream can undo. The capture commits its row before it writes
/// a single byte, so a set refused halfway through the writing would leave a committed proof naming
/// objects that were never stored — and <c>IAssetStore</c> has neither a delete to take an object
/// back with nor a way to un-commit a row. That is why the shape of the whole set is judged here, in
/// one pass, before anything is minted or opened, rather than asset by asset as they are uploaded.
/// </para>
/// <para>
/// Two failures carry their own message key rather than the generic one, because they are the two a
/// caller can act on without guessing: the picture is the wrong sort of file, or it is too big.
/// Both still leave as <c>COMMON_VALIDATION_FAILED</c> at 422 — the key rides in
/// <c>error.fields</c>, which is where NFR-4 puts the per-field explanation.
/// </para>
/// </summary>
public sealed class CaptureProofCommandValidator : AbstractValidator<CaptureProofCommand>
{
    /// <summary>Matching the <c>proof_of_deliveries.recipient_name</c> column.</summary>
    public const int RecipientNameMaximumLength = 200;

    /// <summary>Declares the rules.</summary>
    public CaptureProofCommandValidator()
    {
        RuleFor(command => command.RecipientName)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(RecipientNameMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(command => command.CaptureLocation)
            .NotNull().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        // A separate rule rather than a chained SetValidator, for the reason the delivery's own two
        // locations use: FluentValidation skips a child validator for a null property, so a missing
        // location is reported once, above, rather than twice by two rules that disagree about
        // whose job it was.
        RuleFor(command => command.CaptureLocation!)
            .SetValidator(new LocationInputValidator())
            .OverridePropertyName(nameof(CaptureProofCommand.CaptureLocation));

        // FR-119 reads as one sentence - a signature and at least one photograph - and it is three
        // rules because the three refusals are different sentences to whoever is standing at a door
        // holding a phone.
        RuleFor(command => command.Assets)
            .NotNull().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .Must(assets => Count(assets, ProofAssetKind.Signature) == 1)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .Must(assets => Count(assets, ProofAssetKind.Photo) >= 1)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .Must(assets => Count(assets, ProofAssetKind.Photo) <= ProofAssetRules.MaximumPhotos)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        // Per asset, so the field path names which upload was wrong rather than reporting that
        // something among six of them was.
        RuleForEach(command => command.Assets)
            .Must(asset => ProofAssetRules.IsAllowedContentType(asset.ContentType))
                .WithMessage(nameof(ErrorCode.DELIVERY_PROOF_ASSET_TYPE_NOT_ALLOWED))
            .Must(asset => ProofAssetRules.IsAllowedLength(asset.Length))
                .WithMessage(nameof(ErrorCode.DELIVERY_PROOF_ASSET_TOO_LARGE));
    }

    /// <summary>
    /// How many assets of a kind a command carries. Null counts as none rather than throwing: the
    /// <c>NotNull</c> above already reports an absent set, and a second rule raising on the same
    /// absence would turn a 422 into a 500.
    /// </summary>
    private static int Count(IReadOnlyList<ProofAssetUpload>? assets, ProofAssetKind kind) =>
        assets?.Count(asset => asset.Kind == kind) ?? 0;
}
