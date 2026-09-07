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
    public void Add(Client client) => context.Clients.Add(client);

    /// <inheritdoc />
    public void Remove(Client client) => context.Clients.Remove(client);
}
