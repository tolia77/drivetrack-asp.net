using System.Net;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DriveTrack.Integration.Tests.Configuration;

/// <summary>
/// DW-6 at both ends: the <c>Secure</c> attribute a running host actually writes onto
/// <c>drivetrack.session</c>, and the refusal that keeps a value nobody meant from reaching the
/// cookie handler.
/// <para>
/// The two <c>Set-Cookie</c> cases are what make the knob load-bearing. A suite that only asserted
/// the start-up check would stay green if the configured value were read, validated and then never
/// assigned - which is exactly the bug the change is at risk of, because the assignment and the read
/// are ten lines apart in <c>Program.cs</c> and nothing but a running host connects them.
/// </para>
/// <para>
/// The refusal cases boot a host of their own, for the reason <see cref="CorsPolicyTests"/> does: the
/// surface under test is a host that declines to start, the check runs before <c>builder.Build()</c>,
/// and so those cases reach their exception with no database and no Docker. Only the two
/// <c>Set-Cookie</c> cases ask <see cref="PostgresFixture"/> for a connection string.
/// </para>
/// </summary>
public class SessionCookieTests(PostgresFixture postgres)
{
    /// <summary>
    /// Syntactically valid and deliberately unreachable, so the cases that get past the check stop at
    /// AD-20's migrator rather than finding - and migrating - a PostgreSQL instance that happens to
    /// be running on the machine executing the suite.
    /// </summary>
    private const string UnreachableHost = "127.0.0.1";

    private const string UnreachablePort = "1";

    private const string UnreachableConnectionString =
        $"Host={UnreachableHost};Port={UnreachablePort};Database=drivetrack;"
            + "Username=drivetrack;Password=irrelevant;Timeout=1";

    /// <summary>The endpoint the database failure names, kept tied to the connection string above.</summary>
    private const string UnreachableEndpoint = $"{UnreachableHost}:{UnreachablePort}";

    private const string PolicyKey = "Session:CookieSecurePolicy";

    private const string EnvironmentPolicyKey = "Session__CookieSecurePolicy";

    /// <summary>The cookie the sign-in form issues, named as <c>Program.cs</c> names it.</summary>
    private const string SessionCookieName = "drivetrack.session";

    [Fact]
    public async Task With_nothing_configured_the_session_cookie_carries_no_secure_attribute()
    {
        // The default every existing deployment gets, asserted through a real sign-in over plain
        // HTTP: SameAsRequest, and the request is not secure, so the attribute is absent. This is
        // also the regression guard for the whole change - if the new key ever started defaulting to
        // Always, the compose stack would issue a cookie the browser drops and nobody would stay
        // signed in.
        var cancellationToken = TestContext.Current.CancellationToken;

        var cookie = await SessionCookieAsync(settings: null, secure: false, cancellationToken);

        Assert.False(
            IsSecure(cookie),
            $"The session cookie carried 'secure' with no '{PolicyKey}' configured: {cookie}");
    }

    [Theory]
    [InlineData("Always")]
    [InlineData("always")]
    public async Task With_Always_configured_the_session_cookie_carries_the_secure_attribute(string configured)
    {
        // The point of the story. The request is plain HTTP exactly as above, so the only thing that
        // can have changed the header is the configured policy reaching the cookie handler.
        //
        // The lower-case spelling is here rather than only among the start-up cases below because
        // "accepted at start-up" and "applied to the cookie" are different claims: a match that
        // returned the wrong member would still start a host, and only the header says which policy
        // the handler ended up with.
        var cancellationToken = TestContext.Current.CancellationToken;

        var cookie = await SessionCookieAsync(Configured(configured), secure: false, cancellationToken);

        Assert.True(
            IsSecure(cookie),
            $"The session cookie carried no 'secure' with '{PolicyKey}' set to '{configured}': {cookie}");
    }

    [Theory]
    [InlineData("SameAsRequest")]
    [InlineData("None")]
    public async Task With_a_policy_that_demands_nothing_the_session_cookie_carries_no_secure_attribute(
        string configured)
    {
        // The other two members, at the same surface. Over plain HTTP they are indistinguishable -
        // which is the point: neither may quietly behave like Always, and asserting that at start-up
        // would only have shown the value was accepted, not which policy it was accepted as.
        var cancellationToken = TestContext.Current.CancellationToken;

        var cookie = await SessionCookieAsync(Configured(configured), secure: false, cancellationToken);

        Assert.False(
            IsSecure(cookie),
            $"The session cookie carried 'secure' with '{PolicyKey}' set to '{configured}': {cookie}");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("SameAsRequest", true)]
    [InlineData("None", false)]
    public async Task Over_a_secure_request_SameAsRequest_and_None_part_company(
        string? configured,
        bool expected)
    {
        // The one pair of values plain HTTP cannot tell apart: over http:// both leave the attribute
        // off, so every other case in this file would pass on an implementation that mapped the name
        // "None" onto CookieSecurePolicy.SameAsRequest. Here the request is secure, and the two mean
        // opposite things - which is the whole reason None is accepted rather than withheld.
        //
        // The null row is the absent-key branch at the same surface, and it is the only place that
        // branch is observable: over plain HTTP a default of None would look exactly like the
        // SameAsRequest the code returns, so the unconfigured case above would pass on a host that
        // had quietly stopped marking the cookie Secure for every deployment predating this story.
        var cancellationToken = TestContext.Current.CancellationToken;

        var settings = configured is null ? null : Configured(configured);

        var cookie = await SessionCookieAsync(settings, secure: true, cancellationToken);

        Assert.Equal(expected, IsSecure(cookie));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("SameAsRequest")]
    [InlineData("Always")]
    [InlineData("None")]
    [InlineData("always")]
    [InlineData("ALWAYS")]
    [InlineData("sameasrequest")]
    [InlineData("none")]
    public void A_member_name_in_any_casing_is_not_refused(string? configured)
    {
        // All three members, and the casing an operator types rather than the casing the enum
        // declares. Null is the absent case: no entry at all, which has to keep starting.
        AssertStoppedAtTheDatabase(StartupFailure(configured));
    }

    [Theory]
    [InlineData("Maybe")]
    [InlineData("Secure")]
    [InlineData("Always,None")]
    [InlineData(" Always ")]
    public void A_value_that_names_no_policy_refuses_to_start_naming_it(string configured)
    {
        // The last two are the values Enum.TryParse would have taken and nobody would have meant: it
        // accepts comma-separated combinations, and it trims. A padded value that parses here would
        // be a value an operator could not see the problem with in .env either.
        AssertRefusal(Refusal(configured), configured);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void A_numeric_value_refuses_to_start_rather_than_meaning_an_ordinal(string configured)
    {
        // Pinned to a branch of its own because this is the one refusal with a cost: Enum.TryParse
        // would accept all three, and the ordinals are arbitrary - "0" is SameAsRequest, "1" is
        // Always, "2" is None - so a digit an operator reached for to mean "off" or "on" would select
        // whichever member happens to sit at that position. A value that picks a policy by accident is
        // worth refusing outright.
        AssertRefusal(Refusal(configured), configured);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_refuses_to_start_rather_than_taking_the_default(string configured)
    {
        // Absent is not blank. Absent takes SameAsRequest; a line somebody emptied is a line somebody
        // edited, and treating it as "take the default" would make an operator's intent unknowable.
        var refusal = Refusal(configured);

        Assert.Contains(PolicyKey, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(EnvironmentPolicyKey, refusal.Message, StringComparison.Ordinal);

        // The remedy, not just the diagnosis: the one thing an operator came to this message for is
        // that blanking the line is not how the default is taken - deleting it is.
        Assert.Contains("remove it entirely", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_policy_env_example_ships_is_one_the_check_accepts()
    {
        // The line every `docker compose up` starts from, run through the check that now guards it.
        // Nothing else here would notice it breaking: every other case configures a value of its own,
        // so editing this line to a spelling the check refuses would abort the container with the
        // whole suite green.
        var shipped = ComposeStack.EnvExample(EnvironmentPolicyKey);

        AssertStoppedAtTheDatabase(StartupFailure(shipped));

        // Accepted is not enough. Always is accepted too, and shipping it here would hand every
        // operator who copies this file a Secure cookie over the plain HTTP this stack terminates -
        // a cookie the browser drops, so nobody stays signed in. Only one of the three values is
        // right for the topology this repository ships, so the test names it.
        Assert.Equal(nameof(CookieSecurePolicy.SameAsRequest), shipped, StringComparer.Ordinal);

        // And the key is still forwarded, which is what makes that line reach the container at all. A
        // key absent from compose is a key the app never sees, however carefully .env documents it.
        Assert.Contains(
            EnvironmentPolicyKey,
            ComposeStack.Prod.EnvironmentKeysOf("app"),
            StringComparer.Ordinal);
    }

    [Fact]
    public void Compose_defaults_the_key_rather_than_forwarding_it_bare()
    {
        // The neighbouring Cors__ and Smtp__Host lines are forwarded bare on purpose, so `${...}` is
        // a shape somebody could copy onto this key in good faith - and it would be wrong: compose
        // substitutes a bare reference to an unset variable with the empty string, the check refuses
        // a blank value, and every container whose .env predates this key would stop coming up. The
        // key's presence alone cannot see that, because the name is identical either way.
        var forwarded = ComposeStack.Prod.EnvironmentValueOf("app", EnvironmentPolicyKey);

        var prefix = "${" + EnvironmentPolicyKey + ":-";

        Assert.True(
            forwarded.StartsWith(prefix, StringComparison.Ordinal)
                && forwarded.EndsWith('}'),
            $"compose.prod.yaml forwards '{EnvironmentPolicyKey}' as '{forwarded}', which supplies "
                + $"no default. It has to read '{prefix}<value>}}': an .env predating this key leaves "
                + "the variable unset, a bare reference forwards that as the empty string, and a "
                + "blank value is one the app refuses to start on.");

        // And the default it supplies has to be a value the check accepts, for the reason the
        // .env.example case above exists: this is the value a container actually boots with whenever
        // the operator's .env says nothing, which is every .env written before this story.
        var defaulted = forwarded[prefix.Length..^1];

        AssertStoppedAtTheDatabase(StartupFailure(defaulted));

        // And, as above, accepted is not the claim - SameAsRequest is. This default is what the
        // plain-HTTP stack boots with unless an operator overrides it, so editing it to Always here
        // would break sign-in for every `docker compose up` while leaving the shape check green.
        Assert.Equal(nameof(CookieSecurePolicy.SameAsRequest), defaulted, StringComparer.Ordinal);
    }

    /// <summary>The one setting a behavioural case boots its host with.</summary>
    private static Dictionary<string, string?> Configured(string policy) =>
        new(StringComparer.Ordinal) { [PolicyKey] = policy };

    /// <summary>
    /// Signs in through the real <c>/sign-in</c> form against a host booted with
    /// <paramref name="settings"/>, and answers the whole <c>Set-Cookie</c> value the session cookie
    /// arrived in - attributes included, because the attributes are what is under test.
    /// </summary>
    /// <remarks>
    /// The form rather than a hand-built <c>SignInAsync</c>: <c>SignIn.razor</c> is the only
    /// production path that writes this header, and a replica of it would be asserting against the
    /// test's own idea of the cookie handler's options.
    /// </remarks>
    /// <param name="secure">
    /// True drives the host over <c>https://</c>, which is what makes <c>Request.IsHttps</c> true
    /// inside the pipeline and therefore the only way <c>SameAsRequest</c> and <c>None</c> can be
    /// told apart at all.
    /// </param>
    private async Task<string> SessionCookieAsync(
        IReadOnlyDictionary<string, string?>? settings,
        bool secure,
        CancellationToken cancellationToken)
    {
        // useProbeAuthentication: false, so the cookie scheme under test is the production one rather
        // than the probe scheme the rest of the suite defaults to.
        await using var factory = await ApiFactory.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            useProbeAuthentication: false,
            settings: settings);

        // No redirect following and no cookie container: the header has to be read exactly as the
        // server wrote it, and a handler that stored the cookie would hand back its own rendering.
        //
        // The base address carries the scheme all the way through: TestServer takes the request URI's
        // scheme as the request's, so https here is what Request.IsHttps reads.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri(secure ? "https://localhost" : "http://localhost"),
        });

        using var page = await client.GetAsync(new Uri("/sign-in", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var html = await page.Content.ReadAsStringAsync(cancellationToken);

        var antiforgery = page.Headers.TryGetValues("Set-Cookie", out var issued)
            ? string.Join("; ", issued.Select(value => value.Split(';')[0]))
            : string.Empty;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Form.Email"] = TestConfiguration.AdminEmail,
            ["Form.Password"] = TestConfiguration.AdminPassword,
            ["_handler"] = "signIn",
            ["__RequestVerificationToken"] = AntiforgeryToken(html),
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/sign-in", UriKind.Relative))
        {
            Content = form,
        };

        if (antiforgery.Length > 0)
        {
            request.Headers.Add("Cookie", antiforgery);
        }

        using var response = await client.SendAsync(request, cancellationToken);

        var session = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(value =>
                value.StartsWith(SessionCookieName + "=", StringComparison.Ordinal))
            : null;

        // Read before the client goes out of scope, so the failure below is a string rather than a
        // stream whose server has been disposed. The status alone would say almost nothing: a refused
        // sign-in re-renders the form at 200, so the body - which carries the failure banner - is the
        // only thing that names the cause.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.True(
            session is not null,
            "The sign-in form issued no session cookie, so its Secure attribute cannot be read. "
                + $"Response: {(int)response.StatusCode}, Location: "
                + $"{response.Headers.Location?.OriginalString ?? "(none)"}, body: {body}");

        return session;
    }

    /// <summary>
    /// Whether a <c>Set-Cookie</c> value carries the <c>Secure</c> attribute.
    /// </summary>
    /// <remarks>
    /// Split on the attribute separator rather than searched for as a substring: "secure" occurs
    /// inside a base64 cookie value often enough that a substring test would pass on a host that
    /// never set the flag at all.
    /// </remarks>
    private static bool IsSecure(string setCookie) =>
        setCookie
            .Split(';')
            .Skip(1)
            .Any(attribute => string.Equals(attribute.Trim(), "secure", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Asserts the anatomy every refusal message owes an operator.</summary>
    private static void AssertRefusal(InvalidOperationException refusal, string configured)
    {
        Assert.Contains($"'{configured}'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(PolicyKey, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(EnvironmentPolicyKey, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(".env.example", refusal.Message, StringComparison.Ordinal);

        // The three values it will take, so the message answers the question it provokes.
        foreach (var name in Enum.GetNames<CookieSecurePolicy>())
        {
            Assert.Contains($"'{name}'", refusal.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The start-up refusal for a configured value, found by walking the exception chain:
    /// <c>WebApplicationFactory</c> surfaces the entry point's failure wrapped, so pinning the
    /// outermost type would assert against the test host rather than against the check.
    /// </summary>
    private static InvalidOperationException Refusal(string? configured)
    {
        var failure = StartupFailure(configured);

        Assert.NotNull(failure);

        var refusal = Chain(failure)
            .OfType<InvalidOperationException>()
            .FirstOrDefault(exception => exception.Message.Contains(PolicyKey, StringComparison.Ordinal));

        Assert.NotNull(refusal);

        return refusal;
    }

    /// <summary>
    /// Asserts that start-up got past every eager configuration check - this key's, the CORS list's
    /// and <c>AddInfrastructure</c>'s, which all open the same way - and then stopped where it should.
    /// <para>
    /// Both halves are load-bearing. Absence alone would pass on a host that failed earlier for some
    /// unrelated reason, or that threw nothing at all; the database failure is positive evidence that
    /// the host was built and reached AD-20's migrator, which is the first thing after
    /// <c>builder.Build()</c> and well past the check under test.
    /// </para>
    /// </summary>
    private static void AssertStoppedAtTheDatabase(Exception? failure)
    {
        Assert.NotNull(failure);

        Assert.DoesNotContain(
            Chain(failure).OfType<InvalidOperationException>(),
            exception => exception.Message.StartsWith("Configuration value '", StringComparison.Ordinal));

        var database = Assert.Single(Chain(failure).OfType<NpgsqlException>());

        Assert.Contains(UnreachableEndpoint, database.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a host with <paramref name="configured"/> at <c>Session:CookieSecurePolicy</c> - or,
    /// for null, with that entry genuinely absent - and answers whatever start-up threw, or null if
    /// it threw nothing.
    /// </summary>
    private static Exception? StartupFailure(string? configured)
    {
        var settings = TestConfiguration.Defaults();
        settings["ConnectionStrings:Default"] = UnreachableConnectionString;

        if (configured is not null)
        {
            settings[PolicyKey] = configured;
        }

        using var factory = new ConfiguredHost(settings);

        // Resolving anything from the host is what runs Program.cs's top-level statements, and
        // therefore what runs the check.
        return Record.Exception(() => factory.Services);
    }

    /// <summary>Every exception reachable from <paramref name="exception"/>, itself included.</summary>
    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        if (exception is null)
        {
            yield break;
        }

        yield return exception;

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
            {
                yield return inner;
            }

            yield break;
        }

        foreach (var inner in Chain(exception.InnerException))
        {
            yield return inner;
        }
    }

    /// <summary>
    /// The bare minimum <c>ApiFactory</c> does to boot the real pipeline - the Web project's content
    /// root and a settings dictionary - and nothing else. No database is created, because the cases
    /// that use this never reach one.
    /// </summary>
    private sealed class ConfiguredHost(IReadOnlyDictionary<string, string?> settings)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseContentRoot(RepositoryLayout.ProjectDirectory("DriveTrack.Web"));

            foreach (var setting in settings)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }
    }
}
