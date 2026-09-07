using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Shifts;
using DriveTrack.Domain.Vehicles;

namespace DriveTrack.Domain.Drivers;

/// <summary>
/// The driver subtype of an application user. It exists as its own table because it carries
/// real fields; admin and dispatcher carry none and get no table (DR-3).
/// <para>
/// <see cref="Id"/> is a surrogate key of its own, distinct from <see cref="UserId"/>. That
/// separation is what gives AD-22 teeth — if a driver row id equalled its user id, confusing
/// the two would be harmless and the typed ids would be decoration.
/// </para>
/// </summary>
public sealed class Driver
{
    /// <summary>Surrogate key of the driver row.</summary>
    public DriverId Id { get; set; }

    /// <summary>
    /// The application user this driver is. Required and unique — one driver row per user.
    /// Domain never names the Identity type; the relationship is declared in Infrastructure.
    /// </summary>
    public required UserId UserId { get; set; }

    /// <summary>Driving licence number as recorded by a dispatcher.</summary>
    public required string LicenseNumber { get; set; }

    /// <summary>The assigned vehicle, or null when the driver holds none (FR-45).</summary>
    public int? VehicleId { get; set; }

    /// <inheritdoc cref="VehicleId" />
    public Vehicle? Vehicle { get; set; }

    /// <summary>Shifts this driver has worked. At most one may be open at a time (FR-110).</summary>
    public ICollection<Shift> Shifts { get; } = [];

    /// <summary>Deliveries currently assigned to this driver.</summary>
    public ICollection<Delivery> Deliveries { get; } = [];

    // Deliberately no Messages collection: AD-16 keeps chat severable, so nothing outside the
    // chat module holds a path to Message.
}
