using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using FluentValidation;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// A request to advance a delivery's lifecycle (FR-32, FR-106).
/// <para>
/// The note rides on the same command rather than being a second call, because FR-106 puts it on
/// the same entry: "delivery failed, nobody at the address" is one fact, and writing it as a status
/// change followed by a note would be two rows that a later reader has to join by their timestamps.
/// It is optional, so the ordinary advance carries none.
/// </para>
/// <para>
/// There is no delivery id here: the id is the route, as it is for every other single-row operation
/// in this capability.
/// </para>
/// </summary>
/// <param name="Status">
/// The status being asked for. Nullable because the wire can omit it, and a non-nullable property
/// would bind an absent field to the enum's first member — <see cref="DeliveryStatus.Pending"/> —
/// and turn "you forgot to say where" into a 409 about a transition the caller never requested.
/// </param>
/// <param name="Note">Free text explaining the change, or null.</param>
public sealed record ChangeDeliveryStatusCommand(DeliveryStatus? Status, string? Note);

/// <summary>
/// That a status was named at all, that it is one the system has, and that the note fits the column.
/// <para>
/// Whether <c>InTransit</c> may follow <c>Pending</c> is deliberately not judged here.
/// <see cref="Delivery.NextStatuses"/> is the single place the lifecycle is declared (AD-10);
/// restating it as a validator rule would be the second copy that decision exists to prevent, and
/// it would report a 422 where FR-32 asks for a 409. "Is this a member of the enum" is not a
/// transition rule — it is the same kind of claim as "is this field present".
/// </para>
/// <para>
/// Both of those claims have to be made here, because neither the binder nor the JSON reader makes
/// them: the shell registers <c>JsonStringEnumConverter</c> with its default
/// <c>allowIntegerValues: true</c>, so <c>{"status": 42}</c> deserializes to an undefined member
/// rather than being refused as malformed.
/// </para>
/// </summary>
public sealed class ChangeDeliveryStatusCommandValidator : AbstractValidator<ChangeDeliveryStatusCommand>
{
    /// <summary>Declares the rules.</summary>
    public ChangeDeliveryStatusCommandValidator()
    {
        RuleFor(command => command.Status)
            .NotNull().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .IsInEnum().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        // Bounded only, and not required: a status change explains itself, and demanding a sentence
        // for every one would put "ok" in a thousand rows.
        // The rule lives on the entity that owns the column, so the standalone-note path applies the
        // same one without either validator depending on the other.
        RuleFor(command => command.Note)
            .Must(TimelineEntry.NoteFits).WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));
    }
}
