using System.Globalization;
using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Domain.Deliveries;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DriveTrack.Web.Api;

/// <summary>
/// A capture as a multipart request sends one, and the one place <c>IFormFile</c> is named.
/// <para>
/// A proof carries image bytes, so the request is <c>multipart/form-data</c> rather than JSON — a
/// base64 photograph in a JSON body is a third larger and has to be held whole in memory twice. That
/// makes the bound model an ASP.NET shape, and AD-1 keeps ASP.NET shapes out of Application: this
/// type exists so that <see cref="ToCommand"/> is the boundary where an <c>IFormFile</c> becomes a
/// <see cref="Stream"/> and a content type, and nothing below this folder can name one.
/// </para>
/// <para>
/// Every field is nullable and nothing is validated here. AD-9 puts validation in
/// <c>CaptureProofCommandValidator</c>, called by the capability, so a missing recipient and a
/// missing signature are refused by the same code path whether they arrived over REST or from the
/// capture screen — and the two suppressions in <c>Program.cs</c> mean an unbound field reaches the
/// service as a null rather than as a framework 400 in a shape this contract does not define.
/// </para>
/// </summary>
public sealed class ProofCaptureForm
{
    /// <summary>Who took the parcel at the door.</summary>
    [FromForm]
    public string? RecipientName { get; set; }

    /// <summary>Latitude of the capture point, in decimal degrees.</summary>
    /// <remarks>
    /// Bound as text and parsed invariantly below, which is not fussiness. A form value is bound
    /// under <see cref="CultureInfo.CurrentCulture"/>, and NFR-15 makes that <c>uk-UA</c> — a
    /// culture whose decimal separator is a comma. Bound as a <c>double?</c> the perfectly ordinary
    /// <c>50.4501</c> every client on earth sends would fail to parse, the suppressed model state
    /// would swallow the failure, and the capture would be refused for a missing coordinate the
    /// caller plainly supplied.
    /// <para>
    /// The string stays nullable so an omitted coordinate is absent rather than zero: <c>0, 0</c> is
    /// a real point in the Atlantic, and a request that forgot its longitude must not become one.
    /// </para>
    /// </remarks>
    [FromForm]
    public string? Latitude { get; set; }

    /// <inheritdoc cref="Latitude" />
    [FromForm]
    public string? Longitude { get; set; }

    /// <summary>The signature image (FR-119). Exactly one; the validator refuses none and refuses two.</summary>
    [FromForm]
    public IFormFile? Signature { get; set; }

    /// <summary>The photographs (FR-119). At least one, and at most <c>ProofAssetRules.MaximumPhotos</c>.</summary>
    [FromForm]
    public IFormFileCollection? Photos { get; set; }

    /// <summary>
    /// The same request as the capability's own command: streams, a declared length and a declared
    /// content type, and no web framework anywhere in it.
    /// </summary>
    /// <remarks>
    /// <c>OpenReadStream</c> on an <c>IFormFile</c> reads from the buffered request body, which
    /// ASP.NET Core has already spooled to disk if it was large — so nothing here loads a photograph
    /// into memory, and the streams stay valid for as long as the request does.
    /// <para>
    /// The lengths and types are the ones the request declared, not ones measured here. NFR-28's
    /// check has to run before the first byte reaches the store, and measuring a forward-only stream
    /// would consume the bytes the store is about to write.
    /// </para>
    /// </remarks>
    public CaptureProofCommand ToCommand()
    {
        var assets = new List<ProofAssetUpload>();

        if (Signature is { } signature)
        {
            assets.Add(Upload(ProofAssetKind.Signature, signature));
        }

        foreach (var photo in Photos ?? (IReadOnlyList<IFormFile>)[])
        {
            assets.Add(Upload(ProofAssetKind.Photo, photo));
        }

        return new CaptureProofCommand(
            RecipientName,
            new LocationInput(Coordinate(Latitude), Coordinate(Longitude)),
            assets);
    }

    private static ProofAssetUpload Upload(ProofAssetKind kind, IFormFile file) =>
        new(kind, file.ContentType, file.Length, file.OpenReadStream());

    /// <summary>
    /// A coordinate as the wire spells one: a decimal point, whatever the server's culture is.
    /// </summary>
    /// <remarks>
    /// A value that will not parse answers null rather than throwing, and null is what an omitted
    /// coordinate is - so <c>LocationInputValidator</c> refuses both with the same 422 naming the
    /// field. An exception here would be a 500 for a request the contract has a 422 for.
    /// </remarks>
    private static double? Coordinate(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
