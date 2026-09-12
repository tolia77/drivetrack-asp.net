using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="IDeliveryRepository"/>. See <see cref="EfClientRepository"/> for the AD-6 rules every repository here follows.</summary>
internal sealed class EfDeliveryRepository(AppDbContext context) : IDeliveryRepository
{
    /// <inheritdoc />
    public Task<Delivery?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        // No Include, and the delete path depends on it: a tracked timeline entry would make EF
        // issue its own DELETE at trigger depth 1, where the append-only guard raises. The owned
        // locations still load, because an owned type is columns of this row rather than a
        // navigation to another one (AD-11).
        context.Deliveries.FirstOrDefaultAsync(delivery => delivery.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<Delivery?> FindVisibleAsync(
        int id,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var rows = context.Deliveries.AsNoTracking().Where(delivery => delivery.Id == id);

        // The same narrowing ListAsync applies, on a single row: a driver asking about a delivery
        // that is not theirs gets no row rather than a row they then have to be refused, so the
        // answer is 404 and nothing about the delivery's existence is disclosed.
        if (scope.DriverId is { } driverId)
        {
            rows = rows.Where(delivery => delivery.DriverId == driverId);
        }

        if (scope.ClientId is { } clientId)
        {
            rows = rows.Where(delivery => delivery.ClientId == clientId);
        }

        return rows.FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Delivery>> ListAsync(
        AccessScope scope,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = context.Deliveries.AsNoTracking();

        // AD-3: the scope is a WHERE, applied before OFFSET and LIMIT. Filtering the materialized
        // page instead would answer an empty page for a driver whose rows fall outside the first
        // hundred - the defect the whole scope parameter exists to make unrepresentable.
        if (scope.DriverId is { } driverId)
        {
            rows = rows.Where(delivery => delivery.DriverId == driverId);
        }

        if (scope.ClientId is { } clientId)
        {
            rows = rows.Where(delivery => delivery.ClientId == clientId);
        }

        // Ordered explicitly: PostgreSQL is free to return rows in any order without an ORDER BY,
        // and an unordered OFFSET is a page that can repeat and skip rows between requests.
        return await rows
            .OrderBy(delivery => delivery.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Delivery>> ListByIdsAsync(
        AccessScope scope,
        IReadOnlyCollection<int> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            // An IN () clause is not a query PostgreSQL can run, and a round trip that can only
            // answer nothing is a round trip worth not making.
            return [];
        }

        // Distinct because the caller's rows may name one delivery more than once; the database
        // would answer the row once either way, but the parameter list has no reason to repeat.
        var keys = ids.Distinct().ToArray();

        var rows = context.Deliveries
            .AsNoTracking()
            .Where(delivery => keys.Contains(delivery.Id));

        // The same narrowing ListAsync applies (AD-3), and for the same reason: a row outside the
        // scope is absent from the answer rather than present and then filtered, so no caller can
        // learn of a delivery they may not see by naming its id.
        if (scope.DriverId is { } driverId)
        {
            rows = rows.Where(delivery => delivery.DriverId == driverId);
        }

        if (scope.ClientId is { } clientId)
        {
            rows = rows.Where(delivery => delivery.ClientId == clientId);
        }

        return await rows.ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<decimal?> FindHeaviestActiveWeightForDriverAsync(
        DriverId driverId,
        CancellationToken cancellationToken) =>
        // Max rather than Sum, and that is the whole reading of FR-103: a vehicle carries one
        // parcel at a time, so the question is whether the heaviest one fits, not whether the
        // driver's whole outstanding load would.
        //
        // One aggregate rather than a page of rows the caller would have to walk, and Max over a
        // nullable projection answers null for an empty set instead of throwing the way Max over a
        // non-nullable one would.
        context.Deliveries
            .AsNoTracking()
            .Where(delivery => delivery.DriverId == driverId)
            .Where(delivery => delivery.Status == DeliveryStatus.Pending
                || delivery.Status == DeliveryStatus.InTransit)
            .Select(delivery => (decimal?)delivery.PackageWeightKg)
            .MaxAsync(cancellationToken);

    /// <inheritdoc />
    public void Add(Delivery delivery) => context.Deliveries.Add(delivery);

    /// <inheritdoc />
    public void Remove(Delivery delivery) => context.Deliveries.Remove(delivery);
}
