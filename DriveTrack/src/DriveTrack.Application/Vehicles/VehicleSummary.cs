namespace DriveTrack.Application.Vehicles;

/// <summary>
/// A vehicle as a caller sees it (FR-40). A DTO, so <c>Vehicle</c> and its EF navigations never
/// bind to a component parameter or serialize (AD-17).
/// <para>
/// <see cref="Mileage"/> and <see cref="NextMaintenanceDate"/> are carried even though no logic in
/// this milestone reads them. FR-40 and FR-42 make them part of what a dispatcher records, and PRD
/// section 8 excludes the maintenance <em>alerting</em> feature rather than the fields: they are
/// "captured and never read", which is only true if a create path can set them. A field no request
/// can write is not knowingly-dead data, it is a permanently null column.
/// </para>
/// </summary>
/// <param name="Id">The vehicle row's id.</param>
/// <param name="Model">Make and model as displayed to a dispatcher.</param>
/// <param name="LicensePlate">The registration plate, unique across the fleet (FR-41).</param>
/// <param name="CapacityKg">Payload capacity. The unit is in the name (DR-17).</param>
/// <param name="Mileage">Odometer reading, in kilometres.</param>
/// <param name="NextMaintenanceDate">
/// When servicing is next due, or null when no date is recorded. A <see cref="DateOnly"/> rather
/// than a timestamp: a due date has no time of day and no zone, so AD-13's timestamp rule does not
/// apply to it.
/// </param>
public sealed record VehicleSummary(
    int Id,
    string Model,
    string LicensePlate,
    decimal CapacityKg,
    int Mileage,
    DateOnly? NextMaintenanceDate);
