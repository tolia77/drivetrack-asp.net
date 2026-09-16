using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Notifications;

/// <summary>
/// NFR-27's "validated rather than passed through unchecked", over the notification log.
/// <para>
/// Every message is an <see cref="ErrorCode"/> name applied per rule, never once at the end of a
/// chain — the distinction <c>RegisterClientCommandValidator</c> explains, and the reason a limit
/// of two million is a 422 naming the field rather than a query the database is asked to run.
/// </para>
/// </summary>
public sealed class ListNotificationAttemptsQueryValidator
    : AbstractValidator<ListNotificationAttemptsQuery>
{
    /// <summary>The largest page a caller may ask for, which is also the default page size.</summary>
    public const int MaximumLimit = 100;

    /// <summary>Declares the rules.</summary>
    public ListNotificationAttemptsQueryValidator()
    {
        // The two paging codes rather than a code per query: Offset and Limit are the same two
        // values on every list in the system, no screen offers a box for either, and a caller who
        // typed them into a URL is served by one sentence naming the bound they broke.
        RuleFor(query => query.Offset)
            .GreaterThanOrEqualTo(0).WithMessage(nameof(ErrorCode.COMMON_PAGING_OFFSET_INVALID));

        RuleFor(query => query.Limit)
            .GreaterThan(0).WithMessage(nameof(ErrorCode.COMMON_PAGING_LIMIT_INVALID))
            .LessThanOrEqualTo(MaximumLimit)
                .WithMessage(nameof(ErrorCode.COMMON_PAGING_LIMIT_INVALID));
    }
}
