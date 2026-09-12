using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using FluentValidation;

namespace DriveTrack.Application.Notifications;

/// <summary>
/// AD-3's pipeline over the notification log, which on a read-only capability is guard → validate →
/// read → map.
/// <para>
/// AD-2: the guard is called through the interface, inline in this method's own body. Folding it
/// into a helper would read no better and would not count — the coverage gate walks the method's IL
/// and does not follow a call it makes.
/// </para>
/// <para>
/// <c>RequireRole(UserRole.Admin)</c> rather than <c>Dispatcher</c>, and the difference matters
/// here: AD-4 makes an administrator satisfy every check by rule inside the guard, so naming
/// <c>Dispatcher</c> would admit both. The log is an operations record — it carries the address of
/// every client the system has written to — and FR-28 addresses it to the administrator.
/// </para>
/// <para>
/// <c>ValidatorExtensions.ValidateAndThrowAsync</c> is called in static form for the reason
/// <c>DeliveryService</c> gives: this file imports both <c>FluentValidation</c> and
/// <c>DriveTrack.Application.Common</c>, and the static call cannot bind to FluentValidation's
/// identically-named method, whose exception carries no <c>ErrorCode</c> and would leave as a 500
/// where 422 was meant.
/// </para>
/// </summary>
public sealed class NotificationLogService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAccessGuard accessGuard,
    IValidator<ListNotificationAttemptsQuery> listValidator) : INotificationLogService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationAttemptSummary>> ListAsync(
        ListNotificationAttemptsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        accessGuard.RequireRole(UserRole.Admin);

        // NFR-27: refused before any query is built. After the guard, so a caller who may not read
        // the log is told that rather than told their paging is wrong.
        await ValidatorExtensions.ValidateAndThrowAsync(listValidator, query, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var attempts = await unitOfWork.NotificationAttempts.ListAsync(
            query.Offset,
            query.Limit,
            cancellationToken);

        // Nothing was written, so the scope is disposed without a commit and the empty transaction
        // rolls back. That is the ordinary read path, not an omission.
        return [.. attempts.Select(Summary)];
    }

    private static NotificationAttemptSummary Summary(NotificationAttempt attempt) =>
        new(
            attempt.Id,
            attempt.DeliveryId,
            attempt.Kind,
            attempt.Recipient,
            attempt.AttemptedAt,
            attempt.Outcome,
            attempt.Error);
}
