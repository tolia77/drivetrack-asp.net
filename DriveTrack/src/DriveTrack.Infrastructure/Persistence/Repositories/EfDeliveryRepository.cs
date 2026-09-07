using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="IDeliveryRepository"/>. See <see cref="EfClientRepository"/> for the AD-6 rules every repository here follows.</summary>
internal sealed class EfDeliveryRepository(AppDbContext context) : IDeliveryRepository
{
    /// <inheritdoc />
    public Task<Delivery?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        context.Deliveries.FirstOrDefaultAsync(delivery => delivery.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(Delivery delivery) => context.Deliveries.Add(delivery);

    /// <inheritdoc />
    public void Remove(Delivery delivery) => context.Deliveries.Remove(delivery);
}
