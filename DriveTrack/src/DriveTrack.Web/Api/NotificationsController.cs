using DriveTrack.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveTrack.Web.Api;

/// <summary>
/// The notification log over REST (FR-28).
/// <para>
/// <c>[Authorize]</c> carries no roles, like every other controller here. Who may read the log is
/// decided by <c>IAccessGuard</c> inside <see cref="INotificationLogService"/> — it names
/// <c>RequireRole(UserRole.Admin)</c> outright — and an attribute restating that would be a second,
/// differently-worded copy of a rule the service already states precisely (AD-2). The attribute only
/// spares an anonymous caller a round trip through the service.
/// </para>
/// <para>
/// Read-only, and there is no write member to leave out: an attempt is a record of what happened,
/// written by the background runner and removed only by the cascade from its delivery.
/// </para>
/// <para>
/// Thin, like every controller here (NFR-7): bind, call, return. The envelope comes from the global
/// result filter and every failure from the branch middleware, so there is no status code and no
/// <c>try</c> in this file.
/// </para>
/// </summary>
[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class NotificationsController(INotificationLogService notifications) : ControllerBase
{
    /// <summary>
    /// One page of notification attempts, newest first (FR-28). Defaults to the first hundred.
    /// </summary>
    /// <remarks>
    /// The two parameters are nullable so an omitted one takes the default here rather than binding
    /// to zero — a limit of zero is a refusal, and "I did not say" must not mean "give me nothing".
    /// </remarks>
    [HttpGet]
    public Task<IReadOnlyList<NotificationAttemptSummary>> ListAsync(
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        notifications.ListAsync(
            new ListNotificationAttemptsQuery(
                offset ?? 0,
                limit ?? ListNotificationAttemptsQueryValidator.MaximumLimit),
            cancellationToken);
}
