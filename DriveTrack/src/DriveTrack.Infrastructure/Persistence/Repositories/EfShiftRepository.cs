using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Shifts;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="IShiftRepository"/>. See <see cref="EfClientRepository"/> for the AD-6 rules every repository here follows.</summary>
internal sealed class EfShiftRepository(AppDbContext context) : IShiftRepository
{
    /// <inheritdoc />
    public Task<Shift?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        context.Shifts.FirstOrDefaultAsync(shift => shift.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(Shift shift) => context.Shifts.Add(shift);
}
