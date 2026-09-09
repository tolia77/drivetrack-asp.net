using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IClientRepository"/> over the unit of work's context.
/// <para>
/// AD-6: every method awaits a materializing call, so nothing an <c>IQueryable</c> could carry
/// — a live context, a deferred filter, a lazy load — escapes into Application.
/// </para>
/// </summary>
internal sealed class EfClientRepository(AppDbContext context) : IClientRepository
{
    /// <inheritdoc />
    public Task<Client?> GetByIdAsync(ClientId id, CancellationToken cancellationToken) =>
        context.Clients.FirstOrDefaultAsync(client => client.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Client>> ListAsync(CancellationToken cancellationToken) =>
        // Ordered so this method answers the same way on every request rather than at the
        // planner's discretion. It is not what orders the roster screen: the only caller joins
        // this list into a dictionary and renders it in ListByRoleAsync's surname order.
        // Awaited to a list, so nothing an IQueryable could carry escapes (AD-6).
        await context.Clients
            .OrderBy(client => client.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public void Add(Client client) => context.Clients.Add(client);

    /// <inheritdoc />
    public void Remove(Client client) => context.Clients.Remove(client);
}
