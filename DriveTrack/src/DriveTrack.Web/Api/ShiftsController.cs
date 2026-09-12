using DriveTrack.Application.Shifts;
using DriveTrack.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveTrack.Web.Api;

/// <summary>
/// Driver shifts over REST (FR-109 to FR-117).
/// <para>
/// <c>[Authorize]</c> carries no roles, and on this capability a role argument would be wrong in
/// three different ways at once: a driver may start, end, read, correct and delete their
/// <em>own</em> shifts, dispatch may do all five for <em>anybody's</em>, and a client may do none of
/// it — which is a rule about the row rather than about the route, so no attribute can state it.
/// <c>IAccessGuard.RequireShiftScope</c> and <c>RequireShiftOwner</c> inside
/// <see cref="IShiftService"/> decide, and they run whether or not this controller was reached
/// (AD-2, FR-12).
/// </para>
/// <para>
/// Going on and off duty are their own routes rather than a <c>PUT</c> that sets an end date, and
/// that is FR-117 on the wire: the instants are the server's, and a route whose body carried one
/// would let a driver backdate a shift. It is also what keeps ending a shift one-way — there is no
/// payload here that can null an end.
/// </para>
/// <para>
/// Thin by construction (NFR-7): bind, call, return. The envelope comes from the global result
/// filter and every failure from the branch middleware, so there is no status code and no
/// <c>try</c> in this file.
/// </para>
/// </summary>
[ApiController]
[Route("api/shifts")]
[Authorize]
public sealed class ShiftsController(IShiftService shifts) : ControllerBase
{
    /// <summary>
    /// One page of shifts, newest first (FR-112, FR-113). Defaults to the first hundred.
    /// </summary>
    /// <remarks>
    /// The paging parameters are nullable so an omitted one takes the default here rather than
    /// binding to zero — a limit of zero is a refusal, and "I did not say" must not mean "give me
    /// nothing". <c>driverId</c> is a dispatcher's filter; a driver's own narrowing comes from the
    /// guard and overrides it, so passing somebody else's id narrows to the caller rather than
    /// widening to the other driver.
    /// </remarks>
    [HttpGet]
    public Task<IReadOnlyList<ShiftSummary>> ListAsync(
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromQuery] int? driverId,
        CancellationToken cancellationToken) =>
        shifts.ListAsync(
            new ListShiftsQuery(
                offset ?? 0,
                limit ?? ListShiftsQueryValidator.MaximumLimit,
                driverId),
            cancellationToken);

    /// <summary>One shift.</summary>
    [HttpGet("{id:int}")]
    public Task<ShiftSummary> GetAsync(int id, CancellationToken cancellationToken) =>
        shifts.GetAsync(id, cancellationToken);

    /// <summary>
    /// Which drivers are on duty right now (FR-116), as row ids. Dispatch's read: the delivery board
    /// marks an off-duty driver with it, and never withholds one.
    /// </summary>
    [HttpGet("on-duty")]
    public Task<IReadOnlyList<DriverId>> ListOnDutyAsync(CancellationToken cancellationToken) =>
        shifts.ListOnDutyDriverIdsAsync(cancellationToken);

    /// <summary>Records a shift that already happened, window and all (FR-113).</summary>
    [HttpPost]
    public Task<ShiftSummary> CreateAsync(
        [FromBody] CreateShiftCommand command,
        CancellationToken cancellationToken) =>
        shifts.CreateAsync(command, cancellationToken);

    /// <summary>Puts a driver on duty (FR-109). The start is the server's clock, not the payload's.</summary>
    [HttpPost("start")]
    public Task<ShiftSummary> StartAsync(
        [FromBody] StartShiftCommand command,
        CancellationToken cancellationToken) =>
        shifts.StartAsync(command, cancellationToken);

    /// <summary>Takes a driver off duty (FR-109). The end is the server's clock.</summary>
    [HttpPost("end")]
    public Task<ShiftSummary> EndAsync(
        [FromBody] EndShiftCommand command,
        CancellationToken cancellationToken) =>
        shifts.EndAsync(command, cancellationToken);

    /// <summary>
    /// Corrects a shift (FR-114). Every field the body omits is left alone (AD-23), and
    /// <c>endedAt</c> cannot be sent as null — the contract has no case for it, so a closed shift
    /// cannot be reopened (FR-117).
    /// </summary>
    [HttpPut("{id:int}")]
    public Task<ShiftSummary> UpdateAsync(
        int id,
        [FromBody] UpdateShiftCommand command,
        CancellationToken cancellationToken) =>
        shifts.UpdateAsync(id, command, cancellationToken);

    /// <summary>Deletes a shift (FR-114). 204, with no body.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await shifts.DeleteAsync(id, cancellationToken);

        return NoContent();
    }
}
