using DriveTrack.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveTrack.Web.Api;

/// <summary>
/// The two anonymous account endpoints (FR-1, FR-4).
/// <para>
/// Thin on purpose (NFR-7): bind, call the service, return the DTO. The envelope comes from the
/// global result filter and every failure from the branch middleware, so there is no status code,
/// no message and no <c>try</c> in this file — an adapter that started making those decisions would
/// be a second place where the contract is decided (AD-7).
/// </para>
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController(IUserService users) : ControllerBase
{
    /// <summary>Opens a client account and returns the session it starts (FR-1).</summary>
    [HttpPost("register")]
    public Task<AuthenticatedSession> RegisterAsync(
        [FromBody] RegisterClientCommand command,
        CancellationToken cancellationToken) =>
        users.RegisterClientAsync(command, cancellationToken);

    /// <summary>Exchanges credentials for a session (FR-4).</summary>
    [HttpPost("sign-in")]
    public Task<AuthenticatedSession> SignInAsync(
        [FromBody] SignInCommand command,
        CancellationToken cancellationToken) =>
        users.SignInAsync(command, cancellationToken);
}
