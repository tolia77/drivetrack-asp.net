using DriveTrack.Domain.Reviews;

namespace DriveTrack.Application.Abstractions;

/// <summary>Persistence for <see cref="Review"/>. See <see cref="IClientRepository"/> for the shape rules (AD-6).</summary>
public interface IReviewRepository
{
    /// <summary>Loads a review, or null when there is none with that id.</summary>
    Task<Review?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Stages a new review for the next commit.</summary>
    void Add(Review review);

    /// <summary>Stages a review for deletion (FR-66).</summary>
    void Remove(Review review);
}
