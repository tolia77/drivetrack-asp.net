using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// One stored artefact as a caller reads it: an id to fetch it by, what it is, and what it will
/// arrive as.
/// <para>
/// The id, and nothing that resembles a location. DR-14 keeps the storage key inside the system, so
/// this carries no key, no path and no URL — a reader asks the authenticated asset route for the id
/// and the capability decides, every time, whether this caller may have the bytes.
/// </para>
/// </summary>
/// <param name="Id">The asset row's id, which the asset route takes.</param>
/// <param name="Kind">Whether this is the signature or a photograph.</param>
/// <param name="ContentType">What the bytes will arrive as, so a client can choose how to show them.</param>
public sealed record ProofAssetView(int Id, ProofAssetKind Kind, string ContentType);

/// <summary>
/// A captured proof as a caller reads it (FR-122).
/// <para>
/// <see cref="CapturedByName"/> is the AD-17 decision, taken in the service's mapping step against
/// the caller and nowhere else: dispatch is told who captured the proof, and a client is told that
/// it was captured. A client learning the assigned driver's name through the evidence would be the
/// same disclosure FR-96 closes on the delivery itself, arriving through a different door — so the
/// field is null for every role but administrator and dispatcher, and null means withheld rather
/// than missing.
/// </para>
/// </summary>
/// <param name="DeliveryId">The delivery this proves.</param>
/// <param name="RecipientName">Who took the parcel at the door.</param>
/// <param name="CaptureLocation">Where the hand-over happened, as every screen reads a point.</param>
/// <param name="CapturedAt">When it happened, at offset zero (AD-13).</param>
/// <param name="CapturedByName">The capturer's display name, or null when this caller may not be told.</param>
/// <param name="Assets">The signature and the photographs, signature first.</param>
public sealed record ProofOfDeliveryView(
    int DeliveryId,
    string RecipientName,
    LocationView CaptureLocation,
    DateTimeOffset CapturedAt,
    string? CapturedByName,
    IReadOnlyList<ProofAssetView> Assets);

/// <summary>
/// One asset's bytes, on their way out to whoever asked for them and was allowed to.
/// <para>
/// A stream rather than an array, so an image is written to the response as it is read from the
/// store instead of being held whole in memory first. The caller disposes it, which for the asset
/// route means the framework does once the response has been written.
/// </para>
/// </summary>
/// <param name="ContentType">The stored MIME type, so the response needs no sniffing.</param>
/// <param name="Content">The bytes. The caller disposes the stream.</param>
public sealed record ProofAssetContent(string ContentType, Stream Content);
