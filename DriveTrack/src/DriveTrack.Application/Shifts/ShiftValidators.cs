using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Shifts;

/// <summary>
/// FR-111 over a shift dispatch is recording after the fact: a window may not run backwards.
/// <para>
/// The rule is stated on <c>EndedAt</c> rather than on the pair, because that is the field a
/// dispatcher entered wrongly — <c>StartedAt</c> is where the parallel rule lands on the edit path,
/// for the reason <see cref="ShiftStateValidator"/> gives. NFR-4 promises <c>error.fields</c> keys a
/// form can attach a message to, so which property a rule hangs off decides which box gets marked.
/// </para>
/// <para>
/// Strictly greater, not greater-or-equal: a shift that started and ended at the same instant is a
/// stretch of time of no length, and recording one is a mistake rather than a fact.
/// </para>
/// <para>
/// There is nothing here about the driver or about a start in the future. The driver is a
/// <c>DriverId</c>, so the adapter refuses anything that is not a number at the model binder, and
/// the service refuses a driver no row holds; a start in the future is a shift somebody rostered
/// ahead, which the requirements neither forbid nor ask about.
/// </para>
/// <para>
/// Discovered by <c>AddValidatorsFromAssembly</c>, so there is no registration to add.
/// </para>
/// </summary>
public sealed class CreateShiftCommandValidator : AbstractValidator<CreateShiftCommand>
{
    /// <summary>Declares the rules.</summary>
    public CreateShiftCommandValidator() =>
        RuleFor(command => command.EndedAt)
            .Must((command, endedAt) => endedAt is null || endedAt > command.StartedAt)
                .WithMessage(nameof(ErrorCode.SHIFT_ENDED_BEFORE_STARTED));
}

/// <summary>
/// The same rule, applied to the state the shift will hold rather than to the payload (FR-111).
/// <para>
/// AD-23: this validator is only ever handed <c>UpdateShiftCommand.MergedOnto</c>'s result, in which
/// both fields are filled in from the stored row. That is what makes the case FR-111 is most easily
/// regressed by a refusal: <c>PUT {"startedAt": T+9h}</c> against a shift of <c>[T, T+8h]</c>
/// carries no end at all, so a validator reading the payload would see one instant and nothing to
/// compare it with.
/// </para>
/// <para>
/// One predicate, stated twice — once against each end of the window — and that is about which box
/// a form marks rather than about the rule. A merged state can be reached from either side: an edit
/// that moves only the start, and an edit that moves only the end. Hanging the rule off one property
/// would answer every backwards window by naming that one, so half the callers would be told the
/// box they never touched was wrong while the box they did touch stayed unflagged (NFR-4). Naming
/// both is the honest answer, because a window that runs backwards really is a disagreement between
/// two values rather than a fault in either.
/// </para>
/// <para>
/// The create path states it once, off <c>EndedAt</c>, and that is not an inconsistency: a create
/// always carries both instants and the end is the one a dispatcher got wrong, so there is no second
/// caller for a second name to serve.
/// </para>
/// </summary>
public sealed class ShiftStateValidator : AbstractValidator<ShiftState>
{
    /// <summary>Declares the rules.</summary>
    public ShiftStateValidator()
    {
        RuleFor(state => state.StartedAt)
            .Must((state, startedAt) => state.EndedAt is null || state.EndedAt > startedAt)
                .WithMessage(nameof(ErrorCode.SHIFT_ENDED_BEFORE_STARTED));

        RuleFor(state => state.EndedAt)
            .Must((state, endedAt) => endedAt is null || endedAt > state.StartedAt)
                .WithMessage(nameof(ErrorCode.SHIFT_ENDED_BEFORE_STARTED));
    }
}

/// <summary>
/// NFR-27's "validated rather than passed through unchecked", over the shift list.
/// <para>
/// Every message is an <see cref="ErrorCode"/> name applied per rule, for the reason
/// <c>ListDeliveriesQueryValidator</c> states: a limit of two million is a 422 naming the field
/// rather than a query the database is asked to run.
/// </para>
/// </summary>
public sealed class ListShiftsQueryValidator : AbstractValidator<ListShiftsQuery>
{
    /// <summary>The largest page a caller may ask for, which is also the default page size.</summary>
    public const int MaximumLimit = 100;

    /// <summary>Declares the rules.</summary>
    public ListShiftsQueryValidator()
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
