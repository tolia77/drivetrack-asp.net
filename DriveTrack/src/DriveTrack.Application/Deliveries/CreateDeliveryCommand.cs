using DriveTrack.Application.Common;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// What a dispatcher sends to open a delivery (FR-14 to FR-17).
/// <para>
/// Not partial, and deliberately: a delivery that does not exist yet has no stored state to merge
/// onto, so every field is plainly nullable and the validator decides which of them may stay null.
/// <see cref="UpdateDeliveryCommand"/> is where <see cref="Optional{T}"/> earns its keep.
/// </para>
/// <para>
/// Four fields are optional by requirement rather than by omission: a delivery with no driver, no
/// client, no notes and no window is a valid delivery (FR-16), which is why the driver and client
/// ids are <c>int?</c> and not required. The status is not here at all — a new delivery is
/// <c>Pending</c> and can never be created in a completed state (FR-30), so it is not a value a
/// caller gets to send.
/// </para>
/// </summary>
/// <param name="Pickup">Where the parcel is collected. Required.</param>
/// <param name="Dropoff">Where the parcel is delivered. Required.</param>
/// <param name="PackageDetails">What is being moved. Required.</param>
/// <param name="PackageWeightKg">Weight in kilograms; must be positive (FR-102).</param>
/// <param name="DeliveryNotes">Free-text instructions, or null.</param>
/// <param name="WindowEarliestAt">Earliest acceptable arrival, or null. Either bound may stand alone.</param>
/// <param name="WindowLatestAt">Latest acceptable arrival, or null.</param>
/// <param name="DriverId">The driver to assign, or null for none (FR-16).</param>
/// <param name="ClientId">The requesting client, or null for none.</param>
public sealed record CreateDeliveryCommand(
    LocationInput? Pickup,
    LocationInput? Dropoff,
    string? PackageDetails,
    decimal PackageWeightKg,
    string? DeliveryNotes,
    DateTimeOffset? WindowEarliestAt,
    DateTimeOffset? WindowLatestAt,
    int? DriverId,
    int? ClientId);
