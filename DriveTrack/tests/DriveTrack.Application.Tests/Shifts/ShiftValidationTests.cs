using DriveTrack.Application.Common;
using DriveTrack.Application.Shifts;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Shifts;

namespace DriveTrack.Application.Tests.Shifts;

/// <summary>
/// The validation rows of story 4.2's edge-case matrix, asserted against the three validators that
/// carry them.
/// <para>
/// Driven through <c>ValidatorExtensions.ValidateAndThrowAsync</c> rather than through
/// <c>IValidator.Validate</c>, because the seam is half of what is under test: the static form is
/// what turns a failure into a <see cref="ValidationException"/> carrying an
/// <see cref="ErrorCode"/> and a field list, and FluentValidation's identically named extension
/// would throw a type the adapter answers 500 for (AD-9, NFR-4).
/// </para>
/// <para>
/// Asserted here rather than over HTTP because that is where the rules live and where it is
/// cheapest to be exhaustive. The integration suite still drives one of each end to end, which is
/// what proves the rules are actually reached.
/// </para>
/// <para>
/// Both write validators are exercised, and that is not duplication for its own sake. They judge
/// different things — a payload and AD-23's merged state — and the edit is the path where FR-111
/// going missing has no other symptom: the create path would still refuse a backwards window, so a
/// screen test would keep passing while <c>PUT</c> wrote one.
/// </para>
/// </summary>
public class ShiftValidationTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_window_that_ends_after_it_starts_is_accepted()
    {
        // The ordinary shift, and the reason the refusals below mean anything: a rule that refused
        // everything would pass every assertion about refusals.
        await ValidatorExtensions.ValidateAndThrowAsync(
            new CreateShiftCommandValidator(),
            Command(Start, Start.AddHours(8)),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_shift_that_has_not_ended_is_accepted()
    {
        // FR-110's open shift: null is a state the row may hold, not a missing value. A NotNull rule
        // here would make going on duty impossible to express.
        await ValidatorExtensions.ValidateAndThrowAsync(
            new CreateShiftCommandValidator(),
            Command(Start, endedAt: null),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_window_that_ends_before_it_starts_is_refused_naming_the_end()
    {
        // The matrix's "create with bad window" row. The field matters as much as the refusal: NFR-4
        // promises error.fields keys a form can attach a message to, and on a create both instants
        // are on screen with the end being the one the dispatcher got wrong.
        var failure = await Refused(Command(Start, Start.AddHours(-1)));

        Assert.Equal(ErrorCode.COMMON_VALIDATION_FAILED, failure.Code);
        Assert.Contains(
            nameof(CreateShiftCommand.EndedAt),
            failure.FieldErrors.Select(field => field.Field));
        Assert.Contains(
            nameof(ErrorCode.SHIFT_ENDED_BEFORE_STARTED),
            failure.FieldErrors.Select(field => field.MessageKey));
    }

    [Fact]
    public async Task A_shift_of_no_length_at_all_is_refused()
    {
        // Strictly greater, not greater-or-equal. A shift that started and ended at the same instant
        // is a stretch of time of no length, and recording one is a mistake rather than a fact -
        // which an inclusive comparison would accept without anything else noticing.
        var failure = await Refused(Command(Start, Start));

        Assert.Contains(
            nameof(ErrorCode.SHIFT_ENDED_BEFORE_STARTED),
            failure.FieldErrors.Select(field => field.MessageKey));
    }

    [Fact]
    public async Task An_edit_that_moves_only_the_start_is_judged_on_the_window_it_produces()
    {
        // The matrix's "edit one field only" row, and the rule FR-111 is most easily regressed by.
        // The payload carries one instant and nothing to compare it with, so a validator reading the
        // payload would accept it and store [T+9h, T+8h]: a shift that ends before it begins,
        // assembled out of two individually harmless halves.
        //
        // Both ends are named. A merged state can be reached from either side, so hanging the rule
        // off one property alone would tell half the callers that the box they never touched was
        // wrong while leaving the box they did touch unflagged (NFR-4).
        var merged = new UpdateShiftCommand(
                Optional<DateTimeOffset>.Present(Start.AddHours(9)),
                Optional<DateTimeOffset>.Absent)
            .MergedOnto(Stored(Start, Start.AddHours(8)));

        var failure = await Refused(merged);

        Assert.Equal(ErrorCode.COMMON_VALIDATION_FAILED, failure.Code);
        Assert.Equal(
            [nameof(ShiftState.EndedAt), nameof(ShiftState.StartedAt)],
            failure.FieldErrors.Select(field => field.Field).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(
            nameof(ErrorCode.SHIFT_ENDED_BEFORE_STARTED),
            failure.FieldErrors.Select(field => field.MessageKey));
    }

    [Fact]
    public async Task An_edit_that_moves_only_the_end_is_judged_the_same_way()
    {
        // The mirror of the row above, and the reason the rule is stated against both ends. This
        // caller filled in the end box; a refusal naming only the start would point at a control
        // they never touched and leave the one they got wrong unmarked.
        var merged = new UpdateShiftCommand(
                Optional<DateTimeOffset>.Absent,
                Optional<DateTimeOffset>.Present(Start.AddHours(-1)))
            .MergedOnto(Stored(Start, Start.AddHours(8)));

        var failure = await Refused(merged);

        Assert.Equal(
            [nameof(ShiftState.EndedAt), nameof(ShiftState.StartedAt)],
            failure.FieldErrors.Select(field => field.Field).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task An_edit_that_moves_only_the_start_within_the_window_is_accepted()
    {
        // The other side of the row above. Without it the assertion there would be satisfied by a
        // validator that refused every one-field edit, which is the same defect wearing the opposite
        // sign: a dispatcher correcting a start by ten minutes must not be told to re-enter the end.
        var merged = new UpdateShiftCommand(
                Optional<DateTimeOffset>.Present(Start.AddMinutes(10)),
                Optional<DateTimeOffset>.Absent)
            .MergedOnto(Stored(Start, Start.AddHours(8)));

        await ValidatorExtensions.ValidateAndThrowAsync(
            new ShiftStateValidator(),
            merged,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_edit_that_leaves_an_open_shift_open_is_accepted()
    {
        // An absent end merged onto a row that has none stays none. The state is the open shift's,
        // and open is a state the validator has to accept or a driver on duty could never have their
        // start corrected.
        var merged = new UpdateShiftCommand(
                Optional<DateTimeOffset>.Present(Start.AddHours(1)),
                Optional<DateTimeOffset>.Absent)
            .MergedOnto(Stored(Start, endedAt: null));

        Assert.Null(merged.EndedAt);

        await ValidatorExtensions.ValidateAndThrowAsync(
            new ShiftStateValidator(),
            merged,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void An_absent_field_leaves_the_stored_value_and_an_end_can_never_be_cleared()
    {
        // FR-117 as a property of the merge rather than of any rule. `Optional<DateTimeOffset>` has
        // two cases and only two - a real instant, or "leave it alone" - so there is no value a
        // caller can put in this command that turns a closed shift back into an open one. An
        // `Optional<DateTimeOffset?>` would have had a third, and this assertion is what would fail
        // the day somebody widened the type.
        var stored = Stored(Start, Start.AddHours(8));

        var untouched = new UpdateShiftCommand(
                Optional<DateTimeOffset>.Absent,
                Optional<DateTimeOffset>.Absent)
            .MergedOnto(stored);

        Assert.Equal(stored.StartedAt, untouched.StartedAt);
        Assert.Equal(stored.EndedAt, untouched.EndedAt);

        // And the only other case the type has: a present end is an instant, never a clear.
        var moved = new UpdateShiftCommand(
                Optional<DateTimeOffset>.Absent,
                Optional<DateTimeOffset>.Present(Start.AddHours(9)))
            .MergedOnto(stored);

        Assert.Equal(Start.AddHours(9), moved.EndedAt);
    }

    [Fact]
    public async Task The_default_page_is_accepted_and_anything_outside_the_bounds_is_not()
    {
        // NFR-27: the page the screens and the controller both ask for is inside the bounds, and a
        // limit of two million is a 422 naming the field rather than a query the database is run.
        var validator = new ListShiftsQueryValidator();
        var cancellationToken = TestContext.Current.CancellationToken;

        await ValidatorExtensions.ValidateAndThrowAsync(
            validator,
            new ListShiftsQuery(0, ListShiftsQueryValidator.MaximumLimit, null),
            cancellationToken);

        // A driver filter is a filter and never a bound: dispatch narrows with it, and a driver's
        // own narrowing overrides it in the service. Nothing here refuses one.
        await ValidatorExtensions.ValidateAndThrowAsync(
            validator,
            new ListShiftsQuery(0, 10, 7),
            cancellationToken);

        Assert.NotEmpty(validator.Validate(new ListShiftsQuery(-1, 10, null)).Errors);
        Assert.NotEmpty(validator.Validate(new ListShiftsQuery(0, 0, null)).Errors);
        Assert.NotEmpty(validator
            .Validate(new ListShiftsQuery(0, ListShiftsQueryValidator.MaximumLimit + 1, null))
            .Errors);
    }

    private static CreateShiftCommand Command(DateTimeOffset startedAt, DateTimeOffset? endedAt) =>
        new(new DriverId(4), startedAt, endedAt);

    /// <summary>A stored row for the merge to be applied to.</summary>
    private static Shift Stored(DateTimeOffset startedAt, DateTimeOffset? endedAt) =>
        new()
        {
            Id = 11,
            DriverId = new DriverId(4),
            StartedAt = startedAt,
            EndedAt = endedAt,
        };

    private static async Task<ValidationException> Refused(CreateShiftCommand command) =>
        await Assert.ThrowsAsync<ValidationException>(
            () => ValidatorExtensions.ValidateAndThrowAsync(
                new CreateShiftCommandValidator(),
                command,
                TestContext.Current.CancellationToken));

    private static async Task<ValidationException> Refused(ShiftState state) =>
        await Assert.ThrowsAsync<ValidationException>(
            () => ValidatorExtensions.ValidateAndThrowAsync(
                new ShiftStateValidator(),
                state,
                TestContext.Current.CancellationToken));
}
