using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="IDriverRepository"/>. See <see cref="EfClientRepository"/> for the AD-6 rules every repository here follows.</summary>
internal sealed class EfDriverRepository(AppDbContext context) : IDriverRepository
{
    /// <inheritdoc />
    public Task<Driver?> GetByIdAsync(DriverId id, CancellationToken cancellationToken) =>
        context.Drivers.FirstOrDefaultAsync(driver => driver.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(Driver driver) => context.Drivers.Add(driver);

    /// <inheritdoc />
    public void Remove(Driver driver) => context.Drivers.Remove(driver);
}
