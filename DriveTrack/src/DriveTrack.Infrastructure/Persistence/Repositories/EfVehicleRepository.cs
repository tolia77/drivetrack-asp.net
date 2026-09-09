using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Identity;
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
    public async Task<IReadOnlyList<Vehicle>> ListAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken) =>
        // Ordered explicitly: PostgreSQL is free to return rows in any order without an ORDER BY,
        // and an unordered OFFSET is a page that can repeat and skip rows between requests.
        await context.Vehicles
            .AsNoTracking()
            .OrderBy(vehicle => vehicle.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Vehicle>> ListUnassignedAsync(CancellationToken cancellationToken)
    {
        // The assignment column lives on the driver row, so the question is asked of Drivers even
        // though the answer is a set of vehicles. AD-24 keeps that here rather than on the driver
        // repository: which vehicles are free is a Vehicles-owned question.
        var held = context.Drivers
            .Where(driver => driver.VehicleId != null)
            .Select(driver => driver.VehicleId!.Value);

        return await context.Vehicles
            .AsNoTracking()
            .Where(vehicle => !held.Contains(vehicle.Id))
            .OrderBy(vehicle => vehicle.Id)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<Vehicle?> FindByLicensePlateAsync(
        string licensePlate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(licensePlate);

        // Plain equality, deliberately. The plate is normalized before it is stored, so this asks
        // the same question the unique index answers - which means the index can serve the lookup
        // and enforces exactly what the caller's check promises.
        //
        // The rejected alternative was `vehicle.LicensePlate.ToUpper() == normalized`: it
        // translates to upper(license_plate) = @p, which no index on the column can serve, and it
        // compares .NET's ToUpperInvariant against PostgreSQL's upper() - two case rules that need
        // not agree outside ASCII.
        return context.Vehicles.FirstOrDefaultAsync(
            vehicle => vehicle.LicensePlate == licensePlate,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DriverId?> FindHolderAsync(int vehicleId, CancellationToken cancellationToken)
    {
        // Projected and untracked: the caller wants the identity of the holder, not the holder, and
        // materializing a second copy of a driver the caller may already be editing would put two
        // instances of one row in front of the change tracker.
        var holders = await context.Drivers
            .AsNoTracking()
            .Where(driver => driver.VehicleId == vehicleId)
            .Select(driver => driver.Id)
            .Take(1)
            .ToListAsync(cancellationToken);

        return holders.Count == 0 ? null : holders[0];
    }

    /// <inheritdoc />
    public void Add(Vehicle vehicle) => context.Vehicles.Add(vehicle);

    /// <inheritdoc />
    public void Remove(Vehicle vehicle) => context.Vehicles.Remove(vehicle);
}
