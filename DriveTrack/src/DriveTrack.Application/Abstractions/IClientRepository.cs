using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Abstractions;

/// <summary>
/// Persistence for <see cref="Client"/>.
/// <para>
/// AD-6: every method returns a materialized entity, never an <c>IQueryable</c> and never a
/// stream over a live context, and every I/O method takes a cancellation token. The surface is
/// deliberately narrow — a repository gains a query method only when a requirement asks for one.
/// <see cref="ListAsync"/> is the first requirement that has: FR-46 and FR-48 need the whole
/// roster, so it arrived with them rather than in advance of them.
/// </para>
/// </summary>
public interface IClientRepository
{
    /// <summary>Loads a client, or null when there is none with that id.</summary>
    Task<Client?> GetByIdAsync(ClientId id, CancellationToken cancellationToken);

    /// <summary>
    /// Every client row (FR-46, FR-48). Materialized and ordered, and unpaginated because the
    /// original's client list is (NFR-27).
    /// </summary>
    Task<IReadOnlyList<Client>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Stages a new client for the next commit.</summary>
    void Add(Client client);

    /// <summary>Stages a client for deletion (FR-47).</summary>
    void Remove(Client client);
}
