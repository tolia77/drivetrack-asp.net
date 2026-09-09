namespace DriveTrack.Application.Vehicles;

/// <summary>
/// What a dispatcher sends to put a vehicle on the fleet (FR-40): model, licence plate, capacity,
/// mileage and the next maintenance date.
/// <para>
/// The text fields are nullable because the wire can send anything and the command has to be able
/// to carry it as far as the validator; nothing downstream reads a field the validator has not
/// proved.
/// </para>
/// </summary>
/// <param name="Model">Make and model.</param>
/// <param name="LicensePlate">The registration plate. Trimmed and unique, with no format rule — the original's exact-eight-character rule is deliberately not carried forward.</param>
/// <param name="CapacityKg">Payload capacity in kilograms.</param>
/// <param name="Mileage">Odometer reading in kilometres.</param>
/// <param name="NextMaintenanceDate">When servicing is next due, or null when none is recorded.</param>
public sealed record CreateVehicleCommand(
    string? Model,
    string? LicensePlate,
    decimal CapacityKg,
    int Mileage,
    DateOnly? NextMaintenanceDate);
