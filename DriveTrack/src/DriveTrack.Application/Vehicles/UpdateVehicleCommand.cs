using DriveTrack.Application.Common;
using DriveTrack.Domain.Vehicles;

namespace DriveTrack.Application.Vehicles;

/// <summary>
/// What a dispatcher sends to change a vehicle (FR-42).
/// <para>
/// AD-23: every field is an <see cref="Optional{T}"/>, so "leave the plate alone" and "send the
/// plate" are two different requests.
/// </para>
/// <para>
/// <see cref="NextMaintenanceDate"/> is the one field here where a present-null is a genuine clear:
/// <c>next_maintenance_date</c> is nullable on the row, so sending it as null means "no servicing
/// is scheduled". For the other four a present-null is not a clear but a value the validator
/// refuses, which is one more reason the merge below feeds the validator rather than the payload.
/// </para>
/// </summary>
/// <param name="Model">Make and model.</param>
/// <param name="LicensePlate">The registration plate.</param>
/// <param name="CapacityKg">Payload capacity in kilograms.</param>
/// <param name="Mileage">Odometer reading in kilometres.</param>
/// <param name="NextMaintenanceDate">When servicing is next due. Present-and-null clears it.</param>
public sealed record UpdateVehicleCommand(
    Optional<string> Model,
    Optional<string> LicensePlate,
    Optional<decimal> CapacityKg,
    Optional<int> Mileage,
    Optional<DateOnly?> NextMaintenanceDate)
{
    /// <summary>
    /// The command with every absent field filled in from the row it will be applied to: the state
    /// the vehicle will hold afterwards.
    /// <para>
    /// AD-23 makes this the thing the validator reads. Validating the payload instead would let an
    /// update that omits the model be judged as though the model were empty, and refuse a change to
    /// the capacity because of a field the caller never mentioned.
    /// </para>
    /// </summary>
    /// <param name="vehicle">The stored row.</param>
    internal UpdateVehicleCommand MergedOnto(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        return new UpdateVehicleCommand(
            Optional<string>.Present(Model.Or(vehicle.Model)),
            Optional<string>.Present(LicensePlate.Or(vehicle.LicensePlate)),
            Optional<decimal>.Present(CapacityKg.Or(vehicle.CapacityKg)),
            Optional<int>.Present(Mileage.Or(vehicle.Mileage)),
            Optional<DateOnly?>.Present(NextMaintenanceDate.Or(vehicle.NextMaintenanceDate)));
    }
}
