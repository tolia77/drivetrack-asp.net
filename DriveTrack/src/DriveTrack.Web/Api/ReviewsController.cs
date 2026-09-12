using DriveTrack.Application.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveTrack.Web.Api;

/// <summary>
/// Reviews over REST (FR-62 to FR-67).
/// <para>
/// <c>[Authorize]</c> carries no roles, and that is the point. Who may write a review, read the
/// whole collection, or edit and delete one is decided by <c>IAccessGuard</c> inside
/// <see cref="IReviewService"/> — and this capability is where a role argument would be most
/// wrong: naming a pair of roles on the writes would read as one rule and mean three different ones
/// across the five routes, because writing a review, editing one and deleting one are reserved to
/// three different sets of callers (AD-2).
/// </para>
/// <para>
/// The two lists are separate routes rather than one route that branches on the caller's role, for
/// the reason <c>DeliveriesController</c> splits its two: AD-1 keeps domain branching out of
/// adapters and AD-17 makes the shapes different types, so <c>/api/reviews</c> answers moderation's
/// view — with both parties — and <c>/api/reviews/mine</c> answers the author's, which has no party
/// field at all.
/// </para>
/// <para>
/// Thin by construction (NFR-7): bind, call, return. The envelope comes from the global result
/// filter and every failure from the branch middleware, so there is no status code and no
/// <c>try</c> in this file.
/// </para>
/// </summary>
[ApiController]
[Route("api/reviews")]
[Authorize]
public sealed class ReviewsController(IReviewService reviews) : ControllerBase
{
    /// <summary>One page of every review, with both parties named (FR-64). Defaults to the first hundred.</summary>
    /// <remarks>
    /// The two parameters are nullable so an omitted one takes the default here rather than binding
    /// to zero — a limit of zero is a refusal, and "I did not say" must not mean "give me nothing".
    /// </remarks>
    [HttpGet]
    public Task<IReadOnlyList<ReviewSummary>> ListAsync(
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        reviews.ListAsync(
            new ListReviewsQuery(offset ?? 0, limit ?? ListReviewsQueryValidator.MaximumLimit),
            cancellationToken);

    /// <summary>
    /// One page of the reviews the caller wrote (FR-63). The payload carries no party identity,
    /// because the type it is built from has no field for one.
    /// </summary>
    [HttpGet("mine")]
    public Task<IReadOnlyList<AuthoredReviewSummary>> ListMineAsync(
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        reviews.ListMineAsync(
            new ListReviewsQuery(offset ?? 0, limit ?? ListReviewsQueryValidator.MaximumLimit),
            cancellationToken);

    /// <summary>Writes a review of one of the caller's own finished deliveries (FR-62, FR-67).</summary>
    [HttpPost]
    public Task<AuthoredReviewSummary> CreateAsync(
        [FromBody] CreateReviewCommand command,
        CancellationToken cancellationToken) =>
        reviews.CreateAsync(command, cancellationToken);

    /// <summary>
    /// Edits a review (FR-65). Every field the body omits is left alone (AD-23).
    /// </summary>
    [HttpPut("{id:int}")]
    public Task<AuthoredReviewSummary> UpdateAsync(
        int id,
        [FromBody] UpdateReviewCommand command,
        CancellationToken cancellationToken) =>
        reviews.UpdateAsync(id, command, cancellationToken);

    /// <summary>Deletes a review (FR-66). 204, with no body.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await reviews.DeleteAsync(id, cancellationToken);

        return NoContent();
    }
}
