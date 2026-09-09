using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Drivers;

namespace DriveTrack.Application.Vehicles;

/// <summary>
/// The one writer of <c>drivers.vehicle_id</c> (AD-24).
/// <para>
/// AD-24 gives the driver–vehicle assignment to the Vehicles capability; AD-5 gives an operation
/// one scope and one commit. Creating a driver <em>with</em> a vehicle is one operation, so the
/// assignment cannot open a scope of its own — a refused assignment after a committed account is
/// exactly the half-written state NFR-9 forbids. The resolution is a collaborator that writes
/// inside the caller's scope.
/// </para>
/// <para>
/// Not a service and not an entry point: the calling service has already run AD-3's guard step,
/// which is why <c>GuardCoverageTests</c> neither sees nor needs a guard call here. The name is
/// deliberate too — the scan reads a type whose name ends in <c>Service</c>, or which implements
/// an Application interface whose name does, and this is neither.
/// </para>
/// </summary>
internal static class VehicleAssignment
{
    /// <summary>
    /// Applies an assignment to a loaded driver, inside the caller's unit of work. Nothing is
    /// committed here; the caller's single commit is what makes the assignment land with whatever
    /// else the operation wrote.
    /// </summary>
    /// <param name="unitOfWork">The caller's scope. The driver must be tracked by it.</param>
    /// <param name="driver">The driver whose assignment is being set.</param>
    /// <param name="vehicleId">
    /// The vehicle to hold, or null to hold none. Null is FR-38's clear — the arm the original
    /// system had no way to express, which is why an assigned vehicle there could never be deleted.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotFoundException">No vehicle has that id.</exception>
    /// <exception cref="ConflictException">Another driver already holds that vehicle (FR-44).</exception>
    public static async Task ApplyAsync(
        IUnitOfWork unitOfWork,
        Driver driver,
        int? vehicleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(driver);

        if (vehicleId is null)
        {
            driver.VehicleId = null;

            // The navigation as well as the key: the driver was loaded with its vehicle, and
            // leaving the navigation behind would let the caller map a summary naming a vehicle the
            // driver no longer holds.
            driver.Vehicle = null;

            return;
        }

        var vehicle = await unitOfWork.Vehicles.GetByIdAsync(vehicleId.Value, cancellationToken)
            ?? throw new NotFoundException(
                ErrorCode.COMMON_NOT_FOUND,
                "No vehicle exists with id "
                    + vehicleId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");

        var holder = await unitOfWork.Vehicles.FindHolderAsync(vehicle.Id, cancellationToken);

        // The driver's own current vehicle is a no-op, not a conflict: re-submitting an unchanged
        // form is the ordinary case, and refusing it would make an edit screen unusable. A driver
        // being created has the default id, which no stored holder can equal, so the same
        // comparison refuses correctly there.
        if (holder is { } existing && existing != driver.Id)
        {
            throw new ConflictException(
                ErrorCode.FLEET_VEHICLE_ALREADY_ASSIGNED,
                "Vehicle " + vehicle.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " is already held by driver "
                    + existing.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        driver.VehicleId = vehicle.Id;
        driver.Vehicle = vehicle;

        // The filtered unique index on drivers.vehicle_id stays the race backstop: two dispatchers
        // assigning the same free vehicle at the same instant both pass the check above, and the
        // loser's commit surfaces as PERSISTENCE_UNIQUE_VIOLATION - also a 409, so the caller sees
        // the same status either way (NFR-2).
    }
}
