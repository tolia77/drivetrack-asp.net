using DriveTrack.Domain.Vehicles;

namespace DriveTrack.Application.Abstractions;

/// <summary>Persistence for <see cref="Vehicle"/>. See <see cref="IClientRepository"/> for the shape rules (AD-6).</summary>
public interface IVehicleRepository
{
    /// <summary>Loads a vehicle, or null when there is none with that id.</summary>
    Task<Vehicle?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Stages a new vehicle for the next commit.</summary>
    void Add(Vehicle vehicle);

    /// <summary>Stages a vehicle for deletion (FR-43).</summary>
    void Remove(Vehicle vehicle);
}
