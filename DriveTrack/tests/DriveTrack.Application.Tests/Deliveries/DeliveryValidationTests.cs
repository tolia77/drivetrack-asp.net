using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Domain.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using FluentValidation;

namespace DriveTrack.Application.Tests.Deliveries;

/// <summary>
/// The rows of story 5.1's edge-case matrix that a validator decides, plus AD-23's merge.
/// <para>
/// Asserted here rather than over HTTP because these are rules about a value, and a suite that
/// needed Docker to find out that a weight of zero is refused would be a suite nobody runs while
/// they are writing the rule. The endpoint suite asserts that the refusal reaches the wire with the
/// right status and field name; this asserts that it is a refusal at all.
/// </para>
/// </summary>
public class DeliveryValidationTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly LocationInput Kyiv = new(50.4501, 30.5234);

    private static readonly LocationInput Lviv = new(49.8397, 24.0297);

    // =====================================================================================
    // Creating
    // =====================================================================================

    [Fact]
    public void The_minimum_delivery_is_valid()
    {
        // The matrix's first row: two points, a description and a weight. No driver, no client, no
        // notes and no window - FR-16 makes every one of those optional, and a validator that
        // quietly required one would make the ordinary case impossible.
        Assert.Empty(Failures(Create()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_weight_that_is_not_positive_is_refused_naming_the_field(decimal weight)
    {
        // FR-102. The check constraint refuses this too, and with no field list at all - SQLSTATE
        // 23514 carries a constraint name, not a property - so the validator is what turns it into
        // something a form can attach to an input (NFR-4).
        Assert.Contains(
            nameof(CreateDeliveryCommand.PackageWeightKg),
            Failures(Create(weight: weight)));
    }

    [Fact]
    public void A_weight_wider_than_the_column_is_refused_before_the_commit()
    {
        // numeric(10, 3) overflows rather than truncates, and an overflow at the commit is a 500.
        Assert.Contains(
            nameof(CreateDeliveryCommand.PackageWeightKg),
            Failures(Create(weight: 10_000_000m)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_package_details_are_refused(string? details)
    {
        Assert.Contains(
            nameof(CreateDeliveryCommand.PackageDetails),
            Failures(Create(details: details)));
    }

    [Fact]
    public void Package_details_longer_than_the_column_are_refused()
    {
        Assert.Contains(
            nameof(CreateDeliveryCommand.PackageDetails),
            Failures(Create(details: new string('x', 1001))));
    }

    [Fact]
    public void Notes_longer_than_the_column_are_refused_and_shorter_ones_are_not()
    {
        Assert.Contains(
            nameof(CreateDeliveryCommand.DeliveryNotes),
            Failures(Create(notes: new string('x', 1001))));

        Assert.Empty(Failures(Create(notes: new string('x', 1000))));
    }

    [Fact]
    public void An_inverted_window_is_refused()
    {
        // FR-100, and exactly what ck_deliveries_delivery_window says.
        Assert.Contains(
            nameof(CreateDeliveryCommand.WindowLatestAt),
            Failures(Create(earliest: Noon.AddHours(2), latest: Noon)));
    }

    [Fact]
    public void A_window_whose_bounds_are_equal_is_refused()
    {
        // The boundary the constraint draws is strict inequality, and a window of zero length is a
        // window nothing can be delivered inside. Asserted separately because a `<=` here would
        // pass every other case in this file.
        Assert.Contains(
            nameof(CreateDeliveryCommand.WindowLatestAt),
            Failures(Create(earliest: Noon, latest: Noon)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Either_bound_may_stand_alone_and_both_may_be_absent(bool earliest, bool latest)
    {
        // The other half of FR-100, and the half a "both must be present" rule would break: a
        // dispatcher who knows only the deadline must be able to record only the deadline.
        Assert.Empty(Failures(Create(
            earliest: earliest ? Noon : null,
            latest: latest ? Noon.AddHours(2) : null)));
    }

    [Fact]
    public void An_ordered_window_is_accepted()
    {
        Assert.Empty(Failures(Create(earliest: Noon, latest: Noon.AddHours(2))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(91d)]
    [InlineData(-91d)]
    [InlineData(double.NaN)]
    public void A_pickup_latitude_outside_the_map_is_refused_naming_the_location(double? latitude)
    {
        // Refused here rather than by MapLocation's constructor, and that is the point: a throwing
        // constructor during model binding lands in the suppressed model state and answers 500,
        // where NFR-4 promises a 422 naming the field. NaN is in the list because it fails every
        // comparison, so a range written as two negations would let it through.
        var failures = Failures(Create(pickup: new LocationInput(latitude, 30.5234)));

        Assert.Contains(failures, failure =>
            failure.StartsWith(nameof(CreateDeliveryCommand.Pickup), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(181d)]
    [InlineData(-181d)]
    [InlineData(double.NaN)]
    public void A_dropoff_longitude_outside_the_map_is_refused_naming_the_location(double? longitude)
    {
        var failures = Failures(Create(dropoff: new LocationInput(49.8397, longitude)));

        Assert.Contains(failures, failure =>
            failure.StartsWith(nameof(CreateDeliveryCommand.Dropoff), StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_location_is_refused_once_by_name()
    {
        // Reported by the presence rule and not a second time by the range rules: a child validator
        // is skipped for a null property, which is why the range predicates read a present value.
        // Written out rather than through the helper, because "no pickup at all" is precisely the
        // case a defaulted parameter would fill in.
        var command = new CreateDeliveryCommand(
            null,
            Lviv,
            "Одна палета",
            12.5m,
            null,
            null,
            null,
            null,
            null);

        Assert.Equal(new[] { nameof(CreateDeliveryCommand.Pickup) }, Failures(command));
    }

    // =====================================================================================
    // Requesting (story 7.4)
    //
    // FR-89's matrix, over the five fields a client actually sends. The rules are the create path's
    // and the assertions are written out again on purpose: "a client's 422 names the same field a
    // dispatcher's does" is the claim, and a claim about two validators cannot be made by testing
    // one of them.
    // =====================================================================================

    [Fact]
    public void The_minimum_request_is_valid()
    {
        // Two points, a description and a weight - the same minimum a create has, minus the four
        // fields the command does not carry. There is nothing to assert about a driver, a client or
        // a window here, because there is no field a test could set.
        Assert.Empty(Failures(Request()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_requested_weight_that_is_not_positive_is_refused_naming_the_field(decimal weight)
    {
        // FR-102 reaches the request path unchanged: the column and its check constraint do not
        // care which endpoint the row arrived through.
        Assert.Contains(
            nameof(RequestDeliveryCommand.PackageWeightKg),
            Failures(Request(weight: weight)));
    }

    [Fact]
    public void A_requested_weight_wider_than_the_column_is_refused_before_the_commit()
    {
        Assert.Contains(
            nameof(RequestDeliveryCommand.PackageWeightKg),
            Failures(Request(weight: CreateDeliveryCommandValidator.PackageWeightMaximum + 0.001m)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_requested_package_details_are_refused(string? details)
    {
        Assert.Contains(
            nameof(RequestDeliveryCommand.PackageDetails),
            Failures(Request(details: details)));
    }

    [Fact]
    public void Requested_package_details_longer_than_the_column_are_refused()
    {
        Assert.Contains(
            nameof(RequestDeliveryCommand.PackageDetails),
            Failures(Request(
                details: new string('я', CreateDeliveryCommandValidator.PackageDetailsMaximumLength + 1))));
    }

    [Fact]
    public void Requested_notes_longer_than_the_column_are_refused_and_the_longest_allowed_is_not()
    {
        // The boundary in both directions. A one-sided assertion passes for a validator that
        // refuses every note, which is the mistake worth catching.
        Assert.Contains(
            nameof(RequestDeliveryCommand.DeliveryNotes),
            Failures(Request(
                notes: new string('я', CreateDeliveryCommandValidator.DeliveryNotesMaximumLength + 1))));

        Assert.Empty(Failures(Request(
            notes: new string('я', CreateDeliveryCommandValidator.DeliveryNotesMaximumLength))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_requested_note_is_accepted(string? notes)
    {
        // FR-17: notes are optional, and whitespace is the service's business to blank rather than
        // the validator's to refuse.
        Assert.Empty(Failures(Request(notes: notes)));
    }

    [Fact]
    public void A_request_with_no_pickup_is_refused_once_by_name()
    {
        // Written out rather than through the helper, because "no pickup at all" is precisely the
        // case a defaulted parameter would fill in.
        var command = new RequestDeliveryCommand(null, Lviv, "Одна палета", 12.5m, null);

        Assert.Equal(new[] { nameof(RequestDeliveryCommand.Pickup) }, Failures(command));
    }

    [Fact]
    public void A_request_with_no_dropoff_is_refused_once_by_name()
    {
        var command = new RequestDeliveryCommand(Kyiv, null, "Одна палета", 12.5m, null);

        Assert.Equal(new[] { nameof(RequestDeliveryCommand.Dropoff) }, Failures(command));
    }

    [Fact]
    public void A_requested_coordinate_outside_the_map_is_refused_naming_the_location()
    {
        // The same child validator the create path applies, reported under the location's own name
        // rather than under "Latitude" - which is the field a form has an input for.
        var failures = Failures(Request(pickup: new LocationInput(91, 30.5234)));

        Assert.Contains(failures, failure =>
            failure.StartsWith(nameof(RequestDeliveryCommand.Pickup), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(181d)]
    [InlineData(-181d)]
    [InlineData(double.NaN)]
    public void A_requested_dropoff_outside_the_map_is_refused_naming_the_location(double? longitude)
    {
        // The dropoff's own rule, because the child validator is applied to the two points as two
        // separate RuleFor blocks - and a block pasted from the pickup's keeps compiling with
        // `command.Pickup!` in it. The pickup test above passes either way; this one does not.
        var failures = Failures(Request(dropoff: new LocationInput(49.8397, longitude)));

        Assert.Contains(failures, failure =>
            failure.StartsWith(nameof(RequestDeliveryCommand.Dropoff), StringComparison.Ordinal));
    }

    // =====================================================================================
    // Merging, then validating the merged state (AD-23)
    // =====================================================================================

    [Fact]
    public void An_absent_field_merges_to_what_the_row_already_holds()
    {
        var delivery = Stored();

        var merged = new UpdateDeliveryCommand(
            Optional<LocationInput>.Absent,
            Optional<LocationInput>.Absent,
            Optional<string>.Absent,
            Optional<decimal>.Absent,
            Optional<string?>.Absent,
            Optional<DateTimeOffset?>.Absent,
            Optional<DateTimeOffset?>.Absent,
            Optional<int?>.Absent,
            Optional<int?>.Absent).MergedOnto(delivery);

        Assert.Equal(delivery.PackageDetails, merged.PackageDetails.Value);
        Assert.Equal(delivery.PackageWeightKg, merged.PackageWeightKg.Value);
        Assert.Equal(delivery.DeliveryNotes, merged.DeliveryNotes.Value);
        Assert.Equal(delivery.WindowEarliestAt, merged.WindowEarliestAt.Value);
        Assert.Equal(delivery.WindowLatestAt, merged.WindowLatestAt.Value);
        Assert.Equal(delivery.DriverId?.Value, merged.DriverId.Value);
        Assert.Equal(delivery.ClientId?.Value, merged.ClientId.Value);

        // The location merges to the stored coordinates, and to those only: a LocationInput is
        // coordinates, and the address is a cache they own (DR-11).
        Assert.Equal(delivery.PickupLocation.Latitude, merged.Pickup.Value!.Latitude!.Value);
        Assert.Equal(delivery.PickupLocation.Longitude, merged.Pickup.Value!.Longitude!.Value);
    }

    [Fact]
    public void A_present_null_clears_what_may_be_cleared()
    {
        // The case a plain nullable field cannot express, and the reason Optional exists: FR-16
        // makes an unassigned delivery legal, so `driverId: null` is an operation a dispatcher
        // performs rather than a malformed payload.
        var merged = Update(
            driverId: Optional<int?>.Present(null),
            clientId: Optional<int?>.Present(null),
            notes: Optional<string?>.Present(null),
            earliest: Optional<DateTimeOffset?>.Present(null),
            latest: Optional<DateTimeOffset?>.Present(null)).MergedOnto(Stored());

        Assert.Null(merged.DriverId.Value);
        Assert.Null(merged.ClientId.Value);
        Assert.Null(merged.DeliveryNotes.Value);
        Assert.Null(merged.WindowEarliestAt.Value);
        Assert.Null(merged.WindowLatestAt.Value);
    }

    [Fact]
    public void Every_field_of_a_merged_command_is_present()
    {
        // What makes the update validator's `.Value` reads safe: it is only ever handed a merge,
        // and a merge fills in whatever the caller left out.
        var merged = Update().MergedOnto(Stored());

        Assert.True(merged.Pickup.HasValue);
        Assert.True(merged.Dropoff.HasValue);
        Assert.True(merged.PackageDetails.HasValue);
        Assert.True(merged.PackageWeightKg.HasValue);
        Assert.True(merged.DeliveryNotes.HasValue);
        Assert.True(merged.WindowEarliestAt.HasValue);
        Assert.True(merged.WindowLatestAt.HasValue);
        Assert.True(merged.DriverId.HasValue);
        Assert.True(merged.ClientId.HasValue);
    }

    [Fact]
    public void An_update_naming_only_the_weight_is_judged_on_the_merged_row()
    {
        // AD-23's whole point: the payload mentions one field, and the other eight are the row's.
        // Validating the payload would refuse this for a package description it never sent.
        var merged = Update(weight: Optional<decimal>.Present(20m)).MergedOnto(Stored());

        Assert.Empty(UpdateFailures(merged));
        Assert.Equal(20m, merged.PackageWeightKg.Value);
    }

    [Fact]
    public void An_update_that_would_invert_the_stored_window_is_refused()
    {
        // The rule the merge exists for. The payload carries one bound; the other is already on the
        // row, and only the pair can be judged.
        var stored = Stored(earliest: Noon, latest: Noon.AddHours(2));

        var merged = Update(latest: Optional<DateTimeOffset?>.Present(Noon.AddHours(-1)))
            .MergedOnto(stored);

        Assert.Contains(nameof(UpdateDeliveryCommand.WindowLatestAt), UpdateFailures(merged));
    }

    [Fact]
    public void Clearing_one_stored_bound_leaves_the_other_valid()
    {
        // FR-100 again, through the merge: a window with one bound is a window.
        var stored = Stored(earliest: Noon, latest: Noon.AddHours(2));

        var merged = Update(earliest: Optional<DateTimeOffset?>.Present(null)).MergedOnto(stored);

        Assert.Empty(UpdateFailures(merged));
    }

    [Fact]
    public void An_update_may_not_blank_the_package_details()
    {
        // Present-and-empty is a value the caller sent, not a field they omitted - which is exactly
        // the distinction the merge preserves, asserted from the refusing side.
        var merged = Update(details: Optional<string>.Present(string.Empty)).MergedOnto(Stored());

        Assert.Contains(nameof(UpdateDeliveryCommand.PackageDetails), UpdateFailures(merged));
    }

    [Fact]
    public void An_update_may_not_clear_a_location()
    {
        // The one present-null the update refuses: a delivery has to be collected somewhere.
        var merged = Update(pickup: Optional<LocationInput>.Present(null)).MergedOnto(Stored());

        Assert.Contains(nameof(UpdateDeliveryCommand.Pickup), UpdateFailures(merged));
    }

    [Fact]
    public void An_update_moving_a_point_off_the_map_is_refused()
    {
        var merged = Update(pickup: Optional<LocationInput>.Present(new LocationInput(91, 30.5234)))
            .MergedOnto(Stored());

        Assert.Contains(UpdateFailures(merged), failure =>
            failure.StartsWith(nameof(UpdateDeliveryCommand.Pickup), StringComparison.Ordinal));
    }

    // =====================================================================================
    // Timeline notes (story 5.3)
    //
    // FR-106 puts a note on a status change and FR-107 makes one an entry in its own right, so the
    // same text has two commands with deliberately different rules: optional on the change, required
    // on the standalone note. Both bound by the column.
    // =====================================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_standalone_note_with_nothing_in_it_is_refused_naming_the_field(string? note)
    {
        // FR-107 asks for a note, and an entry holding a single space is a line that renders as
        // blank and reads as a correction nobody wrote. The field name is what lets the form attach
        // the refusal to the box that produced it (NFR-4).
        Assert.Contains(nameof(AddDeliveryNoteCommand.Note), NoteFailures(note));
    }

    [Fact]
    public void A_note_the_length_of_the_column_is_accepted_and_one_character_more_is_not()
    {
        // The boundary in both directions. A one-sided assertion passes for a validator that
        // refuses every note, and for one that refuses none.
        Assert.Empty(NoteFailures(new string('я', 1000)));
        Assert.Contains(nameof(AddDeliveryNoteCommand.Note), NoteFailures(new string('я', 1001)));
    }

    [Fact]
    public void A_note_is_measured_the_way_it_is_stored()
    {
        // The service writes the trimmed text, so a note that fits once trimmed must not be refused
        // for whitespace that never reaches the column. Both commands ask the same question through
        // the same method, so both are asserted.
        var padded = "  " + new string('я', 1000) + "  ";

        Assert.Empty(NoteFailures(padded));
        Assert.Empty(StatusFailures(new ChangeDeliveryStatusCommand(DeliveryStatus.InTransit, padded)));
    }

    [Fact]
    public void A_status_change_that_names_no_status_is_refused_naming_the_field()
    {
        // The property is nullable so an omitted field arrives as absent rather than binding to the
        // enum's first member: without this rule, "you forgot to say where" would be answered with a
        // 409 about a Pending-to-Pending transition the caller never asked for.
        Assert.Contains(
            nameof(ChangeDeliveryStatusCommand.Status),
            StatusFailures(new ChangeDeliveryStatusCommand(null, null)));
    }

    [Fact]
    public void A_status_the_enum_does_not_declare_is_refused_naming_the_field()
    {
        // The JSON reader is configured to accept integers for an enum, so an undefined member
        // deserializes cleanly and reaches the validator. Refused here, as a 422 about the payload,
        // rather than falling through to the lifecycle and being reported as a 409 about the row.
        Assert.Contains(
            nameof(ChangeDeliveryStatusCommand.Status),
            StatusFailures(new ChangeDeliveryStatusCommand((DeliveryStatus)42, null)));
    }

    [Fact]
    public void A_status_change_needs_no_note_at_all()
    {
        // FR-106 makes the note optional on a change: a status advances of its own accord, and
        // demanding a sentence for every one would put "ok" in a thousand rows.
        Assert.Empty(StatusFailures(new ChangeDeliveryStatusCommand(DeliveryStatus.InTransit, null)));
        Assert.Empty(StatusFailures(new ChangeDeliveryStatusCommand(DeliveryStatus.InTransit, "   ")));
    }

    [Fact]
    public void A_status_change_carrying_an_over_long_note_is_refused_naming_the_field()
    {
        Assert.Empty(StatusFailures(
            new ChangeDeliveryStatusCommand(DeliveryStatus.Failed, new string('я', 1000))));

        Assert.Contains(
            nameof(ChangeDeliveryStatusCommand.Note),
            StatusFailures(new ChangeDeliveryStatusCommand(DeliveryStatus.Failed, new string('я', 1001))));
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.InTransit)]
    [InlineData(DeliveryStatus.Delivered)]
    [InlineData(DeliveryStatus.Failed)]
    public void The_validator_judges_no_transition(DeliveryStatus status)
    {
        // AD-10 keeps the transition table in one place, and this is the other half of that claim:
        // asking for Delivered out of nowhere is a valid command that the lifecycle then refuses
        // with a 409 (FR-32). A rule here would report it as a 422 and be the second copy of the
        // table besides.
        Assert.Empty(StatusFailures(new ChangeDeliveryStatusCommand(status, null)));
    }

    // =====================================================================================
    // Paging
    // =====================================================================================

    [Theory]
    [InlineData(0, 100, true)]
    [InlineData(0, 1, true)]
    [InlineData(50, 100, true)]
    [InlineData(-1, 100, false)]
    [InlineData(0, 0, false)]
    [InlineData(0, 101, false)]
    public void The_paging_parameters_are_validated_before_a_query_is_built(
        int offset,
        int limit,
        bool valid)
    {
        // NFR-27: a limit of two million is refused here rather than handed to the database, and a
        // limit of zero is a refusal rather than "give me nothing".
        var result = new ListDeliveriesQueryValidator()
            .Validate(new ListDeliveriesQuery(offset, limit));

        Assert.Equal(valid, result.IsValid);
    }

    // -------------------------------------------------------------------------------------
    // Arrangement
    // -------------------------------------------------------------------------------------

    private static CreateDeliveryCommand Create(
        LocationInput? pickup = null,
        LocationInput? dropoff = null,
        string? details = "Одна палета",
        decimal weight = 12.5m,
        string? notes = null,
        DateTimeOffset? earliest = null,
        DateTimeOffset? latest = null,
        int? driverId = null,
        int? clientId = null) =>
        new(
            pickup ?? Kyiv,
            dropoff ?? Lviv,
            details,
            weight,
            notes,
            earliest,
            latest,
            driverId,
            clientId);

    /// <summary>
    /// A client's request, with the same defaults <see cref="Create" /> uses so a difference
    /// between the two suites is a difference between the validators rather than the fixtures.
    /// </summary>
    private static RequestDeliveryCommand Request(
        LocationInput? pickup = null,
        LocationInput? dropoff = null,
        string? details = "Одна палета",
        decimal weight = 12.5m,
        string? notes = null) =>
        new(pickup ?? Kyiv, dropoff ?? Lviv, details, weight, notes);

    private static UpdateDeliveryCommand Update(
        Optional<LocationInput> pickup = default,
        Optional<LocationInput> dropoff = default,
        Optional<string> details = default,
        Optional<decimal> weight = default,
        Optional<string?> notes = default,
        Optional<DateTimeOffset?> earliest = default,
        Optional<DateTimeOffset?> latest = default,
        Optional<int?> driverId = default,
        Optional<int?> clientId = default) =>
        new(pickup, dropoff, details, weight, notes, earliest, latest, driverId, clientId);

    /// <summary>A stored row, in the state the merge fills absent fields in from.</summary>
    private static Delivery Stored(
        DateTimeOffset? earliest = null,
        DateTimeOffset? latest = null) =>
        new()
        {
            Id = 1,
            DriverId = new DriverId(3),
            ClientId = new ClientId(4),
            PickupLocation = new Location(50.4501, 30.5234, "Київ", Noon),
            DropoffLocation = new Location(49.8397, 24.0297, null, null),
            PackageDetails = "Одна палета",
            PackageWeightKg = 12.5m,
            DeliveryNotes = "Подзвонити",
            WindowEarliestAt = earliest,
            WindowLatestAt = latest,

            // No Status: it has no public setter, and a new delivery is Pending by the entity's own
            // initializer (AD-10). That is the state this merge fixture wants anyway.
            CreatedAt = Noon,
        };

    /// <summary>The names of the fields a create was refused on, in order.</summary>
    private static string[] Failures(CreateDeliveryCommand command) =>
    [
        .. new CreateDeliveryCommandValidator()
            .Validate(command)
            .Errors
            .Select(failure => failure.PropertyName)
            .Distinct(StringComparer.Ordinal),
    ];

    /// <inheritdoc cref="Failures(CreateDeliveryCommand)" />
    private static string[] Failures(RequestDeliveryCommand command) =>
    [
        .. new RequestDeliveryCommandValidator()
            .Validate(command)
            .Errors
            .Select(failure => failure.PropertyName)
            .Distinct(StringComparer.Ordinal),
    ];

    /// <inheritdoc cref="Failures(CreateDeliveryCommand)" />
    private static string[] NoteFailures(string? note) =>
    [
        .. new AddDeliveryNoteCommandValidator()
            .Validate(new AddDeliveryNoteCommand(note))
            .Errors
            .Select(failure => failure.PropertyName)
            .Distinct(StringComparer.Ordinal),
    ];

    /// <inheritdoc cref="Failures(CreateDeliveryCommand)" />
    private static string[] StatusFailures(ChangeDeliveryStatusCommand command) =>
    [
        .. new ChangeDeliveryStatusCommandValidator()
            .Validate(command)
            .Errors
            .Select(failure => failure.PropertyName)
            .Distinct(StringComparer.Ordinal),
    ];

    /// <inheritdoc cref="Failures(CreateDeliveryCommand)" />
    private static string[] UpdateFailures(UpdateDeliveryCommand merged) =>
    [
        .. new UpdateDeliveryCommandValidator()
            .Validate(merged)
            .Errors
            .Select(failure => failure.PropertyName)
            .Distinct(StringComparer.Ordinal),
    ];
}
