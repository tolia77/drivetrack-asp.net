using DriveTrack.Application.Abstractions;
using DriveTrack.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence;

/// <summary>
/// Creates a persistence scope per operation from the pooled context factory (AD-5).
/// <para>
/// Registered as a singleton, which is safe precisely because it holds no context of its own:
/// every scope gets a fresh one from the pool and returns it on disposal. That is the whole
/// point of the shape — a scoped context on a Blazor Server circuit would live as long as the
/// circuit, hours, accumulating tracked entities and throwing on concurrent renders.
/// </para>
/// </summary>
public sealed class UnitOfWorkFactory(
    IDbContextFactory<AppDbContext> contextFactory,
    ScopedIdentityFactory identityFactory)
    : IUnitOfWorkFactory
{
    /// <inheritdoc />
    public async Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken)
    {
        var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var identity = identityFactory.Create(context);

            return new UnitOfWork(context, transaction, identity);
        }
        catch
        {
            // The caller never receives the scope, so it can never dispose it; returning the
            // context to the pool here is the only thing that stops the pool draining on a
            // database that is refusing connections.
            await context.DisposeAsync();
            throw;
        }
    }
}
