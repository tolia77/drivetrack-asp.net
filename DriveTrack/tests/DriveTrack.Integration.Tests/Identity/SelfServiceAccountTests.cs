using System.Globalization;
using System.Net;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Identity;

/// <summary>
/// Story 7.3's I/O matrix over HTTP (FR-87, FR-88, FR-92), against the real <c>Program.cs</c>
/// pipeline with the real JWT scheme.
/// <para>
/// These claims are only true over the wire and against the schema. "The new password signs in and
/// the old one does not" is a property of Identity's hasher reached through the whole adapter, and
/// "a refused edit wrote nothing" is a property of one transaction that no unit test can observe.
/// </para>
/// </summary>
public class SelfServiceAccountTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_client_edits_their_own_name_and_phone_number_in_one_request()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Олена", lastName: "Петренко");
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client,
            HttpMethod.Patch,
            Route(subject.UserId),
            token,
            new { firstName = "Оксана", lastName = "Яремчук", phoneNumber = "+380671112233" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = (await AdministrationApi.ReadAsync(response, cancellationToken)).GetProperty("data");

        Assert.Equal("Оксана", profile.GetProperty("firstName").GetString());
        Assert.Equal("Яремчук", profile.GetProperty("lastName").GetString());
        Assert.Equal("+380671112233", profile.GetProperty("phoneNumber").GetString());

        // Read back rather than trusted from the response: the phone number is written by mutating
        // the tracked client row instead of through a repository member, so the response would carry
        // the new value whether or not the commit ever wrote it.
        var stored = await GetAsync(client, token, subject.UserId, cancellationToken);

        Assert.Equal("Оксана", stored.GetProperty("firstName").GetString());
        Assert.Equal("+380671112233", stored.GetProperty("phoneNumber").GetString());
    }

    [Fact]
    public async Task A_body_naming_one_field_changes_one_field()
    {
        // AD-23's whole point, over the wire: the last name and the phone number are not
        // "unspecified" here, they are untouched.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Олена", lastName: "Петренко",
            phoneNumber: "+380441234567");
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), token,
            new { firstName = "Оксана" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var stored = await GetAsync(client, token, subject.UserId, cancellationToken);

        Assert.Equal("Оксана", stored.GetProperty("firstName").GetString());
        Assert.Equal("Петренко", stored.GetProperty("lastName").GetString());
        Assert.Equal("+380441234567", stored.GetProperty("phoneNumber").GetString());
        Assert.Equal(subject.Email, stored.GetProperty("email").GetString());
    }

    [Fact]
    public async Task A_caller_with_no_client_row_may_not_send_a_phone_number()
    {
        // DR-3 gives a dispatcher no row to store one in. Silently dropping the field would leave
        // the caller believing something was saved, which is the failure Optional<T> exists to
        // prevent - so it is refused, and the name in the same body goes with it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var dispatcher = await AdministrationApi.CreateDispatcherAsync(
            client, admin, cancellationToken, firstName: "Ігор");

        var token = await AdministrationApi.SignInAsync(
            client, dispatcher.Email, AdministrationApi.Password, cancellationToken);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(dispatcher.UserId), token,
            new { firstName = "Богдан", phoneNumber = "+380441234567" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var error = (await AdministrationApi.ReadAsync(response, cancellationToken))
                .GetProperty("error");

            Assert.Equal(
                nameof(ErrorCode.COMMON_VALIDATION_FAILED),
                error.GetProperty("code").GetString());

            // NFR-4: the key is the name the caller sent, so a form can attach it to the input.
            var message = error.GetProperty("fields").GetProperty("phoneNumber")[0].GetString();

            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain(
                nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID), message, StringComparison.Ordinal);
        }

        var stored = await GetAsync(client, token, dispatcher.UserId, cancellationToken);

        Assert.Equal("Ігор", stored.GetProperty("firstName").GetString());

        // And there is no phone number on the profile of an account that has no row for one.
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("phoneNumber").ValueKind);
    }

    [Fact]
    public async Task A_dispatcher_edits_their_own_name_without_mentioning_a_phone_number()
    {
        // The other side of the rule above, and the one that would break if the screen sent the
        // field as a present null rather than leaving it absent.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var dispatcher = await AdministrationApi.CreateDispatcherAsync(client, admin, cancellationToken);

        var token = await AdministrationApi.SignInAsync(
            client, dispatcher.Email, AdministrationApi.Password, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(dispatcher.UserId), token,
            new { firstName = "Богдан" }, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "Богдан",
            (await AdministrationApi.ReadAsync(response, cancellationToken))
                .GetProperty("data").GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task A_client_clearing_their_own_phone_number_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, phoneNumber: "+380441234567");
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using (var response = await SendRawAsync(
            client, HttpMethod.Patch, Route(subject.UserId), token,
            """{"phoneNumber":null}""", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var fields = (await AdministrationApi.ReadAsync(response, cancellationToken))
                .GetProperty("error")
                .GetProperty("fields");

            Assert.True(fields.TryGetProperty("phoneNumber", out _));
        }

        Assert.Equal(
            "+380441234567",
            (await GetAsync(client, token, subject.UserId, cancellationToken))
                .GetProperty("phoneNumber").GetString());
    }

    [Fact]
    public async Task One_user_may_not_edit_another_and_is_refused_before_anything_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var caller = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var other = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Марія");

        var token = await AdministrationApi.SignInAsync(
            client, caller.Email, AdministrationApi.Password, cancellationToken);

        foreach (var attempt in new[]
        {
            (Method: HttpMethod.Patch, Path: Route(other.UserId), Body: (object)new { firstName = "Оксана" }),
            (Method: HttpMethod.Post, Path: Route(other.UserId) + "/password",
                Body: (object)new
                {
                    currentPassword = AdministrationApi.Password,
                    newPassword = "New-Passw0rd",
                    newPasswordConfirmation = "New-Passw0rd",
                }),
            (Method: HttpMethod.Post, Path: Route(other.UserId) + "/email",
                Body: (object)new { email = AdministrationApi.UniqueEmail() }),
        })
        {
            using var response = await AdministrationApi.SendAsync(
                client, attempt.Method, attempt.Path, token, attempt.Body, cancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AdministrationApi.AssertFailureAsync(
                response, ErrorCode.AUTH_FORBIDDEN, cancellationToken);

            // Nothing disclosed by the refusal, not even the address it was addressed to.
            var body = await AdministrationApi.BodyAsync(response, cancellationToken);

            Assert.DoesNotContain(other.Email, body, StringComparison.Ordinal);
        }

        // Nothing written either: the other account is exactly as it was.
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, other.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task An_anonymous_caller_is_unauthenticated_rather_than_forbidden()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), token: null,
            new { firstName = "Оксана" }, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AdministrationApi.AssertFailureAsync(
            response, ErrorCode.AUTH_UNAUTHENTICATED, cancellationToken);
    }

    [Fact]
    public async Task A_user_replaces_their_own_password_and_the_old_one_stops_working()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(subject.UserId) + "/password", token,
            new
            {
                currentPassword = AdministrationApi.Password,
                newPassword = "New-Passw0rd",
                newPasswordConfirmation = "New-Passw0rd",
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // 204 forbids a body, and the envelope stops where HTTP does (NFR-1).
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        }

        Assert.False(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, "New-Passw0rd", cancellationToken));

        // FR-8: what landed is a hash, and the plaintext is nowhere in the row.
        Assert.Equal(
            0L,
            await AdministrationApi.CountAsync(
                factory,
                "asp_net_users",
                $"id = {subject.UserId} AND password_hash LIKE '%New-Passw0rd%'",
                cancellationToken));

        // DW-11 owns revocation, and this story does not pretend to close it: the cookie and the
        // bearer token key on the user id, so the session that made the change still works.
        var stored = await GetAsync(client, token, subject.UserId, cancellationToken);

        Assert.Equal(subject.Email, stored.GetProperty("email").GetString());
    }

    [Fact]
    public async Task A_wrong_current_password_is_refused_and_leaves_the_stored_hash_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(subject.UserId) + "/password", token,
            new
            {
                currentPassword = "Wrong-Passw0rd",
                newPassword = "New-Passw0rd",
                newPasswordConfirmation = "New-Passw0rd",
            },
            cancellationToken))
        {
            // 422 rather than 401: the caller is signed in, and it is one field of their request
            // that was wrong. A 401 would reach FR-13's boundary as an expired session.
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AdministrationApi.AssertFailureAsync(
                response, ErrorCode.AUTH_CURRENT_PASSWORD_INCORRECT, cancellationToken);
        }

        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
        Assert.False(await AdministrationApi.CanSignInAsync(
            client, subject.Email, "New-Passw0rd", cancellationToken));
    }

    [Fact]
    public async Task A_confirmation_that_does_not_match_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(subject.UserId) + "/password", token,
            new
            {
                currentPassword = AdministrationApi.Password,
                newPassword = "New-Passw0rd",
                newPasswordConfirmation = "Other-Passw0rd",
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var fields = (await AdministrationApi.ReadAsync(response, cancellationToken))
            .GetProperty("error")
            .GetProperty("fields");

        Assert.True(fields.TryGetProperty("newPasswordConfirmation", out _));

        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task A_new_password_below_the_policy_is_refused_and_the_account_still_signs_in()
    {
        // The row that would be catastrophic to get wrong: SetPasswordAsync clears the stored hash
        // before Identity computes the new one, so a refusal that reached a commit would leave an
        // account nobody could sign in to.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(subject.UserId) + "/password", token,
            new
            {
                currentPassword = AdministrationApi.Password,
                newPassword = "abc",
                newPasswordConfirmation = "abc",
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var error = (await AdministrationApi.ReadAsync(response, cancellationToken))
                .GetProperty("error");

            Assert.Equal(
                nameof(ErrorCode.COMMON_VALIDATION_FAILED),
                error.GetProperty("code").GetString());
            Assert.True(error.GetProperty("fields").TryGetProperty("password", out _));
        }

        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task A_user_changes_their_own_address_and_signs_in_with_the_new_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        var replacement = AdministrationApi.UniqueEmail();

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(subject.UserId) + "/email", token,
            new { email = replacement }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                replacement,
                (await AdministrationApi.ReadAsync(response, cancellationToken))
                    .GetProperty("data").GetProperty("email").GetString());
        }

        Assert.True(await AdministrationApi.CanSignInAsync(
            client, replacement, AdministrationApi.Password, cancellationToken));
        Assert.False(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));

        // The user-name pair goes with the address, and the *normalized* column is the one that
        // matters: `user_name_index` on normalized_user_name is the only unique index on this
        // table's identity columns - `email_index` is declared without `unique` - so it is the whole
        // race backstop behind EmailChange's friendly check. Deleting the normalizing assignment in
        // SetEmailAsync leaves sign-in working and quietly removes that backstop, which is why it is
        // asserted here rather than left to the human-readable column alone.
        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory,
            "asp_net_users",
            $"id = {subject.UserId} AND user_name = '{replacement}'",
            cancellationToken));
        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory,
            "asp_net_users",
            $"id = {subject.UserId} AND normalized_user_name = '{replacement.ToUpperInvariant()}'",
            cancellationToken));

        // DW-11 again: the session that made the change keeps working, with its email claim stale.
        Assert.Equal(
            replacement,
            (await GetAsync(client, token, subject.UserId, cancellationToken))
                .GetProperty("email").GetString());
    }

    [Fact]
    public async Task The_address_a_change_vacated_can_be_claimed_by_a_new_account()
    {
        // The claim the previous test cannot make on its own. `user_name_index` on
        // normalized_user_name is unique, so an account that changed its address while leaving its
        // user name behind would hold the old address hostage for ever - registration with it would
        // hit the index and answer 409, and nothing else in the suite would notice.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(subject.UserId) + "/email", token,
            new { email = AdministrationApi.UniqueEmail() }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Registered by hand rather than through the helper, because the address is the point: it
        // has to be the exact one the change let go of.
        using var registration = await AdministrationApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/auth/register",
            token: null,
            new
            {
                firstName = "Марія",
                lastName = "Коваль",
                email = subject.Email,
                phoneNumber = "+380509876543",
                password = AdministrationApi.Password,
                passwordConfirmation = AdministrationApi.Password,
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);

        var opened = (await AdministrationApi.ReadAsync(registration, cancellationToken))
            .GetProperty("data");

        Assert.NotEqual(subject.UserId, opened.GetProperty("userId").GetInt32());
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task An_admin_edits_another_user_through_the_same_self_service_routes()
    {
        // AD-4 puts "an admin satisfies every check" inside AccessGuard, once, so RequireSelf already
        // reads as "the user themselves, or an admin". That is the stated reason this story adds no
        // second guard member - and it is untested unless an admin token actually drives these
        // routes. It is also the only route that reaches a dispatcher: /api/clients refuses a
        // non-client id as absent.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var subject = await AdministrationApi.CreateDispatcherAsync(
            client, admin, cancellationToken, firstName: "Ігор");

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), admin,
            new { firstName = "Богдан" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                "Богдан",
                (await AdministrationApi.ReadAsync(response, cancellationToken))
                    .GetProperty("data").GetProperty("firstName").GetString());
        }

        var replacement = AdministrationApi.UniqueEmail();

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(subject.UserId) + "/email", admin,
            new { email = replacement }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                replacement,
                (await AdministrationApi.ReadAsync(response, cancellationToken))
                    .GetProperty("data").GetProperty("email").GetString());
        }

        Assert.True(await AdministrationApi.CanSignInAsync(
            client, replacement, AdministrationApi.Password, cancellationToken));
        Assert.False(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task An_address_another_account_holds_is_refused_in_any_casing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var other = await AdministrationApi.RegisterClientAsync(client, cancellationToken);

        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(subject.UserId) + "/email", token,
            new { email = other.Email.ToUpperInvariant() }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await AdministrationApi.AssertFailureAsync(
                response, ErrorCode.AUTH_EMAIL_ALREADY_IN_USE, cancellationToken);
        }

        // Neither account moved.
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, other.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task Sending_the_address_the_account_already_holds_is_not_a_conflict()
    {
        // EmailExistsAsync answers true for the caller's own address, so saving a form without
        // touching the box would 409 if the unchanged case were not checked first.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(subject.UserId) + "/email", token,
            new { email = subject.Email.ToUpperInvariant() }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // The stored spelling, not the caller's: re-casing an address is not a change this
            // system makes, so nothing was written.
            Assert.Equal(
                subject.Email,
                (await AdministrationApi.ReadAsync(response, cancellationToken))
                    .GetProperty("data").GetProperty("email").GetString());
        }

        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory,
            "asp_net_users",
            $"id = {subject.UserId} AND email = '{subject.Email}'",
            cancellationToken));
    }

    [Fact]
    public async Task An_address_that_is_not_one_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(subject.UserId) + "/email", token,
            new { email = "not-an-address" }, cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var fields = (await AdministrationApi.ReadAsync(response, cancellationToken))
            .GetProperty("error")
            .GetProperty("fields");

        Assert.True(fields.TryGetProperty("email", out _));

        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task An_admin_changes_a_client_address_through_the_edit_they_already_perform()
    {
        // FR-92's administrator half. RequireSelf reads as "the user themselves, or an admin"
        // (AD-4), so the same guard answers both halves and there is no second member for it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        var replacement = AdministrationApi.UniqueEmail();

        using (var response = await AdministrationApi.SendAsync(
            client,
            HttpMethod.Patch,
            "/api/clients/" + subject.UserId.ToString(CultureInfo.InvariantCulture),
            admin,
            new { firstName = "Оксана", email = replacement, password = "New-Passw0rd" },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var updated = (await AdministrationApi.ReadAsync(response, cancellationToken))
                .GetProperty("data");

            Assert.Equal(replacement, updated.GetProperty("email").GetString());
            Assert.Equal("Оксана", updated.GetProperty("firstName").GetString());
        }

        // One transaction: the name, the address and the password all landed (AD-5).
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, replacement, "New-Passw0rd", cancellationToken));
        Assert.False(await AdministrationApi.CanSignInAsync(
            client, subject.Email, "New-Passw0rd", cancellationToken));
    }

    [Fact]
    public async Task An_admin_edit_naming_a_taken_address_writes_nothing_at_all()
    {
        // The half that is easy to miss: the name in the same body must not survive the refusal,
        // because the whole edit is one unit of work (AD-5).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Олена");
        var other = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        var route = "/api/clients/" + subject.UserId.ToString(CultureInfo.InvariantCulture);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, route, admin,
            new { firstName = "Оксана", email = other.Email }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await AdministrationApi.AssertFailureAsync(
                response, ErrorCode.AUTH_EMAIL_ALREADY_IN_USE, cancellationToken);
        }

        using var read = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, route, admin, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var stored = (await AdministrationApi.ReadAsync(read, cancellationToken)).GetProperty("data");

        Assert.Equal("Олена", stored.GetProperty("firstName").GetString());
        Assert.Equal(subject.Email, stored.GetProperty("email").GetString());
    }

    [Fact]
    public async Task An_admin_edit_refused_after_the_password_was_staged_leaves_the_old_one_working()
    {
        // The one window the code itself calls dangerous. SetPasswordAsync nulls the tracked hash
        // before AddPasswordAsync computes the new one, and in ClientAdministrationService the email
        // conflict is raised *after* that call - so between the two the account has no password at
        // all. Committing there would store the null and leave an account nobody can sign in to.
        //
        // The test that sends only a name and an address never enters that window, which is why this
        // one sends both a password and a taken address.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Олена");
        var other = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        var route = "/api/clients/" + subject.UserId.ToString(CultureInfo.InvariantCulture);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, route, admin,
            new { firstName = "Оксана", password = "New-Passw0rd", email = other.Email },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await AdministrationApi.AssertFailureAsync(
                response, ErrorCode.AUTH_EMAIL_ALREADY_IN_USE, cancellationToken);
        }

        // The account still has a password, and it is the old one: the transaction rolled back over
        // the cleared hash as well as over the name.
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
        Assert.False(await AdministrationApi.CanSignInAsync(
            client, subject.Email, "New-Passw0rd", cancellationToken));

        // Read outside EF, because "the column is not null" is a claim about the row rather than
        // about what Identity chose to answer.
        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory,
            "asp_net_users",
            $"id = {subject.UserId} AND password_hash IS NOT NULL",
            cancellationToken));

        using var read = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, route, admin, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var stored = (await AdministrationApi.ReadAsync(read, cancellationToken)).GetProperty("data");

        Assert.Equal("Олена", stored.GetProperty("firstName").GetString());
        Assert.Equal(subject.Email, stored.GetProperty("email").GetString());
    }

    [Fact]
    public async Task An_id_no_account_holds_is_a_404_on_every_one_of_the_three_routes()
    {
        // The branch only an administrator can reach: RequireSelf refuses everybody else before the
        // id is ever looked up, which is why a stranger gets 403 and not 404 (no enumeration). For an
        // admin the guard passes, so the null check three lines down is the whole answer - and until
        // now nothing drove it. Ordered as it is, a missing row is a 404; ordered the other way it
        // would be a NullReferenceException inside a 500.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        const int Absent = 987654;

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(Absent), admin,
            new { firstName = "Олена" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AdministrationApi.AssertFailureAsync(
                response, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(Absent) + "/password", admin,
            new
            {
                currentPassword = AdministrationApi.Password,
                newPassword = AdministrationApi.Password,
                newPasswordConfirmation = AdministrationApi.Password,
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AdministrationApi.AssertFailureAsync(
                response, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(Absent) + "/email", admin,
            new { email = AdministrationApi.UniqueEmail() }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AdministrationApi.AssertFailureAsync(
                response, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }
    }

    [Fact]
    public async Task A_padded_address_is_stored_trimmed_rather_than_refused()
    {
        // ChangeEmailAsync trims before it validates, and the comment there calls the ordering
        // load-bearing: validate the raw string and an address of exactly the maximum length with a
        // trailing space is refused on this path while the administrator's path, which trims as it
        // merges, accepts it. Nothing asserted either half of that. This drives the trim end to end -
        // the padding never reaches the column, and the address that comes back is the one that
        // signs in.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        var replacement = AdministrationApi.UniqueEmail();

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Post, Route(subject.UserId) + "/email", token,
            new { email = "  " + replacement + "  " }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                replacement,
                (await AdministrationApi.ReadAsync(response, cancellationToken))
                    .GetProperty("data").GetProperty("email").GetString());
        }

        // The stored value, not only the echoed one: a padded address written to the column would
        // sign in under neither spelling.
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, replacement, AdministrationApi.Password, cancellationToken));
    }

    private static string Route(int userId) =>
        "/api/users/" + userId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A body written out by hand, because an anonymous object cannot express "this property is
    /// present and its value is null" once the serializer has been told to skip nulls — and that is
    /// precisely the request under test.
    /// </summary>
    private static async Task<HttpResponseMessage> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        string json,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<JsonElement> GetAsync(
        HttpClient client,
        string token,
        int userId,
        CancellationToken cancellationToken)
    {
        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, Route(userId), token, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await AdministrationApi.ReadAsync(response, cancellationToken)).GetProperty("data");
    }
}
