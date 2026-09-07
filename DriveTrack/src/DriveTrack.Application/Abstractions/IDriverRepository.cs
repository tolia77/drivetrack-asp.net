using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Abstractions;

/// <summary>Persistence for <see cref="Driver"/>. See <see cref="IClientRepository"/> for the shape rules (AD-6).</summary>
public interface IDriverRepository
{
    /// <summary>Loads a driver, or null when there is none with that id.</summary>
    Task<Driver?> GetByIdAsync(DriverId id, CancellationToken cancellationToken);

    /// <summary>Stages a new driver for the next commit.</summary>
    void Add(Driver driver);

    /// <summary>Stages a driver for deletion (FR-39).</summary>
    void Remove(Driver driver);
}
