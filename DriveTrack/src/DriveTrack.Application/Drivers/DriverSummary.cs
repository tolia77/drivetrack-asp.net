using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Drivers;

/// <summary>
/// A driver as a dispatcher sees them (FR-35): the account's identity fields, the licence number
/// the driver row carries, and the vehicle they hold. A DTO, so <c>Driver</c>, <c>Vehicle</c> and
/// the EF navigation between them never bind to a component parameter or serialize (AD-17).
/// <para>
/// <see cref="Id"/> and <see cref="UserId"/> are both here and are deliberately different types:
/// the driver row's key addresses this capability's rows, the user id addresses the account, and
/// AD-22 exists so the two cannot be confused into authorizing the wrong caller.
/// </para>
/// <para>
/// The vehicle is flattened into three fields rather than nested. It is what the list and the
/// browser-side search read, and FR-36 searches text — so the model and the plate travel as text.
/// </para>
/// </summary>
/// <param name="Id">The driver row's id.</param>
/// <param name="UserId">The account this driver is.</param>
/// <param name="FirstName">Given name, from the account.</param>
/// <param name="LastName">Family name, from the account.</param>
/// <param name="Email">The address the account signs in with.</param>
/// <param name="LicenseNumber">Driving licence number as recorded by a dispatcher.</param>
/// <param name="VehicleId">The vehicle held, or null when the driver holds none (FR-45).</param>
/// <param name="VehicleModel">Its make and model, or null when the driver holds none.</param>
/// <param name="VehicleLicensePlate">Its registration plate, or null when the driver holds none.</param>
public sealed record DriverSummary(
    DriverId Id,
    UserId UserId,
    string FirstName,
    string LastName,
    string Email,
    string LicenseNumber,
    int? VehicleId,
    string? VehicleModel,
    string? VehicleLicensePlate);
