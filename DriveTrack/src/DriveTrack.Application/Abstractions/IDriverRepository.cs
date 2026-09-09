using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Abstractions;

/// <summary>Persistence for <see cref="Driver"/>. See <see cref="IClientRepository"/> for the shape rules (AD-6).</summary>
public interface IDriverRepository
{
    /// <summary>
    /// Loads a driver with the vehicle they hold, or null when there is none with that id.
    /// </summary>
    /// <remarks>
    /// The vehicle travels with the driver so the summary can name it without the Drivers
    /// capability reaching into <c>unitOfWork.Vehicles</c>, which AD-24 reserves for the one writer
    /// of <c>drivers.vehicle_id</c>.
    /// </remarks>
    Task<Driver?> GetByIdAsync(DriverId id, CancellationToken cancellationToken);

    /// <summary>
    /// Every driver, with the vehicle each holds (FR-35). Unpaged, exactly as the original's
    /// <c>GET /drivers</c> was; PRD section 8 excludes server-side filtering and totals, so the
    /// screen's search runs in the browser over this list.
    /// </summary>
    Task<IReadOnlyList<Driver>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Stages a new driver for the next commit.</summary>
    void Add(Driver driver);

    /// <summary>Stages a driver for deletion (FR-39).</summary>
    void Remove(Driver driver);
}
