using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// What a dispatcher sends to change a delivery (FR-22, FR-23).
/// <para>
/// AD-23: every field is an <see cref="Optional{T}"/>, so "leave the driver alone" and "unassign
/// the driver" are two different requests. That distinction is not decoration here — FR-16 makes
/// an unassigned delivery legal, so <c>driverId: null</c> is an operation a dispatcher performs
/// and not a malformed payload, and a plain nullable field could not tell it from an edit that
/// happened to mention only the weight.
/// </para>
/// <para>
/// The status is absent by design. Story 5.3 owns the single write path a status ever changes
/// through, and a field here would be a second one (AD-25).
/// </para>
/// </summary>
/// <param name="Pickup">A new pickup point, or absent. Present-null is refused: a delivery needs one.</param>
/// <param name="Dropoff">A new dropoff point, or absent.</param>
/// <param name="PackageDetails">New package details, or absent.</param>
/// <param name="PackageWeightKg">A new weight, or absent.</param>
/// <param name="DeliveryNotes">New notes, or absent. Present-null clears them.</param>
/// <param name="WindowEarliestAt">A new earliest bound, or absent. Present-null clears it.</param>
/// <param name="WindowLatestAt">A new latest bound, or absent. Present-null clears it.</param>
/// <param name="DriverId">A new driver, or absent. Present-null unassigns (FR-16).</param>
/// <param name="ClientId">A new client, or absent. Present-null clears it.</param>
public sealed record UpdateDeliveryCommand(
    Optional<LocationInput> Pickup,
    Optional<LocationInput> Dropoff,
    Optional<string> PackageDetails,
    Optional<decimal> PackageWeightKg,
    Optional<string?> DeliveryNotes,
    Optional<DateTimeOffset?> WindowEarliestAt,
    Optional<DateTimeOffset?> WindowLatestAt,
    Optional<int?> DriverId,
    Optional<int?> ClientId)
{
    /// <summary>
    /// The command with every absent field filled in from the row it will be applied to: the state
    /// the delivery will hold afterwards.
    /// <para>
    /// AD-23 makes this the thing the validator reads, and the thing the capacity invariant is
    /// judged against. Validating the payload instead would let an update that mentions only the
    /// driver be refused for a package description it never sent, and would check the new driver's
    /// vehicle against a weight of zero.
    /// </para>
    /// <para>
    /// The two locations merge to their stored coordinates and not to their stored address: a
    /// <see cref="LocationInput"/> is coordinates only, and the address is a cache the coordinates
    /// own (DR-11). Nothing here reads it, and the service resets it whenever a point moves.
    /// </para>
    /// </summary>
    /// <param name="delivery">The stored row.</param>
    internal UpdateDeliveryCommand MergedOnto(Delivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        return new UpdateDeliveryCommand(
            Optional<LocationInput>.Present(Pickup.Or(Coordinates(delivery.PickupLocation))),
            Optional<LocationInput>.Present(Dropoff.Or(Coordinates(delivery.DropoffLocation))),
            Optional<string>.Present(PackageDetails.Or(delivery.PackageDetails)),
            Optional<decimal>.Present(PackageWeightKg.Or(delivery.PackageWeightKg)),
            Optional<string?>.Present(DeliveryNotes.Or(delivery.DeliveryNotes)),
            Optional<DateTimeOffset?>.Present(WindowEarliestAt.Or(delivery.WindowEarliestAt)),
            Optional<DateTimeOffset?>.Present(WindowLatestAt.Or(delivery.WindowLatestAt)),
            Optional<int?>.Present(DriverId.Or(delivery.DriverId?.Value)),
            Optional<int?>.Present(ClientId.Or(delivery.ClientId?.Value)));
    }

    private static LocationInput Coordinates(Domain.Common.Location location) =>
        new(location.Latitude, location.Longitude);
}
