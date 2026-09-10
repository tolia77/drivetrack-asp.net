using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// A party to a delivery as a dispatch screen reads one: the row id it is addressed by, and the
/// name a person recognises.
/// <para>
/// Flattened to two scalars rather than nesting a driver or a client DTO, for the reason
/// <c>DriverSummary</c> flattens its vehicle: this is what a table cell shows and what the
/// browser-side filter runs over, so it travels as text.
/// </para>
/// </summary>
/// <param name="Id">The driver row's or client row's own id.</param>
/// <param name="Name">The party's display name, taken from the account behind the row.</param>
public sealed record DeliveryParty(int Id, string Name);

/// <summary>
/// A delivery as a dispatcher or an admin sees one (FR-18 to FR-24).
/// <para>
/// A null <see cref="Driver"/> or <see cref="Client"/> means unassigned and only that (FR-16). It
/// never means "withheld from you": a role that may not see a party gets
/// <see cref="AssignedDeliverySummary"/>, which has no party fields at all (AD-17). That split is
/// what makes FR-27 and FR-96 impossible to regress by editing a mapping.
/// </para>
/// <para>
/// <see cref="IsOverdue"/> is derived here from the window and the injected clock and is stored
/// nowhere (FR-19): a column would be a second answer that goes stale the moment the clock moves,
/// and "overdue" is not a status the lifecycle has.
/// </para>
/// </summary>
/// <param name="Id">The delivery row's id.</param>
/// <param name="Driver">The assigned driver, or null when none is assigned.</param>
/// <param name="Client">The requesting client, or null when there is none.</param>
/// <param name="Pickup">Where the parcel is collected.</param>
/// <param name="Dropoff">Where the parcel is delivered.</param>
/// <param name="PackageDetails">What is being moved.</param>
/// <param name="PackageWeightKg">Weight in kilograms, the same unit a vehicle's capacity uses.</param>
/// <param name="DeliveryNotes">Free-text instructions, or null.</param>
/// <param name="WindowEarliestAt">Earliest acceptable arrival, or null.</param>
/// <param name="WindowLatestAt">Latest acceptable arrival, or null.</param>
/// <param name="Status">Where the delivery is in its lifecycle (FR-30).</param>
/// <param name="CreatedAt">When it was created, at offset zero (AD-13).</param>
/// <param name="IsOverdue">Derived: the latest bound has passed and the parcel is not delivered.</param>
public sealed record DeliverySummary(
    int Id,
    DeliveryParty? Driver,
    DeliveryParty? Client,
    LocationView Pickup,
    LocationView Dropoff,
    string PackageDetails,
    decimal PackageWeightKg,
    string? DeliveryNotes,
    DateTimeOffset? WindowEarliestAt,
    DateTimeOffset? WindowLatestAt,
    DeliveryStatus Status,
    DateTimeOffset CreatedAt,
    bool IsOverdue);
