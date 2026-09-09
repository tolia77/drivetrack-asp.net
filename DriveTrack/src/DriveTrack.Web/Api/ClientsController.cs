using DriveTrack.Application.Users;
using DriveTrack.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveTrack.Web.Api;

/// <summary>
/// The client roster over REST (FR-46, FR-47, FR-48): the original's four <c>/clients</c> endpoints.
/// <para>
/// <c>[Authorize]</c> carries no roles, and that is the point. Who may list, edit or delete a client
/// is decided by <c>IAccessGuard</c> inside <see cref="IClientAdministrationService"/> — reads are
/// dispatcher-or-admin, writes are admin — and putting a second, differently-worded copy of that
/// rule in an attribute here is how two answers to one question get shipped (AD-2). The attribute
/// only spares an anonymous caller a round trip through the service.
/// </para>
/// <para>
/// Thin, like every controller here (NFR-7): bind, call, return. The envelope comes from the global
/// result filter and every failure from the branch middleware, so there is no status code and no
/// <c>try</c> in this file.
/// </para>
/// </summary>
[ApiController]
[Route("api/clients")]
[Authorize]
public sealed class ClientsController(IClientAdministrationService clients) : ControllerBase
{
    /// <summary>Every client, with name, email and phone number (FR-46, FR-48).</summary>
    [HttpGet]
    public Task<IReadOnlyList<ClientAccount>> ListAsync(CancellationToken cancellationToken) =>
        clients.ListAsync(cancellationToken);

    /// <summary>One client, addressed by the user id of their account.</summary>
    [HttpGet("{id:int}")]
    public Task<ClientAccount> GetAsync(int id, CancellationToken cancellationToken) =>
        clients.GetAsync(new UserId(id), cancellationToken);

    /// <summary>
    /// Edits a client (FR-46). Every field the body omits is left alone; a <c>password</c> the body
    /// omits leaves the stored password in place (AD-23).
    /// </summary>
    [HttpPatch("{id:int}")]
    public Task<ClientAccount> UpdateAsync(
        int id,
        [FromBody] UpdateClientCommand command,
        CancellationToken cancellationToken) =>
        clients.UpdateAsync(new UserId(id), command, cancellationToken);

    /// <summary>Deletes a client's account (FR-47). 204, with no body.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await clients.DeleteAsync(new UserId(id), cancellationToken);

        return NoContent();
    }
}
