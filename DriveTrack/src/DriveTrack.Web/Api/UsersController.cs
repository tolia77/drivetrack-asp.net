using DriveTrack.Application.Users;
using DriveTrack.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveTrack.Web.Api;

/// <summary>
/// A user's own account over REST (FR-5, FR-87, FR-88, FR-92).
/// <para>
/// <c>[Authorize]</c> here is a convenience, not the decision: it stops an anonymous request before
/// it reaches a service, but whether <em>this</em> caller may read <em>that</em> profile is decided
/// by <c>IAccessGuard.RequireSelf</c> inside the service and nowhere else (AD-2). Deleting the
/// attribute would change the status of an anonymous call and nothing about who is allowed to read
/// or change what.
/// </para>
/// <para>
/// Thin, like every controller here (NFR-7): bind, call, return. The envelope comes from the global
/// result filter and every failure from the branch middleware, so there is no status code and no
/// <c>try</c> in this file.
/// </para>
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize]
public sealed class UsersController(IUserService users) : ControllerBase
{
    /// <summary>The profile of one user. Their own, or anybody's when the caller is an admin.</summary>
    [HttpGet("{id:int}")]
    public Task<UserProfile> GetAsync(int id, CancellationToken cancellationToken) =>
        users.GetProfileAsync(new UserId(id), cancellationToken);

    /// <summary>
    /// Edits a user's own name, and their phone number when they are a client (FR-87). Every field
    /// the body omits is left alone (AD-23).
    /// </summary>
    [HttpPatch("{id:int}")]
    public Task<UserProfile> UpdateAsync(
        int id,
        [FromBody] UpdateProfileCommand command,
        CancellationToken cancellationToken) =>
        users.UpdateProfileAsync(new UserId(id), command, cancellationToken);

    /// <summary>
    /// Replaces a user's own password, current password first (FR-88). 204, with no body: a password
    /// has no readable form, so there is nothing to answer with.
    /// </summary>
    [HttpPost("{id:int}/password")]
    public async Task<IActionResult> ChangePasswordAsync(
        int id,
        [FromBody] ChangePasswordCommand command,
        CancellationToken cancellationToken)
    {
        await users.ChangePasswordAsync(new UserId(id), command, cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Replaces the address a user's account signs in with (FR-92). Answers the profile, so the
    /// caller can see the address that was actually stored.
    /// </summary>
    [HttpPost("{id:int}/email")]
    public Task<UserProfile> ChangeEmailAsync(
        int id,
        [FromBody] ChangeEmailCommand command,
        CancellationToken cancellationToken) =>
        users.ChangeEmailAsync(new UserId(id), command, cancellationToken);
}
