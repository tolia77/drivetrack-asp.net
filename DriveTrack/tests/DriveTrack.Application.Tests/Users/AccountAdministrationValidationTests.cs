using DriveTrack.Application.Common;
using DriveTrack.Application.Users;

namespace DriveTrack.Application.Tests.Users;

/// <summary>
/// The 422 rows of story 7.1's I/O matrix, driven through the real <c>ValidateAndThrowAsync</c>
/// seam (AD-9) so the assertions are about the <see cref="FieldError"/> list the adapter will
/// actually receive.
/// <para>
/// Every one of these runs without a database, and that is the point of validating the merged state
/// rather than the payload: "is this row still legal" is a question about a record, and a question
/// about a record needs no container to answer.
/// </para>
/// </summary>
public class AccountAdministrationValidationTests
{
    private static readonly ClientAccountStateValidator ClientState = new();
    private static readonly DispatcherAccountStateValidator DispatcherState = new();
    private static readonly CreateDispatcherCommandValidator CreateDispatcher = new();

    [Fact]
    public async Task A_merged_client_that_changed_nothing_passes()
    {
        // The commonest edit there is: the caller sent one field, everything else was merged back
        // in from the row, and the result is the row as it already was.
        await ValidatorExtensions.ValidateAndThrowAsync(
            ClientState,
            ValidClient(),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_merged_client_with_no_password_passes()
    {
        // FR-50 / AD-23: null here is the absent case - the caller left the password box empty -
        // and a rule that refused it would make "leave the password alone" impossible to express.
        await ValidatorExtensions.ValidateAndThrowAsync(
            ClientState,
            ValidClient() with { Password = null },
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("044 12")]
    [InlineData("044 123 45 67")]
    [InlineData("0441234567")]
    [InlineData("")]
    public async Task A_merged_client_phone_number_outside_E164_is_refused(string phoneNumber)
    {
        var failure = await Refuse(ClientState, ValidClient() with { PhoneNumber = phoneNumber });

        AssertField(failure, "PhoneNumber", nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID));
    }

    [Fact]
    public async Task Clearing_a_client_phone_number_is_refused_rather_than_written()
    {
        // Present-null on a required field is a caller asking to clear something that cannot be
        // cleared. It is a 422, not a silent no-op, and not an empty string in the column.
        var failure = await Refuse(ClientState, ValidClient() with { PhoneNumber = null });

        AssertField(failure, "PhoneNumber", nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID));
    }

    [Fact]
    public async Task Clearing_a_client_name_is_refused()
    {
        var failure = await Refuse(ClientState, ValidClient() with { FirstName = null });

        AssertField(failure, "FirstName", nameof(ErrorCode.COMMON_VALIDATION_FAILED));
    }

    [Fact]
    public async Task An_over_long_client_name_is_refused()
    {
        var failure = await Refuse(
            ClientState,
            ValidClient() with { LastName = new string('я', 101) });

        AssertField(failure, "LastName", nameof(ErrorCode.COMMON_VALIDATION_FAILED));
    }

    [Fact]
    public async Task An_empty_password_the_caller_actually_sent_is_refused()
    {
        // The other half of the absent/present distinction. Absent is "unchanged"; an empty string
        // is a password somebody typed nothing into, and it is refused with the strength code.
        var failure = await Refuse(ClientState, ValidClient() with { Password = string.Empty });

        AssertField(failure, "Password", nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK));
    }

    [Fact]
    public async Task An_unbounded_password_is_refused_before_the_hasher_sees_it()
    {
        var failure = await Refuse(
            ClientState,
            ValidClient() with { Password = new string('a', 129) });

        AssertField(failure, "Password", nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("")]
    [InlineData("@drivetrack.test")]
    public async Task A_merged_client_email_that_is_not_an_address_is_refused(string email)
    {
        // FR-92's administrator half. The merged state carries the stored address when the body
        // omits one, so the only way to reach this rule is to have actually sent something.
        var failure = await Refuse(ClientState, ValidClient() with { Email = email });

        AssertField(failure, "Email", nameof(ErrorCode.AUTH_EMAIL_INVALID));
    }

    [Fact]
    public async Task Clearing_a_client_email_is_refused_rather_than_written()
    {
        // Present-null on the address a client signs in with: an account nobody can reach is not an
        // edit, so it is a 422 rather than an empty column.
        var failure = await Refuse(ClientState, ValidClient() with { Email = null });

        AssertField(failure, "Email", nameof(ErrorCode.AUTH_EMAIL_INVALID));
    }

    [Fact]
    public async Task A_merged_dispatcher_that_changed_nothing_passes()
    {
        await ValidatorExtensions.ValidateAndThrowAsync(
            DispatcherState,
            ValidDispatcher(),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_merged_dispatcher_with_a_blank_password_passes()
    {
        // The matrix row: "Admin edits a dispatcher, blank password" changes the name and leaves
        // the existing password signing in.
        await ValidatorExtensions.ValidateAndThrowAsync(
            DispatcherState,
            ValidDispatcher() with { Password = null },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Clearing_a_dispatcher_name_is_refused()
    {
        var failure = await Refuse(DispatcherState, ValidDispatcher() with { LastName = null });

        AssertField(failure, "LastName", nameof(ErrorCode.COMMON_VALIDATION_FAILED));
    }

    [Fact]
    public async Task A_complete_dispatcher_creation_passes()
    {
        await ValidatorExtensions.ValidateAndThrowAsync(
            CreateDispatcher,
            ValidCreate(),
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("")]
    [InlineData("@drivetrack.test")]
    public async Task Creating_a_dispatcher_without_a_usable_address_is_refused(string email)
    {
        var failure = await Refuse(CreateDispatcher, ValidCreate() with { Email = email });

        AssertField(failure, "Email", nameof(ErrorCode.AUTH_EMAIL_INVALID));
    }

    [Fact]
    public async Task Creating_a_dispatcher_without_a_password_is_refused()
    {
        // Creation is not a merge: there is no stored password to fall back to, so absence here is
        // a missing field rather than an unchanged one.
        var failure = await Refuse(CreateDispatcher, ValidCreate() with { Password = null });

        AssertField(failure, "Password", nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK));
    }

    [Fact]
    public async Task Creating_a_dispatcher_without_a_name_is_refused()
    {
        var failure = await Refuse(CreateDispatcher, ValidCreate() with { FirstName = " " });

        AssertField(failure, "FirstName", nameof(ErrorCode.COMMON_VALIDATION_FAILED));
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
            await Refuse(ClientState, new ClientAccountState(null, null, null, string.Empty, null)),
            await Refuse(DispatcherState, new DispatcherAccountState(null, null, string.Empty)),
            await Refuse(CreateDispatcher, new CreateDispatcherCommand(null, null, null, null)),
        };

        var offenders = failures
            .SelectMany(failure => failure.FieldErrors)
            .Where(field => !codes.Contains(field.MessageKey))
            .Select(field => field.Field + ": " + field.MessageKey)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static ClientAccountState ValidClient() =>
        new("Олена", "Петренко", "+380441234567", "New-Passw0rd", "olena@drivetrack.test");

    private static DispatcherAccountState ValidDispatcher() =>
        new("Ігор", "Ковальчук", "New-Passw0rd");

    private static CreateDispatcherCommand ValidCreate() =>
        new("Ігор", "Ковальчук", "dispatcher@drivetrack.test", "New-Passw0rd");

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
        // Contains rather than Single, as the registration suite does: an empty value breaks two
        // rules on the same field at once, and NFR-4 wants both reported rather than one.
        Assert.Equal(ErrorCode.COMMON_VALIDATION_FAILED, failure.Code);
        Assert.Contains(
            failure.FieldErrors,
            error => error.Field == field && error.MessageKey == messageKey);
    }
}
