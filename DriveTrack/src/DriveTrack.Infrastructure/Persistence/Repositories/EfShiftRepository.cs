using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Identity;
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
    public Task<Shift?> FindOpenAsync(DriverId driverId, CancellationToken cancellationToken) =>
        // Tracked, not AsNoTracking: ending a shift writes to the row this returns, so the change
        // tracker has to be holding it when the caller sets EndedAt.
        //
        // `EndedAt == null` is the whole definition of open and is the index's own filter, so the
        // query the database is asked is the one the index answers.
        context.Shifts.FirstOrDefaultAsync(
            shift => shift.DriverId == driverId && shift.EndedAt == null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Shift>> ListAsync(
        DriverId? driverId,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = context.Shifts.AsNoTracking();

        // AD-3: the narrowing is a WHERE, applied before OFFSET and LIMIT. Filtering the
        // materialized page instead would answer an empty page for a driver whose shifts fall
        // outside the first hundred - the defect the parameter exists to make unrepresentable.
        if (driverId is { } driver)
        {
            rows = rows.Where(shift => shift.DriverId == driver);
        }

        // Newest first, and the id as the tie-break: PostgreSQL is free to return rows in any order
        // without an ORDER BY, and two shifts sharing an instant would otherwise make a page that
        // can repeat and skip rows between requests.
        return await rows
            .OrderByDescending(shift => shift.StartedAt)
            .ThenByDescending(shift => shift.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DriverId>> ListOpenDriverIdsAsync(
        CancellationToken cancellationToken) =>
        // One round trip for the whole roster rather than one per driver, which is the shape that
        // makes a forty driver roster forty queries. The index makes at most one open row per
        // driver, so Distinct is a statement of that rather than a deduplication anything needs.
        await context.Shifts
            .AsNoTracking()
            .Where(shift => shift.EndedAt == null)
            .Select(shift => shift.DriverId)
            .Distinct()
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public void Add(Shift shift) => context.Shifts.Add(shift);

    /// <inheritdoc />
    public void Remove(Shift shift) => context.Shifts.Remove(shift);
}
