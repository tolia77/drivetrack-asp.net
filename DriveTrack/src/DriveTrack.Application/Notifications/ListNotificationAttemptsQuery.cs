namespace DriveTrack.Application.Notifications;

/// <summary>
/// NFR-27's paging over the notification log, expressed as a request rather than as two loose
/// integers — the same shape <c>ListDeliveriesQuery</c> takes, because the rule is the same rule.
/// <para>
/// The log is the list that most needed one: the table grows by a row per status change per
/// delivery and nothing prunes it, so a read with no bound materialises the whole history every
/// time an administrator opens the screen.
/// </para>
/// <para>
/// There is deliberately no sort or filter here. PRD section 8 excludes server-side filtering,
/// sorting and result counts, so the screen shows the page it already fetched; a parameter here
/// would be the first half of a feature the product does not have.
/// </para>
/// <para>
/// Nor is there a scope. The caller does not get to say whose rows it wants: the log is an
/// administrator's whole view, decided by <c>IAccessGuard.RequireRole</c> inside the service (AD-2).
/// </para>
/// </summary>
/// <param name="Offset">Rows to skip. Zero is the first page.</param>
/// <param name="Limit">Rows to take, at most <see cref="ListNotificationAttemptsQueryValidator.MaximumLimit"/>.</param>
public sealed record ListNotificationAttemptsQuery(int Offset, int Limit);
