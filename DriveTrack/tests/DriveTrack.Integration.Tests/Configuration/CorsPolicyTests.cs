using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DriveTrack.Integration.Tests.Configuration;

/// <summary>
/// NFR-12 at both ends: the policy the running host applies to a cross-origin request, and the
/// refusal that keeps a wildcard from ever reaching it.
/// <para>
/// The policy had no test at all before this suite, which made NFR-12 true by coincidence —
/// replacing <c>WithOrigins(allowedOrigins)</c> with <c>AllowAnyOrigin()</c> would have left the
/// whole solution green. The two preflight cases below are what make the origin list load-bearing.
/// </para>
/// <para>
/// The refusal cases boot a host of their own rather than reading <c>Program.cs</c> as text: the
/// surface under test is a host that declines to start, and the check runs before
/// <c>builder.Build()</c>, so those cases reach their exception without a database and without
/// Docker. Only the two preflight cases ask <see cref="PostgresFixture"/> for a connection string,
/// and the container starts lazily on that first request.
/// </para>
/// </summary>
public class CorsPolicyTests(PostgresFixture postgres)
{
    /// <summary>
    /// Syntactically valid and deliberately unreachable. The refusal cases never get this far, but
    /// the two legal cases do, and they must fail at the migrator rather than find — and migrate —
    /// a PostgreSQL instance that happens to be running on the machine executing the suite.
    /// </summary>
    private const string UnreachableHost = "127.0.0.1";

    private const string UnreachablePort = "1";

    private const string UnreachableConnectionString =
        $"Host={UnreachableHost};Port={UnreachablePort};Database=drivetrack;"
            + "Username=drivetrack;Password=irrelevant;Timeout=1";

    /// <summary>The endpoint the database failure names, kept tied to the connection string above.</summary>
    private const string UnreachableEndpoint = $"{UnreachableHost}:{UnreachablePort}";

    private const string OriginKey = "Cors:AllowedOrigins:0";

    private const string EnvironmentOriginKey = "Cors__AllowedOrigins__0";

    [Fact]
    public async Task A_preflight_from_an_allowed_origin_is_answered_with_that_origin_and_credentials()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var request = Preflight(TestConfiguration.AllowedOrigin);
        using var response = await client.SendAsync(request, cancellationToken);

        Assert.True(
            response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins),
            "The preflight came back without an Access-Control-Allow-Origin header.");
        Assert.Equal(TestConfiguration.AllowedOrigin, Assert.Single(origins));

        // The other half of what NFR-12 is about: this policy allows credentials, which is exactly
        // why the origin list may never be a wildcard.
        Assert.True(
            response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var credentials),
            "The preflight came back without an Access-Control-Allow-Credentials header.");
        Assert.Equal("true", Assert.Single(credentials));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("HTTPS://APP.DRIVETRACK.TEST")]
    [InlineData(TestConfiguration.AllowedOrigin + ":443")]
    public async Task A_preflight_from_a_disallowed_origin_is_answered_without_the_header(string origin)
    {
        // No header rather than a refused status: that is how CORS says no, and it is what a browser
        // reads. A test asserting a status code here would pass against AllowAnyOrigin().
        //
        // The last two are the same host as the allowed origin, spelled the two ways Uri would
        // normalize away. They are what makes the start-up check's strictness more than an assumption:
        // eight of its refusal cases exist only because the middleware compares ordinally, and these
        // are the cases that show a running host doing exactly that. If a future framework version
        // normalized instead, those refusals would become gratuitous and these two would say so.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var request = Preflight(origin);
        using var response = await client.SendAsync(request, cancellationToken);

        Assert.False(response.Headers.TryGetValues("Access-Control-Allow-Origin", out _));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.com")]
    public void A_wildcard_origin_refuses_to_start_naming_the_value_and_the_key(string origin)
    {
        // The subdomain form is pinned to this branch rather than left to the shape theory below,
        // which would accept it under either wording: matching a subdomain wildcard needs
        // SetIsOriginAllowedToAllowWildcardSubdomains, which this policy deliberately never calls,
        // so the value would otherwise be configured, accepted and silently match nothing.
        var refusal = Refusal(origin);

        Assert.Contains("is a wildcard", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(origin, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(OriginKey, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(EnvironmentOriginKey, refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_wildcard_at_a_later_index_refuses_to_start_naming_that_index(int index)
    {
        // Two things at once. A loop that stopped after the first entry would let a wildcard in any
        // further numbered key reach the policy unrefused; and index 2 - with nothing at index 1 -
        // is where naming the line by its position in the bound array rather than by the child's own
        // key would report the problem against a line the operator never wrote.
        var refusal = Refusal(new Dictionary<int, string?>
        {
            [0] = TestConfiguration.AllowedOrigin,
            [index] = "*",
        });

        Assert.Contains($"Cors:AllowedOrigins:{index}", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"Cors__AllowedOrigins__{index}", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app.example.com")]
    [InlineData("/relative")]
    [InlineData("ftp://app.example.com")]
    [InlineData("https://app.example.com/")]
    [InlineData("https://app.example.com/app")]
    [InlineData("https://app.example.com:443")]
    [InlineData("HTTPS://App.Example.com")]
    [InlineData(" https://app.example.com ")]
    [InlineData("https://user:pass@app.example.com")]
    [InlineData("https://@app.example.com")]
    public void A_value_that_is_not_the_origin_a_browser_sends_refuses_to_start_naming_it(string origin)
    {
        // The last five parse perfectly well and are refused anyway, which is the case the message
        // has to be honest about: WithOrigins compares to the Origin header character for character,
        // and Uri strips a default port, lower-cases scheme and host and trims - so each of these is
        // a value that would be configured, accepted and then match nobody. The two @ forms are the
        // ones that survive every other clause: the authority part keeps userinfo verbatim, so both
        // round-trip, and the second has an empty UserInfo into the bargain.
        var refusal = Refusal(origin);

        Assert.Contains("not the origin a browser would send", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(origin, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(OriginKey, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(EnvironmentOriginKey, refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_origin_refuses_to_start_naming_the_key(string origin)
    {
        // compose.dev.yaml forwards Cors__AllowedOrigins__0 unconditionally, so an unset variable
        // arrives as a blank entry rather than as no entry. Allowing no cross-origin caller means
        // deleting the line from both files, and the message has to say so.
        var refusal = Refusal(origin);

        Assert.Contains(OriginKey, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(EnvironmentOriginKey, refusal.Message, StringComparison.Ordinal);

        // The remedy, not just the diagnosis. Without this the sentence that tells an operator the
        // one thing they came to the message for — that the line has to go from both files, because
        // blanking it is what got them here — could be deleted with the suite green.
        Assert.Contains("remove that line from .env", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://app.example.com")]
    public void An_absent_or_explicit_origin_list_is_not_refused(string? origin)
    {
        // The preflight cases above are where a legal origin is shown to work end to end; this is
        // the other half — the values the check must let through.
        AssertStoppedAtTheDatabase(StartupFailure(origin));
    }

    [Fact]
    public void The_origin_env_example_ships_is_one_the_check_accepts()
    {
        // The line every `docker compose up` starts from, run through the check that now guards it.
        // Nothing else here would notice it breaking: every other case configures an origin of its
        // own, so editing this line to a shape the check refuses would abort the container with the
        // whole suite green.
        AssertStoppedAtTheDatabase(StartupFailure(ComposeStack.EnvExample(EnvironmentOriginKey)));

        // And the key is still forwarded, which is what makes that line reach the container at all —
        // and what makes deleting it from .env alone a blank entry rather than no entry.
        Assert.Contains(
            EnvironmentOriginKey,
            ComposeStack.Dev.EnvironmentKeysOf("app"),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// A preflight for the real REST route, shaped the way a browser shapes one: the method it
    /// intends to use, announced before it uses it.
    /// </summary>
    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Options,
            new Uri("/api/deliveries", UriKind.Relative));

        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        return request;
    }

    /// <summary>
    /// The start-up refusal for a configured origin, found by walking the exception chain:
    /// <c>WebApplicationFactory</c> surfaces the entry point's failure wrapped, so pinning the
    /// outermost type would assert against the test host rather than against the check.
    /// </summary>
    private static InvalidOperationException Refusal(string? origin) =>
        Refusal(new Dictionary<int, string?> { [0] = origin });

    /// <inheritdoc cref="Refusal(string?)" />
    private static InvalidOperationException Refusal(IReadOnlyDictionary<int, string?> origins)
    {
        var failure = StartupFailure(origins);

        Assert.NotNull(failure);

        var refusal = Chain(failure)
            .OfType<InvalidOperationException>()
            .FirstOrDefault(exception =>
                exception.Message.Contains("Cors:AllowedOrigins", StringComparison.Ordinal));

        Assert.NotNull(refusal);

        return refusal;
    }

    /// <summary>
    /// Asserts that start-up got past every eager configuration check — this file's and
    /// <c>AddInfrastructure</c>'s, which all open the same way — and then stopped where it should.
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

        // The migrator, reaching for the endpoint the connection string above names and finding
        // nothing there.
        var database = Assert.Single(Chain(failure).OfType<NpgsqlException>());

        Assert.Contains(UnreachableEndpoint, database.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a host with <paramref name="origin"/> configured at index 0 — or, for null, with that
    /// entry genuinely absent — and answers whatever start-up threw, or null if it threw nothing.
    /// </summary>
    private static Exception? StartupFailure(string? origin) =>
        StartupFailure(new Dictionary<int, string?> { [0] = origin });

    /// <inheritdoc cref="StartupFailure(string?)" />
    /// <param name="origins">
    /// The <c>Cors:AllowedOrigins</c> entries by index. A null value removes that index, which is how
    /// a genuinely absent entry is distinguished from a blank one — and how an index is left as a gap
    /// between two configured ones.
    /// </param>
    private static Exception? StartupFailure(IReadOnlyDictionary<int, string?> origins)
    {
        var settings = TestConfiguration.Defaults();
        settings["ConnectionStrings:Default"] = UnreachableConnectionString;

        foreach (var (index, origin) in origins)
        {
            var key = $"Cors:AllowedOrigins:{index}";

            if (origin is null)
            {
                settings.Remove(key);
            }
            else
            {
                settings[key] = origin;
            }
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
    /// The bare minimum <c>ApiFactory</c> does to boot the real pipeline — the Web project's content
    /// root and a settings dictionary — and nothing else. No database is created, because the cases
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
