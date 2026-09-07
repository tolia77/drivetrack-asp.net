using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="INotificationAttemptRepository"/>. See <see cref="EfClientRepository"/> for the AD-6 rules every repository here follows.</summary>
internal sealed class EfNotificationAttemptRepository(AppDbContext context) : INotificationAttemptRepository
{
    /// <inheritdoc />
    public Task<NotificationAttempt?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        context.NotificationAttempts.FirstOrDefaultAsync(attempt => attempt.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(NotificationAttempt attempt) => context.NotificationAttempts.Add(attempt);
}
