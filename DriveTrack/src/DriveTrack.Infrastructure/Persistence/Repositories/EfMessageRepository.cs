using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Chat;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="IMessageRepository"/>. See <see cref="EfClientRepository"/> for the AD-6 rules every repository here follows.</summary>
internal sealed class EfMessageRepository(AppDbContext context) : IMessageRepository
{
    /// <inheritdoc />
    public Task<Message?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        context.Messages.FirstOrDefaultAsync(message => message.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(Message message) => context.Messages.Add(message);
}
