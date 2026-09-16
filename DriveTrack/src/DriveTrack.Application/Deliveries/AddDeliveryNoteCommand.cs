using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using FluentValidation;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// A standalone note on a delivery's timeline, changing nothing else (FR-107).
/// <para>
/// The entry it produces carries neither status, which is the shape FR-107 asks for and the shape
/// FR-108 makes a correction out of: the timeline is append-only, so the way to correct a line is
/// to write another one.
/// </para>
/// </summary>
/// <param name="Note">
/// The text. Nullable on the type because the wire may send anything and the command has to carry
/// whatever arrived as far as the validator, which refuses it by name.
/// </param>
public sealed record AddDeliveryNoteCommand(string? Note);

/// <summary>
/// FR-107's one rule: there has to be something to say, and it has to fit the column.
/// <para>
/// <c>NotEmpty</c> rather than <c>NotNull</c>, so a note of spaces is refused as well as a missing
/// one — an entry holding a single space is a line that renders as blank and reads as a correction
/// nobody wrote.
/// </para>
/// </summary>
public sealed class AddDeliveryNoteCommandValidator : AbstractValidator<AddDeliveryNoteCommand>
{
    /// <summary>Declares the rules.</summary>
    public AddDeliveryNoteCommandValidator() =>
        RuleFor(command => command.Note)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED))

            // The same bound and the same measurement as a note riding on a status change: one rule
            // in one place, on the entity that owns the column, so the two routes cannot come to
            // disagree about what fits and neither is the other's dependency.
            .Must(TimelineEntry.NoteFits)
                .WithMessage(nameof(ErrorCode.DELIVERY_NOTE_TOO_LONG));
}
