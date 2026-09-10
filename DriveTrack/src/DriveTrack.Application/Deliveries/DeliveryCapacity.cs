using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// FR-103's invariant, in one place: a delivery is never left assigned to a driver whose vehicle
/// cannot carry it.
/// <para>
/// The invariant has two sides and either can move. A delivery can gain a driver or gain weight,
/// which is this capability's own doing; on the fleet's side a driver can be handed a smaller
/// vehicle, and the vehicle they already hold can simply be recorded as carrying less — three
/// movers, one rule. AD-24 says an invariant belongs to the capability that owns the data it is
/// about, so both arms live here and the fleet calls in rather than restating the rule: two copies
/// of a rule are two rules the first time one of them is edited.
/// </para>
/// <para>
/// Not a service and not an entry point, exactly like <c>VehicleAssignment</c>: it writes nothing,
/// runs inside the caller's unit of work, and the calling service has already taken AD-3's guard
/// step. The name matters too — <c>GuardCoverageTests</c> scans a type whose name ends in
/// <c>Service</c> or which implements an Application interface whose name does, and this is
/// neither.
/// </para>
/// </summary>
internal static class DeliveryCapacity
{
    /// <summary>
    /// Refuses a delivery whose weight the assigned driver's vehicle cannot carry — the delivery
    /// side of FR-103, called on every create and every update, against the merged weight and the
    /// merged driver.
    /// </summary>
    /// <param name="unitOfWork">The caller's scope.</param>
    /// <param name="driverId">The driver the delivery will be assigned to, or null for none.</param>
    /// <param name="packageWeightKg">The weight the delivery will carry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="DomainRuleException">
    /// The driver holds a vehicle whose capacity is below the weight
    /// (<c>DELIVERY_EXCEEDS_VEHICLE_CAPACITY</c>, 409).
    /// </exception>
    public static async Task EnsureDeliveryFitsAsync(
        IUnitOfWork unitOfWork,
        DriverId? driverId,
        decimal packageWeightKg,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        if (driverId is not { } assigned)
        {
            // An unassigned delivery has no vehicle to exceed. FR-16 makes that a legal state, not
            // a check waiting to happen.
            return;
        }

        var driver = await unitOfWork.Drivers.GetByIdAsync(assigned, cancellationToken);

        // FR-38: a driver holding no vehicle takes no check, and that is not an error. A driver row
        // that has since vanished is not this rule's business either - the caller's own reference
        // check answers that, with a code of its own.
        if (driver?.Vehicle is not { } vehicle || packageWeightKg <= vehicle.CapacityKg)
        {
            return;
        }

        throw Exceeded(packageWeightKg, vehicle.CapacityKg);
    }

    /// <summary>
    /// Refuses a fleet change that would leave one of the driver's active deliveries too heavy for
    /// what they now hold — the fleet side of FR-103, called from <c>VehicleAssignment</c> when a
    /// driver is handed another vehicle, and from <c>VehicleService.UpdateAsync</c> when the
    /// vehicle they already hold is recorded as carrying less.
    /// </summary>
    /// <param name="unitOfWork">The caller's scope.</param>
    /// <param name="driverId">The driver whose vehicle, or whose vehicle's capacity, is changing.</param>
    /// <param name="capacityKg">The capacity they are about to be left with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="DomainRuleException">
    /// An active delivery already assigned to that driver weighs more than the new vehicle can
    /// carry (<c>DELIVERY_EXCEEDS_VEHICLE_CAPACITY</c>, 409).
    /// </exception>
    public static async Task EnsureDriverStillFitsAsync(
        IUnitOfWork unitOfWork,
        DriverId driverId,
        decimal capacityKg,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var heaviest = await unitOfWork.Deliveries.FindHeaviestActiveWeightForDriverAsync(
            driverId,
            cancellationToken);

        if (heaviest is not { } weight || weight <= capacityKg)
        {
            return;
        }

        throw Exceeded(weight, capacityKg);
    }

    /// <summary>
    /// FR-103's "rejection names both figures". NFR-3 gives one wire message per code, so the two
    /// numbers travel in the exception message for the log and reach the dispatcher through the
    /// form instead, which shows the selected driver's capacity beside the weight input.
    /// </summary>
    private static DomainRuleException Exceeded(decimal packageWeightKg, decimal capacityKg) =>
        new(
            ErrorCode.DELIVERY_EXCEEDS_VEHICLE_CAPACITY,
            "A delivery weighing "
                + packageWeightKg.ToString(CultureInfo.InvariantCulture)
                + " kg cannot be carried by a vehicle whose capacity is "
                + capacityKg.ToString(CultureInfo.InvariantCulture) + " kg.");
}
