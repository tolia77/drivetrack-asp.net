using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Application.Drivers;
using DriveTrack.Application.Users;
using DriveTrack.Application.Vehicles;
using DriveTrack.Domain.Deliveries;
using FluentValidation;
using FluentValidation.Validators;
using ValidationException = DriveTrack.Application.Common.ValidationException;

namespace DriveTrack.Application.Tests.Common;

/// <summary>
/// What a refused field actually says.
/// <para>
/// A hundred and three rules used to key <c>COMMON_VALIDATION_FAILED</c>, so two thirds of every
/// refusal in the product rendered as one sentence — "Дані запиту не пройшли перевірку." An empty
/// note and a note of 1001 characters — one past the column's thousand — were byte-identical on
/// screen, and the only thing that told a caller which of the two they had sent was the field name
/// beside it. The rules now carry their own codes, and this is what keeps them that way.
/// </para>
/// <para>
/// The sweep below is the standing half: every validator in the layer, every rule, found by
/// reflection rather than from a list, so a rule added later is covered by having been written. The
/// named cases after it are the I/O matrix — a bounded rule naming its limit, a presence rule
/// sharing one code, a paging bound that stays shared because no screen offers a box for it.
/// </para>
/// </summary>
public class FieldMessageTests
{
    /// <summary>Every concrete <see cref="AbstractValidator{T}"/> the application layer declares.</summary>
    private static Type[] ValidatorTypes() =>
        [.. typeof(ErrorCode).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false }
                && typeof(IValidator).IsAssignableFrom(type))];

    /// <summary>Those of them this sweep can construct.</summary>
    private static IValidator[] Validators() =>
        [.. ValidatorTypes()
            .Where(type => type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(type => (IValidator)Activator.CreateInstance(type)!)];

    /// <summary>
    /// Every message key a validator in the layer states, with the rule that states it.
    /// <para>
    /// A <c>SetValidator</c> component is skipped: it delegates rather than refusing, so it has no
    /// message of its own and carries FluentValidation's "no default error message" placeholder.
    /// The rules it delegates to are swept in their own right, because the child validator is a
    /// validator in this assembly like any other.
    /// </para>
    /// </summary>
    private static IEnumerable<(string Validator, string Field, string Key)> Messages() =>
        Validators().SelectMany(validator => validator
            .CreateDescriptor()
            .Rules
            .SelectMany(rule => rule.Components
                .Where(component => component.Validator is not IChildValidatorAdaptor)
                .Select(component => (
                    Validator: validator.GetType().Name,
                    Field: rule.PropertyName ?? string.Empty,
                    Key: component.GetUnformattedErrorMessage()))));

    [Fact]
    public void The_sweep_reaches_every_validator_the_layer_declares()
    {
        // Not merely "there are some". The sweep can only construct a parameterless validator, so a
        // validator that grows a constructor argument would drop out of both sweeps below in
        // silence - and the rules that stopped being checked would be exactly the ones somebody was
        // in the middle of changing. Equality is what makes that a failing build rather than a
        // quietly narrower guarantee.
        var declared = ValidatorTypes();

        Assert.NotEmpty(declared);

        var uninstantiable = declared
            .Where(type => type.GetConstructor(Type.EmptyTypes) is null)
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(uninstantiable);
        Assert.Equal(declared.Length, Validators().Length);
    }

    [Fact]
    public void No_rule_states_the_generic_validation_code_as_its_field_message()
    {
        // The acceptance criterion, said as a test. COMMON_VALIDATION_FAILED is still the code the
        // ValidationException itself carries - that is the 422 on the wire - but no single rule may
        // offer it as the explanation for one field, because it explains nothing about that field.
        var offenders = Messages()
            .Where(message => message.Key == nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .Select(message => $"{message.Validator}.{message.Field}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_rule_states_an_error_code_name_rather_than_a_sentence()
    {
        // The convention the adapter's field localization rests on, swept across the whole layer
        // rather than per validator: a sentence here reaches a user untranslated (NFR-14), and a
        // rule that simply forgot its WithMessage carries FluentValidation's own English.
        var codes = Enum.GetNames<ErrorCode>().ToHashSet(StringComparer.Ordinal);

        var offenders = Messages()
            .Where(message => !codes.Contains(message.Key))
            .Select(message => $"{message.Validator}.{message.Field}: {message.Key}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    // -----------------------------------------------------------------------------------------
    // Bounded rules: a code of their own, naming the limit
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_over_long_value_and_an_empty_one_are_no_longer_the_same_sentence()
    {
        // The matrix's first two rows, against the same box. This is the whole defect: before, both
        // of these produced COMMON_VALIDATION_FAILED on `PackageDetails`, and the screen had one
        // sentence for two different mistakes.
        var empty = await Refuse(
            new CreateDeliveryCommandValidator(),
            Delivery() with { PackageDetails = string.Empty });

        var oversized = await Refuse(
            new CreateDeliveryCommandValidator(),
            Delivery() with
            {
                PackageDetails = new string(
                    'я',
                    CreateDeliveryCommandValidator.PackageDetailsMaximumLength + 1),
            });

        AssertField(empty, "PackageDetails", nameof(ErrorCode.COMMON_FIELD_REQUIRED));
        AssertField(
            oversized,
            "PackageDetails",
            nameof(ErrorCode.DELIVERY_PACKAGE_DETAILS_TOO_LONG));
    }

    [Fact]
    public async Task A_weight_below_the_floor_and_one_above_the_ceiling_are_two_codes()
    {
        // Two ends of one range, and two different things for a dispatcher to do about them.
        var zero = await Refuse(
            new CreateDeliveryCommandValidator(),
            Delivery() with { PackageWeightKg = 0m });

        var huge = await Refuse(
            new CreateDeliveryCommandValidator(),
            Delivery() with
            {
                PackageWeightKg = CreateDeliveryCommandValidator.PackageWeightMaximum + 1m,
            });

        AssertField(
            zero,
            "PackageWeightKg",
            nameof(ErrorCode.DELIVERY_PACKAGE_WEIGHT_NOT_POSITIVE));
        AssertField(
            huge,
            "PackageWeightKg",
            nameof(ErrorCode.DELIVERY_PACKAGE_WEIGHT_TOO_LARGE));
    }

    [Fact]
    public async Task Two_fields_failing_at_once_are_two_codes_on_two_fields()
    {
        // The matrix's third row. NFR-4 wants every offending field reported, and the point of the
        // split codes is that the two entries no longer read the same.
        var failure = await Refuse(
            new CreateVehicleCommandValidator(),
            new CreateVehicleCommand(string.Empty, "AA1234BB", 0m, 0, null));

        AssertField(failure, "Model", nameof(ErrorCode.COMMON_FIELD_REQUIRED));
        AssertField(failure, "CapacityKg", nameof(ErrorCode.FLEET_CAPACITY_NOT_POSITIVE));
    }

    [Fact]
    public async Task An_over_long_note_names_the_column_it_did_not_fit()
    {
        var failure = await Refuse(
            new AddDeliveryNoteCommandValidator(),
            new AddDeliveryNoteCommand(new string('я', TimelineEntry.NoteMaximumLength + 1)));

        AssertField(failure, "Note", nameof(ErrorCode.DELIVERY_NOTE_TOO_LONG));
    }

    [Fact]
    public async Task An_empty_note_is_the_shared_presence_code()
    {
        // The matrix's second row, said the way the spec words it: one code for every bare presence
        // rule, readable because the field travels beside it.
        var failure = await Refuse(new AddDeliveryNoteCommandValidator(), new AddDeliveryNoteCommand(" "));

        AssertField(failure, "Note", nameof(ErrorCode.COMMON_FIELD_REQUIRED));
    }

    [Fact]
    public async Task A_vehicle_is_refused_field_by_field_with_its_own_bounds()
    {
        var model = await Refuse(
            new CreateVehicleCommandValidator(),
            Vehicle() with
            {
                Model = new string('я', CreateVehicleCommandValidator.ModelMaximumLength + 1),
            });

        var plate = await Refuse(
            new CreateVehicleCommandValidator(),
            Vehicle() with
            {
                LicensePlate = new string(
                    'я',
                    CreateVehicleCommandValidator.LicensePlateMaximumLength + 1),
            });

        var capacity = await Refuse(
            new CreateVehicleCommandValidator(),
            Vehicle() with { CapacityKg = CreateVehicleCommandValidator.CapacityMaximum + 1m });

        var mileage = await Refuse(new CreateVehicleCommandValidator(), Vehicle() with { Mileage = -1 });

        AssertField(model, "Model", nameof(ErrorCode.FLEET_MODEL_TOO_LONG));
        AssertField(plate, "LicensePlate", nameof(ErrorCode.FLEET_LICENSE_PLATE_TOO_LONG));
        AssertField(capacity, "CapacityKg", nameof(ErrorCode.FLEET_CAPACITY_TOO_LARGE));
        AssertField(mileage, "Mileage", nameof(ErrorCode.FLEET_MILEAGE_NEGATIVE));
    }

    [Fact]
    public async Task A_licence_number_beyond_the_column_names_the_licence_number()
    {
        var failure = await Refuse(
            new CreateDriverCommandValidator(),
            new CreateDriverCommand(
                "Олена",
                "Петренко",
                "olena@drivetrack.test",
                "New-Passw0rd",
                new string('я', CreateDriverCommandValidator.LicenseNumberMaximumLength + 1),
                null));

        AssertField(failure, "LicenseNumber", nameof(ErrorCode.FLEET_LICENSE_NUMBER_TOO_LONG));
    }

    [Fact]
    public async Task A_name_beyond_the_column_is_told_apart_from_a_name_left_out()
    {
        var missing = await Refuse(
            new CreateDispatcherCommandValidator(),
            new CreateDispatcherCommand(" ", "Ковальчук", "d@drivetrack.test", "New-Passw0rd"));

        var oversized = await Refuse(
            new CreateDispatcherCommandValidator(),
            new CreateDispatcherCommand(
                "Ігор",
                new string('я', RegisterClientCommandValidator.NameMaximumLength + 1),
                "d@drivetrack.test",
                "New-Passw0rd"));

        AssertField(missing, "FirstName", nameof(ErrorCode.COMMON_FIELD_REQUIRED));
        AssertField(oversized, "LastName", nameof(ErrorCode.AUTH_LAST_NAME_TOO_LONG));
    }

    // -----------------------------------------------------------------------------------------
    // Rules whose granularity is deliberately coarser
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, 50, "Offset", nameof(ErrorCode.COMMON_PAGING_OFFSET_INVALID))]
    [InlineData(0, 0, "Limit", nameof(ErrorCode.COMMON_PAGING_LIMIT_INVALID))]
    [InlineData(0, 101, "Limit", nameof(ErrorCode.COMMON_PAGING_LIMIT_INVALID))]
    public async Task A_paging_bound_keeps_a_shared_code(int offset, int limit, string field, string key)
    {
        // The matrix's last row, and the deliberate exception to "one code per bounded rule": no
        // screen offers a box for either of these, so a field-specific sentence would have nothing
        // to sit under and nothing a user could have typed differently.
        var failure = await Refuse(
            new ListDeliveriesQueryValidator(),
            new ListDeliveriesQuery(offset, limit));

        AssertField(failure, field, key);
    }

    [Fact]
    public async Task A_missing_point_and_an_impossible_one_are_two_different_refusals()
    {
        var missing = await Refuse(new CreateDeliveryCommandValidator(), Delivery() with { Pickup = null });

        var impossible = await Refuse(
            new CreateDeliveryCommandValidator(),
            Delivery() with { Pickup = new LocationInput(91, 30.5) });

        AssertField(missing, "Pickup", nameof(ErrorCode.COMMON_FIELD_REQUIRED));

        // The path the child validator writes, which the screen collapses to `Pickup`.
        AssertField(
            impossible,
            "Pickup.Latitude",
            nameof(ErrorCode.COMMON_COORDINATE_OUT_OF_RANGE));
    }

    [Fact]
    public async Task A_status_left_out_and_a_status_that_does_not_exist_are_two_refusals()
    {
        var missing = await Refuse(
            new ChangeDeliveryStatusCommandValidator(),
            new ChangeDeliveryStatusCommand(null, null));

        var unknown = await Refuse(
            new ChangeDeliveryStatusCommandValidator(),
            new ChangeDeliveryStatusCommand((DeliveryStatus)42, null));

        AssertField(missing, "Status", nameof(ErrorCode.COMMON_FIELD_REQUIRED));
        AssertField(unknown, "Status", nameof(ErrorCode.DELIVERY_STATUS_UNKNOWN));
    }

    [Fact]
    public async Task An_address_search_below_the_floor_says_what_the_floor_is()
    {
        var failure = await Refuse(new SearchPlacesQueryValidator(), new SearchPlacesQuery("ки"));

        AssertField(failure, "Query", nameof(ErrorCode.DELIVERY_PLACE_QUERY_TOO_SHORT));
    }

    [Fact]
    public async Task An_inverted_delivery_window_is_reported_on_the_bound_a_dispatcher_would_move()
    {
        var failure = await Refuse(
            new CreateDeliveryCommandValidator(),
            Delivery() with
            {
                WindowEarliestAt = new DateTimeOffset(2026, 9, 16, 18, 0, 0, TimeSpan.Zero),
                WindowLatestAt = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero),
            });

        AssertField(
            failure,
            "WindowLatestAt",
            nameof(ErrorCode.DELIVERY_WINDOW_ENDS_BEFORE_IT_STARTS));
    }

    private static CreateDeliveryCommand Delivery() =>
        new(
            new LocationInput(50.45, 30.52),
            new LocationInput(49.84, 24.03),
            "Коробка",
            12.5m,
            null,
            null,
            null,
            null,
            null);

    private static CreateVehicleCommand Vehicle() => new("Фургон", "AA1234BB", 1200m, 0, null);

    private static async Task<ValidationException> Refuse<T>(IValidator<T> validator, T instance) =>
        await Assert.ThrowsAsync<ValidationException>(
            () => ValidatorExtensions.ValidateAndThrowAsync(
                validator,
                instance,
                TestContext.Current.CancellationToken));

    private static void AssertField(ValidationException failure, string field, string messageKey)
    {
        // The envelope's own code is unchanged: a validation failure is still one 422 carrying a
        // list, and it is the entries in that list that became specific.
        Assert.Equal(ErrorCode.COMMON_VALIDATION_FAILED, failure.Code);
        Assert.Contains(
            failure.FieldErrors,
            error => error.Field == field && error.MessageKey == messageKey);
    }
}
