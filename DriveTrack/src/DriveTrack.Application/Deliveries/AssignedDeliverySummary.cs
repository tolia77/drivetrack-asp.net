using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// A delivery as the driver carrying it or the client who requested it sees one (FR-25, FR-27,
/// FR-96): everything <see cref="DeliverySummary"/> carries, minus both parties.
/// <para>
/// AD-17's rule is that a role which sees less gets a distinct DTO rather than a nulled field on a
/// shared one, and this is the case that shows why. A delivery legitimately has no driver and no
/// client (FR-16), so a null <c>Driver</c> on a shared summary would have to mean both "nobody is
/// assigned" and "you may not be told who is" — and the day someone maps the shared type for a
/// driver's screen, a client's name ships with it and nothing fails. Here there is no field to
/// fill: a counterparty's identity cannot reach a driver or a client through this type at all.
/// </para>
/// </summary>
/// <param name="Id">The delivery row's id.</param>
/// <param name="Pickup">Where the parcel is collected.</param>
/// <param name="Dropoff">Where the parcel is delivered.</param>
/// <param name="PackageDetails">What is being moved.</param>
/// <param name="PackageWeightKg">Weight in kilograms.</param>
/// <param name="DeliveryNotes">Free-text instructions, or null.</param>
/// <param name="WindowEarliestAt">Earliest acceptable arrival, or null.</param>
/// <param name="WindowLatestAt">Latest acceptable arrival, or null.</param>
/// <param name="Status">Where the delivery is in its lifecycle (FR-30).</param>
/// <param name="CreatedAt">When it was created, at offset zero (AD-13).</param>
/// <param name="IsOverdue">Derived from the window and the injected clock; stored nowhere.</param>
public sealed record AssignedDeliverySummary(
    int Id,
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
