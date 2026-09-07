using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="IProofOfDeliveryRepository"/>. See <see cref="EfClientRepository"/> for the AD-6 rules every repository here follows.</summary>
internal sealed class EfProofOfDeliveryRepository(AppDbContext context) : IProofOfDeliveryRepository
{
    /// <inheritdoc />
    public Task<ProofOfDelivery?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        context.ProofOfDeliveries.FirstOrDefaultAsync(proof => proof.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(ProofOfDelivery proof) => context.ProofOfDeliveries.Add(proof);
}
