using DriveTrack.Application.Users;
using DriveTrack.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveTrack.Web.Api;

/// <summary>
/// Dispatcher accounts over REST (FR-49, FR-50): the original's five <c>/dispatchers</c> endpoints.
/// <para>
/// As in <see cref="ClientsController"/>, <c>[Authorize]</c> carries no roles: every one of these
/// operations is admin-only, and that is settled by <c>IAccessGuard</c> inside
/// <see cref="IDispatcherAdministrationService"/> (AD-2).
/// </para>
/// </summary>
[ApiController]
[Route("api/dispatchers")]
[Authorize]
public sealed class DispatchersController(IDispatcherAdministrationService dispatchers) : ControllerBase
{
    /// <summary>Opens a dispatcher account (FR-49). 201, with the account as the payload.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateDispatcherCommand command,
        CancellationToken cancellationToken)
    {
        var account = await dispatchers.CreateAsync(command, cancellationToken);

        return Created(
            new Uri("/api/dispatchers/" + account.UserId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture), UriKind.Relative),
            account);
    }

    /// <summary>Every dispatcher (FR-49).</summary>
    [HttpGet]
    public Task<IReadOnlyList<DispatcherAccount>> ListAsync(CancellationToken cancellationToken) =>
        dispatchers.ListAsync(cancellationToken);

    /// <summary>One dispatcher. A user id that is not a dispatcher's answers 404.</summary>
    [HttpGet("{id:int}")]
    public Task<DispatcherAccount> GetAsync(int id, CancellationToken cancellationToken) =>
        dispatchers.GetAsync(new UserId(id), cancellationToken);

    /// <summary>
    /// Edits a dispatcher (FR-50). A body with no <c>password</c> leaves the existing password
    /// working (AD-23).
    /// </summary>
    [HttpPatch("{id:int}")]
    public Task<DispatcherAccount> UpdateAsync(
        int id,
        [FromBody] UpdateDispatcherCommand command,
        CancellationToken cancellationToken) =>
        dispatchers.UpdateAsync(new UserId(id), command, cancellationToken);

    /// <summary>Deletes a dispatcher's account (FR-50). 204, with no body.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await dispatchers.DeleteAsync(new UserId(id), cancellationToken);

        return NoContent();
    }
}
