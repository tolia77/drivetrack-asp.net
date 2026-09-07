using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Vehicles;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="IVehicleRepository"/>. See <see cref="EfClientRepository"/> for the AD-6 rules every repository here follows.</summary>
internal sealed class EfVehicleRepository(AppDbContext context) : IVehicleRepository
{
    /// <inheritdoc />
    public Task<Vehicle?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        context.Vehicles.FirstOrDefaultAsync(vehicle => vehicle.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(Vehicle vehicle) => context.Vehicles.Add(vehicle);

    /// <inheritdoc />
    public void Remove(Vehicle vehicle) => context.Vehicles.Remove(vehicle);
}
