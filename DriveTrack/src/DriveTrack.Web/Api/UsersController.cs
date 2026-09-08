using DriveTrack.Application.Users;
using DriveTrack.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveTrack.Web.Api;

/// <summary>
/// Reading a profile over REST (FR-5).
/// <para>
/// <c>[Authorize]</c> here is a convenience, not the decision: it stops an anonymous request before
/// it reaches a service, but whether <em>this</em> caller may read <em>that</em> profile is decided
/// by <c>IAccessGuard.RequireSelf</c> inside <see cref="IUserService.GetProfileAsync"/> and nowhere
/// else (AD-2). Deleting the attribute would change the status of an anonymous call and nothing
/// about who is allowed to read what.
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
}
