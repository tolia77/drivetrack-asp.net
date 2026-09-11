using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Tests.Deliveries;

/// <summary>
/// The validation rows of story 6.1's edge-case matrix, asserted against
/// <see cref="CaptureProofCommandValidator"/>.
/// <para>
/// Every claim here is one AD-26 makes expensive to get wrong. The store is written before the
/// transaction that names the keys, so a rule that failed to refuse an upload would let bytes reach
/// the bucket that no row will ever reference — and a rule that refused a legal one would leave a
/// driver standing at a door unable to record that the parcel arrived. Both are decided by this
/// class, before a single byte moves, which is why they are asserted here rather than over HTTP.
/// </para>
/// </summary>
public class ProofValidationTests
{
    private static readonly LocationInput Kyiv = new(50.4501, 30.5234);

    [Fact]
    public void The_minimum_capture_is_valid()
    {
        // FR-119's whole sentence: a recipient, a place, a signature and one photograph.
        Assert.Empty(Failures(Capture()));
    }

    [Fact]
    public void A_capture_carrying_the_most_photographs_allowed_is_valid()
    {
        // The ceiling is inclusive, and saying so is what stops an off-by-one turning the fifth
        // legal photograph into a refusal nobody can explain.
        Assert.Empty(Failures(Capture(photos: ProofAssetRules.MaximumPhotos)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_capture_with_no_recipient_is_refused_naming_the_field(string? recipient)
    {
        // FR-119 records who took the parcel. A blank name is a NOT NULL column holding a space,
        // which renders as absent and filters as present - worse than either.
        Assert.Contains(
            nameof(CaptureProofCommand.RecipientName),
            Failures(Capture(recipient: recipient)));
    }

    [Fact]
    public void A_recipient_name_wider_than_the_column_is_refused_before_the_commit()
    {
        Assert.Empty(Failures(Capture(
            recipient: new string('я', CaptureProofCommandValidator.RecipientNameMaximumLength))));

        Assert.Contains(
            nameof(CaptureProofCommand.RecipientName),
            Failures(Capture(
                recipient: new string(
                    'я',
                    CaptureProofCommandValidator.RecipientNameMaximumLength + 1))));
    }

    [Fact]
    public void A_capture_with_no_coordinates_is_refused_naming_the_location()
    {
        // The columns are NOT NULL and FR-119 says the point is recorded rather than typed, so a
        // browser that refused geolocation cannot capture. There is deliberately no fallback: the
        // dropoff point would record a place the hand-over did not happen.
        Assert.Contains(Failures(Capture(location: new LocationInput(null, null))), NamesTheLocation);

        // And the absent pair, which is what a caller who sent no location object at all produces.
        // Built directly rather than through the helper, whose null argument means "take the
        // default" - an omission the helper cannot express is one this claim needs.
        Assert.Contains(
            Failures(new CaptureProofCommand(
                "Олена Петренко",
                null,
                [
                    new ProofAssetUpload(ProofAssetKind.Signature, "image/png", 2048, Stream.Null),
                    new ProofAssetUpload(ProofAssetKind.Photo, "image/jpeg", 1024, Stream.Null),
                ])),
            NamesTheLocation);
    }

    [Fact]
    public void A_capture_whose_coordinates_are_off_the_map_is_refused()
    {
        // The same range rule every other location in the system is held to, reached through the
        // shared child validator rather than restated here.
        Assert.Contains(Failures(Capture(location: new LocationInput(91, 30.5234))), NamesTheLocation);
    }

    /// <summary>
    /// Whether a field name is about the capture point. The child validator reports
    /// <c>CaptureLocation.Latitude</c> and the presence rule reports <c>CaptureLocation</c>, and
    /// both are the same answer to a caller: the place is missing or impossible.
    /// </summary>
    private static bool NamesTheLocation(string field) =>
        field.StartsWith(nameof(CaptureProofCommand.CaptureLocation), StringComparison.Ordinal);

    [Fact]
    public void A_capture_with_no_signature_is_refused()
    {
        Assert.Contains(nameof(CaptureProofCommand.Assets), Failures(Capture(signatures: 0)));
    }

    [Fact]
    public void A_capture_carrying_two_signatures_is_refused()
    {
        // "Exactly one", not "at least one": the proof view shows the signature, singular, and two
        // would leave a reader deciding which one the recipient actually wrote.
        Assert.Contains(nameof(CaptureProofCommand.Assets), Failures(Capture(signatures: 2)));
    }

    [Fact]
    public void A_capture_with_no_photograph_is_refused()
    {
        Assert.Contains(nameof(CaptureProofCommand.Assets), Failures(Capture(photos: 0)));
    }

    [Fact]
    public void A_capture_carrying_more_photographs_than_allowed_is_refused()
    {
        Assert.Contains(
            nameof(CaptureProofCommand.Assets),
            Failures(Capture(photos: ProofAssetRules.MaximumPhotos + 1)));
    }

    [Fact]
    public void A_capture_with_no_assets_at_all_is_refused_rather_than_thrown_on()
    {
        // A null collection is reachable from the wire, and the three shape rules all read it. The
        // claim is that they report it instead of raising - a NullReferenceException here would be
        // a 500 for a request the contract answers with a 422.
        Assert.Contains(
            nameof(CaptureProofCommand.Assets),
            Failures(new CaptureProofCommand("Олена Петренко", Kyiv, null)));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/svg+xml")]
    [InlineData("text/plain")]
    [InlineData(null)]
    public void An_asset_of_a_type_a_proof_may_not_carry_is_refused_with_its_own_key(string? type)
    {
        // NFR-28. The key rides in error.fields, which is where NFR-4 puts the per-field
        // explanation, so a driver is told the file was the wrong sort rather than that something
        // about the request was.
        Assert.Contains(
            nameof(ErrorCode.DELIVERY_PROOF_ASSET_TYPE_NOT_ALLOWED),
            Keys(Capture(photoContentType: type)));
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    [InlineData("IMAGE/PNG")]
    public void The_three_allowed_types_are_accepted_however_they_are_spelled(string type)
    {
        // Case-insensitively, because a MIME type is case-insensitive by its own specification and
        // some clients shout. A rule that only accepted the lower-case spelling would refuse a
        // legal photograph for a reason nobody could see.
        Assert.Empty(Failures(Capture(photoContentType: type)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(ProofAssetRules.MaximumAssetBytes + 1)]
    public void An_asset_that_is_empty_or_oversized_is_refused_with_its_own_key(long length)
    {
        // Both ends. Zero is what a browser sends for a control nobody filled in, and an empty file
        // is not evidence of anything; the ceiling is NFR-28's.
        Assert.Contains(
            nameof(ErrorCode.DELIVERY_PROOF_ASSET_TOO_LARGE),
            Keys(Capture(photoLength: length)));
    }

    [Fact]
    public void An_asset_exactly_at_the_cap_is_accepted()
    {
        Assert.Empty(Failures(Capture(photoLength: ProofAssetRules.MaximumAssetBytes)));
    }

    /// <summary>The offending property names of a capture, deduplicated.</summary>
    private static string[] Failures(CaptureProofCommand command) =>
    [
        .. new CaptureProofCommandValidator()
            .Validate(command)
            .Errors
            .Select(failure => failure.PropertyName)
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The message keys of a capture's failures. The two asset rules are the only ones in this
    /// capability that carry a key of their own, and the key is the half a field name cannot show.
    /// </summary>
    private static string[] Keys(CaptureProofCommand command) =>
    [
        .. new CaptureProofCommandValidator()
            .Validate(command)
            .Errors
            .Select(failure => failure.ErrorMessage)
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    /// A capture built from the legal shape, with exactly one thing changed by each test.
    /// </summary>
    /// <remarks>
    /// The streams are <see cref="Stream.Null"/> throughout. Nothing here reads a byte - the
    /// validator judges the declared length and the declared type, which is the whole point of both
    /// being declared: NFR-28's check has to run before the store is touched, and measuring a
    /// forward-only stream would consume the bytes the store is about to write.
    /// </remarks>
    private static CaptureProofCommand Capture(
        string? recipient = "Олена Петренко",
        LocationInput? location = null,
        int signatures = 1,
        int photos = 1,
        string? photoContentType = "image/jpeg",
        long photoLength = 1024)
    {
        var assets = new List<ProofAssetUpload>();

        for (var index = 0; index < signatures; index++)
        {
            assets.Add(new ProofAssetUpload(
                ProofAssetKind.Signature,
                "image/png",
                2048,
                Stream.Null));
        }

        for (var index = 0; index < photos; index++)
        {
            assets.Add(new ProofAssetUpload(
                ProofAssetKind.Photo,
                photoContentType,
                photoLength,
                Stream.Null));
        }

        return new CaptureProofCommand(recipient, location ?? Kyiv, assets);
    }
}
