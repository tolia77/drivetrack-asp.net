using DriveTrack.Application.Deliveries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveTrack.Web.Api;

/// <summary>
/// Deliveries over REST (FR-14 to FR-27).
/// <para>
/// <c>[Authorize]</c> here is a convenience, not the decision: it stops an anonymous request before
/// it reaches a service, but whether <em>this</em> caller may open, read or delete a delivery is
/// decided by <c>IAccessGuard</c> inside <see cref="IDeliveryService"/> and nowhere else (AD-2).
/// That is also why there is no <c>Roles</c> argument — the two lists below are reserved to
/// different callers, and an attribute that named one of them would be a second, coarser copy of a
/// rule the service already states precisely.
/// </para>
/// <para>
/// The two lists are separate routes rather than one route that branches on the caller's role.
/// AD-1 keeps domain branching out of adapters and AD-17 makes the shapes different types, so the
/// split is in the routing table where a reader can see it: <c>/api/deliveries</c> answers
/// dispatch's view, <c>/api/deliveries/mine</c> answers a driver's or a client's, and neither can
/// be coaxed into answering the other.
/// </para>
/// <para>
/// Thin by construction: no try/catch, no <c>IActionResult</c>, no status set by hand. AD-7 leaves
/// every status and every message to the envelope.
/// </para>
/// </summary>
[ApiController]
[Route("api/deliveries")]
[Authorize]
public sealed class DeliveriesController(IDeliveryService deliveries) : ControllerBase
{
    /// <summary>One page of every delivery (FR-18). Defaults to the first hundred.</summary>
    /// <remarks>
    /// The two parameters are nullable so an omitted one takes the default here rather than binding
    /// to zero — a limit of zero is a refusal, and "I did not say" must not mean "give me nothing".
    /// </remarks>
    [HttpGet]
    public Task<IReadOnlyList<DeliverySummary>> ListAsync(
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        deliveries.ListAsync(
            new ListDeliveriesQuery(offset ?? 0, limit ?? ListDeliveriesQueryValidator.MaximumLimit),
            cancellationToken);

    /// <summary>
    /// One page of the caller's own deliveries (FR-25, FR-27). The payload carries no counterparty
    /// identity, because the type it is built from has no field for one.
    /// </summary>
    [HttpGet("mine")]
    public Task<IReadOnlyList<AssignedDeliverySummary>> ListMineAsync(
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        deliveries.ListMineAsync(
            new ListDeliveriesQuery(offset ?? 0, limit ?? ListDeliveriesQueryValidator.MaximumLimit),
            cancellationToken);

    /// <summary>
    /// The places matching a typed address (FR-104), each with the point a map click would set.
    /// </summary>
    /// <remarks>
    /// A literal segment beside the <c>{id:int}</c> routes below, and the constraint is what keeps
    /// the two apart: <c>places</c> is not an integer, so it can only ever match this route.
    /// </remarks>
    [HttpGet("places")]
    public Task<IReadOnlyList<PlaceMatch>> SearchPlacesAsync(
        [FromQuery] string? query,
        CancellationToken cancellationToken) =>
        deliveries.SearchPlacesAsync(new SearchPlacesQuery(query), cancellationToken);

    /// <summary>One delivery, as dispatch reads it.</summary>
    [HttpGet("{id:int}")]
    public Task<DeliverySummary> GetAsync(int id, CancellationToken cancellationToken) =>
        deliveries.GetAsync(id, cancellationToken);

    /// <summary>Opens a delivery (FR-14 to FR-17).</summary>
    [HttpPost]
    public Task<DeliverySummary> CreateAsync(
        [FromBody] CreateDeliveryCommand command,
        CancellationToken cancellationToken) =>
        deliveries.CreateAsync(command, cancellationToken);

    /// <summary>Changes a delivery (FR-22). Absent fields are left alone (AD-23).</summary>
    [HttpPut("{id:int}")]
    public Task<DeliverySummary> UpdateAsync(
        int id,
        [FromBody] UpdateDeliveryCommand command,
        CancellationToken cancellationToken) =>
        deliveries.UpdateAsync(id, command, cancellationToken);

    /// <summary>Deletes a delivery and its dependents (FR-24). Admin only, decided in the service.</summary>
    [HttpDelete("{id:int}")]
    public Task DeleteAsync(int id, CancellationToken cancellationToken) =>
        deliveries.DeleteAsync(id, cancellationToken);

    /// <summary>
    /// Advances a delivery's lifecycle (FR-30 to FR-34). Still no <c>Roles</c> argument: "the
    /// assigned driver, or dispatch" is not a role list, and the guard states it precisely.
    /// </summary>
    [HttpPost("{id:int}/status")]
    public Task<TimelineEntryView> ChangeStatusAsync(
        int id,
        [FromBody] ChangeDeliveryStatusCommand command,
        CancellationToken cancellationToken) =>
        deliveries.ChangeStatusAsync(id, command, cancellationToken);

    /// <summary>Appends a note to a delivery's timeline, changing nothing else (FR-107).</summary>
    [HttpPost("{id:int}/timeline")]
    public Task<TimelineEntryView> AddNoteAsync(
        int id,
        [FromBody] AddDeliveryNoteCommand command,
        CancellationToken cancellationToken) =>
        deliveries.AddNoteAsync(id, command, cancellationToken);

    /// <summary>A delivery's history, oldest first (FR-108).</summary>
    [HttpGet("{id:int}/timeline")]
    public Task<IReadOnlyList<TimelineEntryView>> ListTimelineAsync(
        int id,
        CancellationToken cancellationToken) =>
        deliveries.ListTimelineAsync(id, cancellationToken);
}
