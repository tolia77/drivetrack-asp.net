using DriveTrack.Application.Abstractions;
using DriveTrack.Infrastructure.Identity;
using DriveTrack.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
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
    private readonly ScopedIdentity _identity;
    private bool _committed;

    internal UnitOfWork(AppDbContext context, IDbContextTransaction transaction, ScopedIdentity identity)
    {
        _context = context;
        _transaction = transaction;
        _identity = identity;

        // AD-5: the Identity managers are built over this same context, so a user, its role row and
        // its subtype row are staged against one change tracker inside one transaction.
        Users = new EfUserAccountRepository(context, identity);
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
    public IUserAccountRepository Users { get; }

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

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await _transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // AD-8: a unique or check violation leaves here as a typed failure carrying a contract
            // code; anything else is returned unchanged and surfaces as the 500 envelope. The
            // original is kept as the InnerException either way, so the log still has the SQL detail
            // the wire never sees.
            //
            // This is the commit, and the last place a constraint can be caught, but it is not the
            // only place: EfUserAccountRepository.FlushAsync saves inside this transaction to
            // materialize the identity key a subtype row's foreign key needs, and calls the same
            // translator so the two answer alike. Those two are the whole set.
            var translated = PostgresConstraintTranslator.Translate(exception);

            if (ReferenceEquals(translated, exception))
            {
                // Not a constraint the contract models. Rethrow in place so the original stack
                // trace survives; "throw translated" would overwrite it with this line.
                throw;
            }

            throw translated;
        }

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
            try
            {
                // Before the context: disposing a manager disposes its store, and a store built over
                // a context that has already gone back to the pool is a use-after-free waiting to
                // happen. In its own try for the same reason the context disposal is in a finally -
                // an unguarded throw here would strand the pooled context this block exists to return.
                _identity.Dispose();
            }
            finally
            {
                // In a finally because the context comes from a pool: a transaction or manager
                // disposal that throws - a dropped connection during rollback is the ordinary case -
                // would otherwise leak one context per failure until the pool ran dry.
                await _context.DisposeAsync();
            }
        }
    }
}
