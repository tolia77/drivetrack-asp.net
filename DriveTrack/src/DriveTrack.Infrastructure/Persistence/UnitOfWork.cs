using DriveTrack.Application.Abstractions;
using DriveTrack.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore.Storage;

namespace DriveTrack.Infrastructure.Persistence;

/// <summary>
/// One operation's persistence scope (AD-5): one short-lived context, one transaction, one
/// commit. Created by <see cref="UnitOfWorkFactory"/> and disposed by the caller.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _context;
    private readonly IDbContextTransaction _transaction;
    private bool _committed;

    internal UnitOfWork(AppDbContext context, IDbContextTransaction transaction)
    {
        _context = context;
        _transaction = transaction;

        Clients = new EfClientRepository(context);
        Drivers = new EfDriverRepository(context);
        Vehicles = new EfVehicleRepository(context);
        Deliveries = new EfDeliveryRepository(context);
        Shifts = new EfShiftRepository(context);
        Reviews = new EfReviewRepository(context);
        ProofOfDeliveries = new EfProofOfDeliveryRepository(context);
        NotificationAttempts = new EfNotificationAttemptRepository(context);
        TimelineEntries = new EfTimelineEntryRepository(context);
        Messages = new EfMessageRepository(context);
    }

    /// <inheritdoc />
    public IClientRepository Clients { get; }

    /// <inheritdoc />
    public IDriverRepository Drivers { get; }

    /// <inheritdoc />
    public IVehicleRepository Vehicles { get; }

    /// <inheritdoc />
    public IDeliveryRepository Deliveries { get; }

    /// <inheritdoc />
    public IShiftRepository Shifts { get; }

    /// <inheritdoc />
    public IReviewRepository Reviews { get; }

    /// <inheritdoc />
    public IProofOfDeliveryRepository ProofOfDeliveries { get; }

    /// <inheritdoc />
    public INotificationAttemptRepository NotificationAttempts { get; }

    /// <inheritdoc />
    public ITimelineEntryRepository TimelineEntries { get; }

    /// <inheritdoc />
    public IMessageRepository Messages { get; }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_committed)
        {
            throw new InvalidOperationException(
                "This unit of work has already been committed. AD-5 gives each operation one "
                + "scope and one commit; create a new scope for the next operation.");
        }

        await _context.SaveChangesAsync(cancellationToken);
        await _transaction.CommitAsync(cancellationToken);

        _committed = true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            // Disposing an uncommitted transaction rolls it back, which is what makes "dispose
            // without commit" the safe default: a failure part-way through a multi-step write
            // leaves nothing behind (NFR-9) without every caller remembering to say so.
            await _transaction.DisposeAsync();
        }
        finally
        {
            // In a finally because the context comes from a pool: a transaction disposal that
            // throws - a dropped connection during rollback is the ordinary case - would
            // otherwise leak one context per failure until the pool ran dry.
            await _context.DisposeAsync();
        }
    }
}
