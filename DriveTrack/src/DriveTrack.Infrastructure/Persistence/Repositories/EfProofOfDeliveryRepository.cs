using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
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
    public Task<bool> ExistsForDeliveryAsync(int deliveryId, CancellationToken cancellationToken) =>
        // AnyAsync rather than a load: the caller wants the answer, and a unique index on
        // delivery_id makes this an index probe rather than a read of the row and its assets.
        context.ProofOfDeliveries
            .AsNoTracking()
            .AnyAsync(proof => proof.DeliveryId == deliveryId, cancellationToken);

    /// <inheritdoc />
    public Task<ProofOfDelivery?> FindVisibleByDeliveryAsync(
        int deliveryId,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        // Built before the query rather than called inside the lambda: a method call inside an
        // expression tree is something the translator has to make sense of, and a local holding the
        // composed query is a subquery it already understands.
        var visible = Visible(scope);

        return context.ProofOfDeliveries
            .AsNoTracking()

            // The assets come with the proof because every caller of this needs them: FR-122's view
            // is the capture plus its artefacts, and fetching them separately would be a second
            // round trip for a set the same query already knows how to join.
            .Include(proof => proof.Assets)
            .Where(proof => proof.DeliveryId == deliveryId)

            // AD-3: the narrowing is a WHERE against the delivery this proof belongs to, not a
            // filter applied to a row that was already returned. A client asking about another
            // client's proof gets no row rather than a row they then have to be refused.
            .Where(proof => visible.Any(delivery => delivery.Id == proof.DeliveryId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<ProofAsset?> FindVisibleAssetAsync(
        int assetId,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var visible = Visible(scope);

        // ProofAsset has no DbSet of its own - it is reached through its proof - so the set is asked
        // for by type. The scope still travels the whole way: asset to proof to delivery, all three
        // in the one WHERE.
        return context.Set<ProofAsset>()
            .AsNoTracking()
            .Where(asset => asset.Id == assetId)
            .Where(asset => context.ProofOfDeliveries.Any(proof =>
                proof.Id == asset.ProofOfDeliveryId
                && visible.Any(delivery => delivery.Id == proof.DeliveryId)))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Add(ProofOfDelivery proof) => context.ProofOfDeliveries.Add(proof);

    /// <summary>
    /// The deliveries this caller may be shown, as a query the two reads above compose against: a
    /// proof is visible exactly when its delivery is, and an asset exactly when its proof is.
    /// <para>
    /// The pair of predicates is written out again here rather than shared with
    /// <see cref="EfDeliveryRepository"/>, which carries its own copy in each of its two visibility
    /// reads. Sharing them would mean a helper both repositories reach — and AD-6 keeps a repository
    /// a closed unit over one aggregate, so the alternative to this duplication is a third type
    /// whose whole content is two <c>Where</c> clauses. Said once <em>within this file</em> is what
    /// is claimed here, and it is all that is claimed: the day the scope grows a third dimension,
    /// this file and <c>EfDeliveryRepository</c> both have to be edited.
    /// </para>
    /// </summary>
    private IQueryable<Delivery> Visible(AccessScope scope)
    {
        var rows = context.Deliveries.AsNoTracking();

        if (scope.DriverId is { } driverId)
        {
            rows = rows.Where(delivery => delivery.DriverId == driverId);
        }

        if (scope.ClientId is { } clientId)
        {
            rows = rows.Where(delivery => delivery.ClientId == clientId);
        }

        return rows;
    }
}
