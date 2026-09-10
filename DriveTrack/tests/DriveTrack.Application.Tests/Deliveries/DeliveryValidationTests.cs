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
            Status = DeliveryStatus.Pending,
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

    /// <inheritdoc cref="Failures" />
    private static string[] UpdateFailures(UpdateDeliveryCommand merged) =>
    [
        .. new UpdateDeliveryCommandValidator()
            .Validate(merged)
            .Errors
            .Select(failure => failure.PropertyName)
            .Distinct(StringComparer.Ordinal),
    ];
}
