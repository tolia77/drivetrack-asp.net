using DriveTrack.Application.Common;
using DriveTrack.Application.Users;

namespace DriveTrack.Application.Tests.Users;

/// <summary>
/// FR-3's rules, driven through the real <c>ValidateAndThrowAsync</c> seam (AD-9) so the assertions
/// are about the <see cref="FieldError"/> list the adapter will actually receive.
/// <para>
/// Both halves of each row matter. The field name is what lets a form attach the message to the
/// input that produced it (NFR-4), and the message key has to be an <see cref="ErrorCode"/> name,
/// because the adapter resolves it through the same Ukrainian catalogue as <c>error.code</c> — a
/// sentence there would reach a user untranslated.
/// </para>
/// </summary>
public class RegistrationValidationTests
{
    private static readonly RegisterClientCommandValidator Registration = new();
    private static readonly SignInCommandValidator SignIn = new();

    [Fact]
    public async Task A_complete_registration_passes()
    {
        await ValidatorExtensions.ValidateAndThrowAsync(Registration, Valid(), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("044 123 45 67")]
    [InlineData("0441234567")]
    [InlineData("+0441234567")]
    [InlineData("+38044123456789012")]
    [InlineData("")]
    public async Task A_phone_number_outside_E164_is_refused(string phoneNumber)
    {
        var failure = await Refuse(Valid() with { PhoneNumber = phoneNumber });

        AssertField(failure, "PhoneNumber", nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID));
    }

    [Theory]
    [InlineData("+380441234567")]
    [InlineData("+15551234567")]
    public async Task An_E164_phone_number_is_accepted(string phoneNumber)
    {
        await ValidatorExtensions.ValidateAndThrowAsync(
            Registration,
            Valid() with { PhoneNumber = phoneNumber },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_mismatched_confirmation_is_refused()
    {
        var failure = await Refuse(Valid() with { PasswordConfirmation = "Something-Else-9" });

        AssertField(failure, "PasswordConfirmation", nameof(ErrorCode.AUTH_PASSWORD_CONFIRMATION_MISMATCH));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("")]
    public async Task A_malformed_email_is_refused(string email)
    {
        var failure = await Refuse(Valid() with { Email = email });

        AssertField(failure, "Email", nameof(ErrorCode.AUTH_EMAIL_INVALID));
    }

    [Fact]
    public async Task A_missing_password_is_refused_with_the_weak_password_key()
    {
        // The same code Identity's own policy failure maps to, so a caller sees one answer to
        // "the password is not acceptable" whichever half decided it (NFR-2).
        var failure = await Refuse(Valid() with { Password = "", PasswordConfirmation = "" });

        AssertField(failure, "Password", nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_missing_name_is_refused(string? firstName)
    {
        var failure = await Refuse(Valid() with { FirstName = firstName });

        AssertField(failure, "FirstName", nameof(ErrorCode.COMMON_FIELD_REQUIRED));
    }

    [Fact]
    public async Task A_name_beyond_the_column_length_is_refused()
    {
        var failure = await Refuse(Valid() with { LastName = new string('я', 101) });

        AssertField(failure, "LastName", nameof(ErrorCode.AUTH_LAST_NAME_TOO_LONG));
    }

    [Fact]
    public async Task An_email_beyond_the_column_length_is_refused()
    {
        var local = new string('a', RegisterClientCommandValidator.EmailMaximumLength);
        var failure = await Refuse(Valid() with { Email = local + "@drivetrack.test" });

        AssertField(failure, "Email", nameof(ErrorCode.AUTH_EMAIL_INVALID));
    }

    [Fact]
    public async Task A_phone_number_beyond_the_column_length_is_refused()
    {
        var failure = await Refuse(Valid() with
        {
            PhoneNumber = "+" + new string('1', RegisterClientCommandValidator.PhoneNumberMaximumLength),
        });

        AssertField(failure, "PhoneNumber", nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID));
    }

    [Fact]
    public async Task An_unbounded_password_is_refused_before_it_reaches_the_hasher()
    {
        // Registration is anonymous and the hasher is deliberately slow, so an unbounded password is
        // work a stranger can ask the server to do. The ceiling is a rule about the request, which
        // is why it is here rather than in the store.
        var long_ = new string('p', RegisterClientCommandValidator.PasswordMaximumLength + 1);
        var failure = await Refuse(Valid() with { Password = long_, PasswordConfirmation = long_ });

        // The ceiling, not the strength policy: what is wrong with this password is its length.
        AssertField(failure, "Password", nameof(ErrorCode.AUTH_PASSWORD_TOO_LONG));
    }

    [Fact]
    public async Task Sign_in_bounds_the_email_and_the_password_too()
    {
        // The same reasoning, on the other anonymous endpoint: it verifies a hash, so it does the
        // same slow work for a caller who has presented nothing.
        var failure = await Assert.ThrowsAsync<ValidationException>(() =>
            ValidatorExtensions.ValidateAndThrowAsync(
                SignIn,
                new SignInCommand(
                    new string('a', RegisterClientCommandValidator.EmailMaximumLength + 1),
                    new string('p', RegisterClientCommandValidator.PasswordMaximumLength + 1)),
                TestContext.Current.CancellationToken));

        AssertField(failure, "Email", nameof(ErrorCode.AUTH_EMAIL_INVALID));
        AssertField(failure, "Password", nameof(ErrorCode.AUTH_PASSWORD_TOO_LONG));
    }

    [Fact]
    public async Task Every_offending_field_is_reported_not_only_the_first()
    {
        // NFR-4: a form that reports one error at a time is a form filled in five times.
        var failure = await Refuse(new RegisterClientCommand(null, null, "nope", "0441234567", "", "x"));

        Assert.Equal(ErrorCode.COMMON_VALIDATION_FAILED, failure.Code);
        Assert.True(failure.FieldErrors.Count >= 4, "Expected every offending field to be reported.");
    }

    [Theory]
    [InlineData(null, "Passw0rd!")]
    [InlineData("", "Passw0rd!")]
    public async Task Sign_in_refuses_a_missing_email(string? email, string password)
    {
        var failure = await Assert.ThrowsAsync<ValidationException>(() =>
            ValidatorExtensions.ValidateAndThrowAsync(
                SignIn,
                new SignInCommand(email, password),
                TestContext.Current.CancellationToken));

        AssertField(failure, "Email", nameof(ErrorCode.AUTH_EMAIL_INVALID));
    }

    [Fact]
    public async Task Sign_in_accepts_any_well_formed_pair()
    {
        // Deliberately thin: anything stricter would tell an unauthenticated caller something about
        // the account they named, and unknown email and wrong password must be indistinguishable.
        await ValidatorExtensions.ValidateAndThrowAsync(
            SignIn,
            new SignInCommand("unknown@drivetrack.test", "whatever"),
            TestContext.Current.CancellationToken);
    }

    private static RegisterClientCommand Valid() => new(
        "Олена",
        "Петренко",
        "olena@drivetrack.test",
        "+380441234567",
        "Passw0rd!",
        "Passw0rd!");

    private static Task<ValidationException> Refuse(RegisterClientCommand command) =>
        Assert.ThrowsAsync<ValidationException>(() =>
            ValidatorExtensions.ValidateAndThrowAsync(
                Registration,
                command,
                TestContext.Current.CancellationToken));

    private static void AssertField(ValidationException failure, string field, string messageKey)
    {
        Assert.Equal(ErrorCode.COMMON_VALIDATION_FAILED, failure.Code);
        Assert.Contains(
            failure.FieldErrors,
            error => error.Field == field && error.MessageKey == messageKey);
    }
}
