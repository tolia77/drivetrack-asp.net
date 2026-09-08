using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Application.Users;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Domain.Identity;
using DriveTrack.Web.Account;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.Extensions.Localization;

namespace DriveTrack.Integration.Tests.Identity;

/// <summary>
/// The register, sign-in and profile rows of the I/O matrix, over HTTP, against the real
/// <c>Program.cs</c> pipeline with the real cookie and JWT schemes.
/// <para>
/// The scheme selection is the part that cannot be asserted anywhere else: "cookie off
/// <c>/api</c>, bearer on <c>/api/*</c>" is a property of the composition root, and a test that
/// registered its own handlers would be asserting against a replica of the decision rather than
/// the decision.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AuthenticationTests(PostgresFixture postgres)
{
    private const string Password = "Passw0rd-Test";

    [Fact]
    public async Task Registration_returns_a_session_and_commits_the_user_role_and_client_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        var email = UniqueEmail();
        using var response = await Register(client, email, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await ReadAsync(response, cancellationToken);

        Assert.True(envelope.GetProperty("success").GetBoolean());

        var session = envelope.GetProperty("data");

        Assert.Equal(email, session.GetProperty("email").GetString());

        // AD-21: the role crosses the wire as its member name, never as an ordinal.
        Assert.Equal("Client", session.GetProperty("role").GetString());
        Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("accessToken").GetString()));

        // AD-22: the typed id is a compile-time device, not a wire shape.
        var userId = session.GetProperty("userId").GetInt32();

        Assert.True(userId > 0);

        // One user, one role row, one client row - and the password nowhere in plaintext.
        Assert.Equal(1L, await CountAsync(factory, "asp_net_users", $"normalized_email = '{email.ToUpperInvariant()}'", cancellationToken));
        Assert.Equal(1L, await CountAsync(factory, "asp_net_user_roles", $"user_id = {userId}", cancellationToken));
        Assert.Equal(1L, await CountAsync(factory, "clients", $"user_id = {userId}", cancellationToken));
        Assert.Equal(
            0L,
            await CountAsync(factory, "asp_net_users", $"id = {userId} AND password_hash LIKE '%{Password}%'", cancellationToken));
    }

    [Fact]
    public async Task A_second_registration_on_the_same_email_is_a_conflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        var email = UniqueEmail();

        using (var first = await Register(client, email, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var second = await Register(client, email, cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await AssertFailure(second, ErrorCode.AUTH_EMAIL_ALREADY_IN_USE, cancellationToken);

        Assert.Equal(
            1L,
            await CountAsync(factory, "asp_net_users", $"normalized_email = '{email.ToUpperInvariant()}'", cancellationToken));
    }

    [Fact]
    public async Task A_national_phone_number_is_refused_naming_the_field()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            Payload(UniqueEmail(), phoneNumber: "044 123 45 67"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var error = (await ReadAsync(response, cancellationToken)).GetProperty("error");

        Assert.Equal(nameof(ErrorCode.COMMON_VALIDATION_FAILED), error.GetProperty("code").GetString());

        // NFR-4: the key is the name the caller sent, so a form can attach the message to the input.
        var message = error.GetProperty("fields").GetProperty("phoneNumber")[0].GetString();

        Assert.False(string.IsNullOrWhiteSpace(message));

        // NFR-14: what arrives is the Ukrainian sentence, not the code the validator named.
        Assert.DoesNotContain(nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID), message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mismatched_confirmation_is_refused_naming_the_field()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            Payload(UniqueEmail(), passwordConfirmation: "Something-Else-1"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var error = (await ReadAsync(response, cancellationToken)).GetProperty("error");

        Assert.True(error.GetProperty("fields").TryGetProperty("passwordConfirmation", out _));
    }

    [Fact]
    public async Task A_password_below_the_policy_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        // Present, so the validator is satisfied, and below Identity's policy, so the store refuses
        // it - the row that proves both halves answer with the same code (NFR-2).
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            Payload(UniqueEmail(), password: "abc", passwordConfirmation: "abc"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var error = (await ReadAsync(response, cancellationToken)).GetProperty("error");

        Assert.True(error.GetProperty("fields").TryGetProperty("password", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unknown_email_and_a_wrong_password_are_indistinguishable(bool registerFirst)
    {
        // The whole point: an attacker must not be able to tell an account that exists from one that
        // does not, so both raise AUTH_INVALID_CREDENTIALS and both answer 401.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        var email = UniqueEmail();

        if (registerFirst)
        {
            using var registration = await Register(client, email, cancellationToken);

            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        }

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/sign-in", UriKind.Relative),
            new { email, password = "Wrong-Passw0rd" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertFailure(response, ErrorCode.AUTH_INVALID_CREDENTIALS, cancellationToken);
    }

    [Fact]
    public async Task A_bearer_token_reads_its_own_profile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        var (userId, token) = await RegisterAndSignIn(client, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/users/{userId}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = (await ReadAsync(response, cancellationToken)).GetProperty("data");

        Assert.Equal(userId, profile.GetProperty("userId").GetInt32());
        Assert.Equal("Client", profile.GetProperty("role").GetString());

        // FR-5: the licence number is a driver's field, so a client's profile carries null.
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("licenseNumber").ValueKind);
    }

    [Fact]
    public async Task Another_users_profile_is_forbidden_and_discloses_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        var (_, token) = await RegisterAndSignIn(client, cancellationToken);
        var (otherUserId, _) = await RegisterAndSignIn(client, cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/api/users/{otherUserId}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var envelope = await ReadAsync(response, cancellationToken);

        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("data").ValueKind);
        Assert.Equal(
            nameof(ErrorCode.AUTH_FORBIDDEN),
            envelope.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_seeded_admin_reads_any_profile()
    {
        // AD-4's override, end to end: the admin holds one role row like everybody else and passes
        // the check by rule inside the guard.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        var (userId, _) = await RegisterAndSignIn(client, cancellationToken);
        var adminToken = await SignIn(client, TestConfiguration.AdminEmail, TestConfiguration.AdminPassword, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/users/{userId}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_no_credentials_gets_the_envelope_not_a_problem_document()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/users/1", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var envelope = await ReadAsync(response, cancellationToken);

        Assert.False(envelope.GetProperty("success").GetBoolean());
        Assert.Equal(
            nameof(ErrorCode.AUTH_UNAUTHENTICATED),
            envelope.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_cookie_presented_to_the_api_is_not_credentials()
    {
        // The path selector forwards /api/* to the JWT handler only, so the cookie is never even
        // looked at. A per-endpoint attribute could not make that guarantee.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        var email = UniqueEmail();

        using (var registration = await Register(client, email, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        }

        var cookie = await SignInWithCookieAsync(factory, email, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/users/1", UriKind.Relative));
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertFailure(response, ErrorCode.AUTH_UNAUTHENTICATED, cancellationToken);
    }

    [Fact]
    public async Task A_bearer_token_presented_off_the_api_is_treated_as_anonymous()
    {
        // The other half of the selector. A protected page reached with a bearer token and no cookie
        // is an anonymous browser as far as the cookie handler is concerned, and gets the redirect a
        // browser expects rather than a JSON envelope.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var (_, token) = await RegisterAndSignIn(client, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/profile", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sign_in_form_issues_the_cookie_and_lands_on_the_roles_route()
    {
        // FR-7 through the adapter that actually decides it. A circuit cannot write Set-Cookie, so
        // this page renders statically and its POST is the only path that issues a session.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        var email = UniqueEmail();

        using (var registration = await Register(client, email, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        }

        var result = await PostSignInFormAsync(factory, email, Password, cancellationToken);

        Assert.NotNull(result.SessionCookie);

        // The redirect is absolute - NavigationManager resolves the route against the base URI - so
        // the assertion is about the route, not about the host the test server happened to use.
        Assert.EndsWith(
            LandingRoute.For(UserRole.Client),
            result.Location ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sign_in_form_re_renders_the_localized_refusal_and_issues_no_cookie()
    {
        // The failure half of the same form. The message is resolved through the catalogue rather
        // than typed in, so this asserts "the user is told, in Ukrainian, what the REST adapter would
        // have said" instead of pinning a sentence that a translation pass would break.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        var email = UniqueEmail();

        using (var registration = await Register(client, email, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        }

        var result = await PostSignInFormAsync(factory, email, "Wrong-Passw0rd", cancellationToken);

        Assert.Null(result.SessionCookie);
        Assert.Equal(HttpStatusCode.OK, result.Status);

        using var scope = factory.Services.CreateScope();
        var localizer = scope.ServiceProvider
            .GetRequiredService<IStringLocalizer<DriveTrack.Web.Resources.ErrorMessages>>();
        var expected = localizer[nameof(ErrorCode.AUTH_INVALID_CREDENTIALS)].Value;

        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Contains(expected, WebUtility.HtmlDecode(result.Body), StringComparison.Ordinal);

        // InputText renders value="@CurrentValueAsString", so a model that still held the password
        // would write it into the refusal's HTML - into the page cache, the proxy log and view
        // source. The field comes back empty instead.
        Assert.DoesNotContain("Wrong-Passw0rd", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_register_form_issues_the_cookie_and_lands_on_the_roles_route()
    {
        // FR-1 through the screen rather than the endpoint: registration signs the new client in on
        // the spot, and the cookie half of that is the part the JSON tests never touch.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);

        var email = UniqueEmail();
        var result = await PostRegisterFormAsync(factory, email, Password, Password, cancellationToken);

        Assert.NotNull(result.SessionCookie);
        Assert.EndsWith(
            LandingRoute.For(UserRole.Client),
            result.Location ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_register_form_re_renders_the_localized_field_refusal_and_issues_no_cookie()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);

        var result = await PostRegisterFormAsync(
            factory,
            UniqueEmail(),
            Password,
            "Something-Else-1",
            cancellationToken);

        Assert.Null(result.SessionCookie);
        Assert.Equal(HttpStatusCode.OK, result.Status);

        using var scope = factory.Services.CreateScope();
        var localizer = scope.ServiceProvider
            .GetRequiredService<IStringLocalizer<DriveTrack.Web.Resources.ErrorMessages>>();
        var expected = localizer[nameof(ErrorCode.AUTH_PASSWORD_CONFIRMATION_MISMATCH)].Value;

        Assert.Contains(expected, WebUtility.HtmlDecode(result.Body), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Something-Else-1", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cookie_the_form_issues_authenticates_a_protected_page()
    {
        // Without this the entire cookie half could stop authenticating and the suite would stay
        // green: every other cookie assertion is about a cookie being refused somewhere.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var api = factory.CreateClient();

        var email = UniqueEmail();

        using (var registration = await Register(api, email, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        }

        var cookie = await SignInWithCookieAsync(factory, email, cancellationToken);

        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        using var withCookie = new HttpRequestMessage(HttpMethod.Get, new Uri("/profile", UriKind.Relative));
        withCookie.Headers.Add("Cookie", cookie);

        using var authenticated = await client.SendAsync(withCookie, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);

        // The same request without it, so the 200 above is the cookie's doing and not the page's.
        using var anonymous = await client.GetAsync(new Uri("/profile", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Contains(
            "/sign-in",
            anonymous.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_out_clears_the_session_cookie_and_leaves_the_browser_anonymous()
    {
        // FR-6. The cookie is stateless, so what sign-out can promise is that the browser stops
        // holding one - this drives the flow the way a browser would, cookie jar and all.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var email = UniqueEmail();

        using (var registration = await Register(client, email, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        }

        // Sign in on this client so its cookie jar holds the session, then read a statically
        // rendered page back for the sign-out form's antiforgery token - which is bound to the
        // signed-in identity, so it has to be fetched after signing in, not before.
        using (var page = await client.GetAsync(new Uri("/sign-in", UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);

            var html = await page.Content.ReadAsStringAsync(cancellationToken);

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["_handler"] = "signIn",
                ["Form.Email"] = email,
                ["Form.Password"] = Password,
                ["__RequestVerificationToken"] = AntiforgeryToken(html),
            });

            using var signIn = await client.PostAsync(
                new Uri("/sign-in", UriKind.Relative),
                form,
                cancellationToken);

            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        }

        using (var protectedPage = await client.GetAsync(new Uri("/profile", UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, protectedPage.StatusCode);
        }

        string signOutToken;

        using (var page = await client.GetAsync(new Uri("/sign-in", UriKind.Relative), cancellationToken))
        {
            signOutToken = AntiforgeryToken(await page.Content.ReadAsStringAsync(cancellationToken));
        }

        using var signOutForm = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = signOutToken,
        });

        using var signOut = await client.PostAsync(
            new Uri("/sign-out", UriKind.Relative),
            signOutForm,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);

        var cleared = signOut.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(value => value.StartsWith("drivetrack.session=", StringComparison.Ordinal))
            : null;

        Assert.NotNull(cleared);
        Assert.StartsWith("drivetrack.session=;", cleared, StringComparison.Ordinal);

        using var afterSignOut = await client.GetAsync(new Uri("/profile", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, afterSignOut.StatusCode);
        Assert.Contains(
            "/sign-in",
            afterSignOut.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_out_without_the_antiforgery_token_is_refused()
    {
        // The endpoint binds no form value, so UseAntiforgery does not cover it on its own. Without
        // the explicit validation any cross-site form could sign a user out.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var empty = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal));
        using var response = await client.PostAsync(new Uri("/sign-out", UriKind.Relative), empty, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_bearer_token_is_refused_with_the_envelope()
    {
        // AD-13 is why the issuer takes a TimeProvider at all: back-date the host's clock and the
        // token it signs is already past its expiry against the clock the handler validates with,
        // so the configured lifetime is exercised without waiting an hour for it.
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(-2));

        await using var factory = await ApiFactory.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            useProbeAuthentication: false,
            clock: clock);

        using var client = factory.CreateClient();

        var (userId, token) = await RegisterAndSignIn(client, cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/api/users/{userId}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertFailure(response, ErrorCode.AUTH_UNAUTHENTICATED, cancellationToken);
    }

    [Fact]
    public async Task Both_schemes_issue_the_same_claims_and_read_back_as_the_same_caller()
    {
        // The two schemes are written in different layers - the cookie here, the token in
        // Infrastructure - and nothing else compares them. A claim dropped or misspelled on either
        // side would leave that half's callers anonymous with every other test still green.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);

        var account = new UserAccount(
            new UserId(41),
            "Олена",
            "Петренко",
            "olena@drivetrack.test",
            UserRole.Client,
            DriverId: null,
            ClientId: new ClientId(7));

        var session = new AuthenticatedSession(
            account.Id,
            account.FirstName,
            account.LastName,
            account.Email,
            account.Role,
            AccessToken: string.Empty,
            account.DriverId,
            account.ClientId);

        // The cookie half, read back through the adapter that answers ICurrentUser.
        var principal = ClaimsFactory.Build(session);
        var context = new DefaultHttpContext { User = principal };
        var caller = new CurrentUser(
            new HttpContextAccessor { HttpContext = context },
            new ServiceCollection().BuildServiceProvider());

        Assert.True(caller.IsAuthenticated);
        Assert.Equal(account.Id, caller.UserId);
        Assert.Equal(UserRole.Client, caller.Role);
        Assert.Equal(account.ClientId, caller.ClientId);
        Assert.Null(caller.DriverId);

        // The circuit half of the same adapter. An interactive screen has no HttpContext at all -
        // the request that opened the circuit finished long ago - so the caller can only come from
        // the AuthenticationStateProvider. The assertions above never reach that fallback, so
        // without these the whole interactive half could stop authenticating with the suite green.
        var circuitServices = new ServiceCollection();

        circuitServices.AddSingleton<AuthenticationStateProvider>(
            new StubAuthenticationStateProvider(principal));

        var circuitCaller = new CurrentUser(
            new HttpContextAccessor(),
            circuitServices.BuildServiceProvider());

        Assert.True(circuitCaller.IsAuthenticated);
        Assert.Equal(account.Id, circuitCaller.UserId);
        Assert.Equal(UserRole.Client, circuitCaller.Role);
        Assert.Equal(account.ClientId, circuitCaller.ClientId);
        Assert.Null(circuitCaller.DriverId);

        // The bearer half, decoded from the token the registered issuer signs for the same account.
        var issued = factory.Services.GetRequiredService<IAccessTokenIssuer>().Issue(account);
        var token = new JsonWebToken(issued);

        foreach (var claimType in new[]
                 {
                     DriveTrackClaimTypes.UserId,
                     DriveTrackClaimTypes.Role,
                     DriveTrackClaimTypes.Name,
                     DriveTrackClaimTypes.Email,
                     DriveTrackClaimTypes.ClientId,
                 })
        {
            Assert.Equal(
                principal.FindFirst(claimType)?.Value,
                token.Claims.FirstOrDefault(claim => claim.Type == claimType)?.Value);
        }

        // Absent on both sides, for the same reason: this account is not a driver.
        Assert.DoesNotContain(token.Claims, claim => claim.Type == DriveTrackClaimTypes.DriverId);
        Assert.Null(principal.FindFirst(DriveTrackClaimTypes.DriverId));
    }

    [Fact]
    public void A_role_with_no_landing_screen_is_a_failure_not_a_default()
    {
        // FR-7's map is total for the same reason the status map is: a fifth role added without a
        // screen must fail loudly rather than quietly sending someone to the home page.
        Assert.Throws<ArgumentOutOfRangeException>(() => LandingRoute.For((UserRole)99));
    }

    [Fact]
    public async Task The_shell_offers_sign_out_when_signed_in_and_the_anonymous_entry_points_otherwise()
    {
        // FR-12. The nav is the only user-reachable path to POST /sign-out, and the sign-out flow
        // test builds its own request rather than following a link - so the control could be dropped,
        // or the two AuthorizeView arms swapped, with every other assertion in this file still green.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        using (var anonymous = await client.GetAsync(new Uri("/sign-in", UriKind.Relative), cancellationToken))
        {
            var html = await anonymous.Content.ReadAsStringAsync(cancellationToken);

            Assert.DoesNotContain("action=\"/sign-out\"", html, StringComparison.Ordinal);
            Assert.Contains("href=\"register\"", html, StringComparison.Ordinal);
        }

        var email = UniqueEmail();

        using (var registration = await Register(client, email, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        }

        var cookie = await SignInWithCookieAsync(factory, email, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/sign-in", UriKind.Relative));
        request.Headers.Add("Cookie", cookie);

        using var signedIn = await client.SendAsync(request, cancellationToken);
        var signedInHtml = await signedIn.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("action=\"/sign-out\"", signedInHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"profile\"", signedInHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_a_sign_in_issues_carries_the_callers_subtype_id()
    {
        // The matrix's sign-in row promises the subtype id in the token. Registration builds its
        // session from the client row it has just written; sign-in rebuilds it through MapAsync,
        // which reads the row back - a different path, and until now only registration's token
        // ever reached HTTP with a subtype id on it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        var email = UniqueEmail();
        int userId;

        using (var registration = await Register(client, email, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);

            userId = (await ReadAsync(registration, cancellationToken))
                .GetProperty("data")
                .GetProperty("userId")
                .GetInt32();
        }

        var token = new JsonWebToken(await SignIn(client, email, Password, cancellationToken));

        Assert.Equal(
            userId.ToString(CultureInfo.InvariantCulture),
            token.Claims.FirstOrDefault(claim => claim.Type == DriveTrackClaimTypes.UserId)?.Value);
        Assert.Equal(
            nameof(UserRole.Client),
            token.Claims.FirstOrDefault(claim => claim.Type == DriveTrackClaimTypes.Role)?.Value);

        var clientId = token.Claims.FirstOrDefault(claim => claim.Type == DriveTrackClaimTypes.ClientId)?.Value;

        Assert.False(string.IsNullOrEmpty(clientId));

        // Tied to the row rather than merely present: a claim carrying the user id, or any other
        // number, would satisfy a presence check and be wrong in exactly the way that matters.
        Assert.Equal(
            1L,
            await CountAsync(factory, "clients", $"user_id = {userId} AND id = {int.Parse(clientId, CultureInfo.InvariantCulture)}", cancellationToken));

        Assert.DoesNotContain(token.Claims, claim => claim.Type == DriveTrackClaimTypes.DriverId);
    }

    [Fact]
    public async Task The_landing_page_is_anonymous()
    {
        // FR-99: the one screen a visitor with no account can reach.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateAsync(cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A host whose default scheme is the production path selector, not the probe. Every assertion
    /// in this file is about the real cookie and JWT handlers, so opting out is the whole point.
    /// </summary>
    private Task<ApiFactory> CreateAsync(CancellationToken cancellationToken) =>
        ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken, useProbeAuthentication: false);

    private static string UniqueEmail() => Guid.NewGuid().ToString("N")[..12] + "@drivetrack.test";

    private static object Payload(
        string email,
        string phoneNumber = "+380441234567",
        string password = Password,
        string? passwordConfirmation = null) =>
        new
        {
            firstName = "Олена",
            lastName = "Петренко",
            email,
            phoneNumber,
            password,
            passwordConfirmation = passwordConfirmation ?? password,
        };

    private static Task<HttpResponseMessage> Register(
        HttpClient client,
        string email,
        CancellationToken cancellationToken) =>
        client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            Payload(email),
            cancellationToken);

    private static async Task<(int UserId, string Token)> RegisterAndSignIn(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var email = UniqueEmail();

        using var response = await Register(client, email, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = (await ReadAsync(response, cancellationToken)).GetProperty("data");

        return (session.GetProperty("userId").GetInt32(), session.GetProperty("accessToken").GetString()!);
    }

    private static async Task<string> SignIn(
        HttpClient client,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/sign-in", UriKind.Relative),
            new { email, password },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await ReadAsync(response, cancellationToken))
            .GetProperty("data")
            .GetProperty("accessToken")
            .GetString()!;
    }

    /// <summary>
    /// Signs in through the Blazor form and returns the <c>Set-Cookie</c> value, so the cookie under
    /// test is the one the production sign-in path actually issues.
    /// </summary>
    private static async Task<string> SignInWithCookieAsync(
        ApiFactory factory,
        string email,
        CancellationToken cancellationToken)
    {
        var result = await PostSignInFormAsync(factory, email, Password, cancellationToken);

        Assert.True(
            result.SessionCookie is not null,
            "The sign-in form did not issue a session cookie. Response: "
                + (int)result.Status + " " + result.Body);

        return result.SessionCookie.Split(';')[0];
    }

    /// <summary>
    /// Drives a real statically rendered Blazor form: fetch the page for its antiforgery token, post
    /// the fields back, and report what came out. These forms are the production cookie path, so
    /// both their success and their refusal are asserted through them rather than through a replica.
    /// </summary>
    private static Task<FormResult> PostSignInFormAsync(
        ApiFactory factory,
        string email,
        string password,
        CancellationToken cancellationToken) =>
        PostFormAsync(
            factory,
            "/sign-in",
            "signIn",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Form.Email"] = email,
                ["Form.Password"] = password,
            },
            cancellationToken);

    /// <summary>
    /// Posts the register form. The field names are the ones the page renders, which are derived
    /// from the binding expression - a mismatch between them and the model property binds nothing
    /// and refuses every post, so spelling them out here is part of what is under test.
    /// </summary>
    private static Task<FormResult> PostRegisterFormAsync(
        ApiFactory factory,
        string email,
        string password,
        string? passwordConfirmation,
        CancellationToken cancellationToken) =>
        PostFormAsync(
            factory,
            "/register",
            "register",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Form.FirstName"] = "Олена",
                ["Form.LastName"] = "Петренко",
                ["Form.Email"] = email,
                ["Form.PhoneNumber"] = "+380441234567",
                ["Form.Password"] = password,
                ["Form.PasswordConfirmation"] = passwordConfirmation ?? password,
            },
            cancellationToken);

    private static async Task<FormResult> PostFormAsync(
        ApiFactory factory,
        string page_,
        string handler,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        using var page = await client.GetAsync(new Uri(page_, UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var html = await page.Content.ReadAsStringAsync(cancellationToken);
        var token = AntiforgeryToken(html);
        var antiforgeryCookie = page.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? string.Join("; ", setCookies.Select(value => value.Split(';')[0]))
            : string.Empty;

        var values = new Dictionary<string, string>(fields, StringComparer.Ordinal)
        {
            ["_handler"] = handler,
            ["__RequestVerificationToken"] = token,
        };

        using var form = new FormUrlEncodedContent(values);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(page_, UriKind.Relative))
        {
            Content = form,
        };

        if (antiforgeryCookie.Length > 0)
        {
            request.Headers.Add("Cookie", antiforgeryCookie);
        }

        using var response = await client.SendAsync(request, cancellationToken);

        var session = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(value => value.StartsWith("drivetrack.session=", StringComparison.Ordinal))
            : null;

        // Read the body before the client goes out of scope, so the caller is asserting against a
        // string rather than against a stream whose server has been disposed.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return new FormResult(
            response.StatusCode,
            session,
            response.Headers.Location?.OriginalString,
            body);
    }

    /// <summary>What the sign-in form answered: the status, the session cookie and redirect if one was issued, and the rendered page.</summary>
    private sealed record FormResult(
        HttpStatusCode Status,
        string? SessionCookie,
        string? Location,
        string Body);

    private static string AntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, "The sign-in form did not render an antiforgery token.");

        return match.Groups[1].Value;
    }

    private static async Task<JsonElement> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);

        Assert.NotNull(document);

        return document.RootElement;
    }

    private static async Task AssertFailure(
        HttpResponseMessage response,
        ErrorCode expected,
        CancellationToken cancellationToken)
    {
        var envelope = await ReadAsync(response, cancellationToken);

        Assert.False(envelope.GetProperty("success").GetBoolean());
        Assert.Equal(expected.ToString(), envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(envelope.GetProperty("error").GetProperty("message").GetString()));
    }

    /// <summary>
    /// An already-resolved authentication state, the shape the server's circuit provider hands out:
    /// the state is set when the circuit is created, so the task is complete before anything reads it.
    /// </summary>
    private sealed class StubAuthenticationStateProvider(ClaimsPrincipal principal)
        : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private static async Task<long> CountAsync(
        ApiFactory factory,
        string table,
        string predicate,
        CancellationToken cancellationToken)
    {
        var value = await factory.Database.ScalarAsync(
            $"SELECT COUNT(*) FROM {table} WHERE {predicate}",
            cancellationToken);

        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
