using DriveTrack.Application.Common;
using DriveTrack.Application.Users;

namespace DriveTrack.Application.Tests.Users;

/// <summary>
/// The 422 rows of story 7.3's I/O matrix (FR-87, FR-88, FR-92), driven through the real
/// <c>ValidateAndThrowAsync</c> seam (AD-9) so the assertions are about the <see cref="FieldError"/>
/// list the adapter will actually receive.
/// <para>
/// None of these needs a database, and that is the point of validating the merged profile rather
/// than the payload: "is this row still legal" is a question about a record, and a question about a
/// record needs no container to answer. The rows that do need one — a password that is wrong rather
/// than malformed, an address another account holds — are asserted over HTTP instead.
/// </para>
/// </summary>
public class SelfServiceValidationTests
{
    private static readonly ProfileStateValidator Profile = new();
    private static readonly ChangePasswordCommandValidator Password = new();
    private static readonly ChangeEmailCommandValidator Email = new();

    [Fact]
    public async Task A_merged_client_profile_that_changed_nothing_passes()
    {
        await ValidatorExtensions.ValidateAndThrowAsync(
            Profile,
            ValidClient(),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_merged_profile_with_no_client_row_and_no_phone_number_passes()
    {
        // The commonest edit a dispatcher, a driver or an administrator makes: their own name, and
        // no phone number anywhere near it.
        await ValidatorExtensions.ValidateAndThrowAsync(
            Profile,
            ValidOther(),
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("044 12")]
    [InlineData("044 123 45 67")]
    [InlineData("0441234567")]
    [InlineData("")]
    public async Task A_client_phone_number_outside_E164_is_refused(string phoneNumber)
    {
        var failure = await Refuse(Profile, ValidClient() with { PhoneNumber = phoneNumber });

        AssertField(failure, "PhoneNumber", nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID));
    }

    [Fact]
    public async Task Clearing_a_client_phone_number_is_refused_rather_than_written()
    {
        // Present-null on a required field is a caller asking to clear something that cannot be
        // cleared. It is a 422, not a silent no-op, and not an empty string in the column.
        var failure = await Refuse(Profile, ValidClient() with { PhoneNumber = null });

        AssertField(failure, "PhoneNumber", nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID));
    }

    [Fact]
    public async Task A_phone_number_on_an_account_with_no_client_row_is_refused()
    {
        // DR-3 gives a dispatcher, a driver and an administrator no row to store a number in. The
        // alternative - dropping a field the caller did send - is exactly the failure Optional<T>
        // exists to prevent, and it would be invisible to everyone involved.
        var failure = await Refuse(Profile, ValidOther() with { PhoneNumber = "+380441234567" });

        AssertField(failure, "PhoneNumber", nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID));
    }

    [Fact]
    public async Task Clearing_a_name_is_refused_whoever_sends_it()
    {
        var failure = await Refuse(Profile, ValidClient() with { FirstName = null });

        AssertField(failure, "FirstName", nameof(ErrorCode.COMMON_VALIDATION_FAILED));
    }

    [Fact]
    public async Task An_over_long_name_is_refused()
    {
        var failure = await Refuse(Profile, ValidOther() with { LastName = new string('я', 101) });

        AssertField(failure, "LastName", nameof(ErrorCode.COMMON_VALIDATION_FAILED));
    }

    [Fact]
    public async Task A_complete_password_change_passes()
    {
        await ValidatorExtensions.ValidateAndThrowAsync(
            Password,
            ValidPassword(),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_password_change_without_the_current_password_is_refused()
    {
        // COMMON_VALIDATION_FAILED, not the strength code: a missing field is not a weak password,
        // and a user told "your password is too weak" would go looking at the wrong box.
        var failure = await Refuse(Password, ValidPassword() with { CurrentPassword = null });

        AssertField(failure, "CurrentPassword", nameof(ErrorCode.COMMON_VALIDATION_FAILED));
    }

    [Fact]
    public async Task A_password_change_with_no_new_password_is_refused()
    {
        var failure = await Refuse(Password, ValidPassword() with { NewPassword = string.Empty });

        AssertField(failure, "NewPassword", nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK));
    }

    [Fact]
    public async Task An_unbounded_new_password_is_refused_before_the_hasher_sees_it()
    {
        var failure = await Refuse(
            Password,
            ValidPassword() with
            {
                NewPassword = new string('a', 129),
                NewPasswordConfirmation = new string('a', 129),
            });

        AssertField(failure, "NewPassword", nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK));
    }

    [Fact]
    public async Task A_confirmation_that_does_not_match_the_new_password_is_refused()
    {
        var failure = await Refuse(
            Password,
            ValidPassword() with { NewPasswordConfirmation = "Other-Passw0rd" });

        AssertField(
            failure,
            "NewPasswordConfirmation",
            nameof(ErrorCode.AUTH_PASSWORD_CONFIRMATION_MISMATCH));
    }

    [Fact]
    public async Task A_password_is_never_trimmed_on_its_way_through_the_rules()
    {
        // Leading and trailing space is part of a password somebody chose, so a pair that differs
        // only in padding is a mismatch rather than a match the validator quietly repaired.
        var failure = await Refuse(
            Password,
            ValidPassword() with { NewPasswordConfirmation = " New-Passw0rd " });

        AssertField(
            failure,
            "NewPasswordConfirmation",
            nameof(ErrorCode.AUTH_PASSWORD_CONFIRMATION_MISMATCH));
    }

    [Fact]
    public async Task A_well_formed_address_passes()
    {
        await ValidatorExtensions.ValidateAndThrowAsync(
            Email,
            new ChangeEmailCommand("olena@drivetrack.test"),
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("")]
    [InlineData("@drivetrack.test")]
    [InlineData(null)]
    public async Task An_address_that_is_not_one_is_refused(string? email)
    {
        var failure = await Refuse(Email, new ChangeEmailCommand(email));

        AssertField(failure, "Email", nameof(ErrorCode.AUTH_EMAIL_INVALID));
    }

    [Fact]
    public async Task An_over_long_address_is_refused()
    {
        var failure = await Refuse(
            Email,
            new ChangeEmailCommand(new string('a', 250) + "@drivetrack.test"));

        AssertField(failure, "Email", nameof(ErrorCode.AUTH_EMAIL_INVALID));
    }

    [Fact]
    public async Task Every_message_is_an_error_code_name_rather_than_a_sentence()
    {
        // NFR-14 by construction: the adapter resolves a field message through the same Ukrainian
        // catalogue as error.code, so a sentence here would reach a user untranslated. Asserted
        // across the whole validator rather than row by row, because the failure mode is one rule
        // somebody wrote in prose.
        var codes = Enum.GetNames<ErrorCode>().ToHashSet(StringComparer.Ordinal);

        var failures = new List<ValidationException>
        {
            await Refuse(Profile, new ProfileState(null, null, null, IsClient: true)),
            await Refuse(Profile, new ProfileState(null, null, "+380441234567", IsClient: false)),
            await Refuse(Password, new ChangePasswordCommand(null, null, "mismatch")),
            await Refuse(Email, new ChangeEmailCommand(null)),
        };

        var offenders = failures
            .SelectMany(failure => failure.FieldErrors)
            .Where(field => !codes.Contains(field.MessageKey))
            .Select(field => field.Field + ": " + field.MessageKey)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static ProfileState ValidClient() =>
        new("Олена", "Петренко", "+380441234567", IsClient: true);

    private static ProfileState ValidOther() =>
        new("Ігор", "Ковальчук", null, IsClient: false);

    private static ChangePasswordCommand ValidPassword() =>
        new("Passw0rd-Test", "New-Passw0rd", "New-Passw0rd");

    private static async Task<ValidationException> Refuse<T>(
        FluentValidation.IValidator<T> validator,
        T instance) =>
        await Assert.ThrowsAsync<ValidationException>(
            () => ValidatorExtensions.ValidateAndThrowAsync(
                validator,
                instance,
                TestContext.Current.CancellationToken));

    private static void AssertField(ValidationException failure, string field, string messageKey)
    {
        // Contains rather than Single, as the administration suite does: an empty value breaks two
        // rules on the same field at once, and NFR-4 wants both reported rather than one.
        Assert.Equal(ErrorCode.COMMON_VALIDATION_FAILED, failure.Code);
        Assert.Contains(
            failure.FieldErrors,
            error => error.Field == field && error.MessageKey == messageKey);
    }
}
