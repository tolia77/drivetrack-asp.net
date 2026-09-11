using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Chat;
using DriveTrack.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="IMessageRepository"/>. See <see cref="EfClientRepository"/> for the AD-6 rules every repository here follows.</summary>
internal sealed class EfMessageRepository(AppDbContext context) : IMessageRepository
{
    /// <inheritdoc />
    public Task<Message?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        context.Messages.FirstOrDefaultAsync(message => message.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Message>> ListForDriverAsync(
        DriverId driverId,
        CancellationToken cancellationToken) =>
        await context.Messages
            // A history is read, never edited: nothing here is written back, so tracking it would
            // only be change detection over rows no code path changes.
            .AsNoTracking()
            // The thread key, and the same one the row was written against (AD-15). The
            // (driver_id, sent_at) index declared on the table serves exactly this shape.
            .Where(message => message.DriverId == driverId)
            // Id breaks a tie, for the reason the timeline read gives: two messages written in one
            // transaction share an instant, and FR-72 asks for an order rather than an almost-order.
            .OrderBy(message => message.SentAt)
            .ThenBy(message => message.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public void Add(Message message) => context.Messages.Add(message);
}
