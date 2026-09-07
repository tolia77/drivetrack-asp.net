using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Reviews;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="IReviewRepository"/>. See <see cref="EfClientRepository"/> for the AD-6 rules every repository here follows.</summary>
internal sealed class EfReviewRepository(AppDbContext context) : IReviewRepository
{
    /// <inheritdoc />
    public Task<Review?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        context.Reviews.FirstOrDefaultAsync(review => review.Id == id, cancellationToken);

    /// <inheritdoc />
    public void Add(Review review) => context.Reviews.Add(review);

    /// <inheritdoc />
    public void Remove(Review review) => context.Reviews.Remove(review);
}
