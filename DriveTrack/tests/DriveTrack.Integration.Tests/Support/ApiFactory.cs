using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// Boots the real <c>Program.cs</c> pipeline over a database of its own.
/// <para>
/// One factory for every HTTP test, so each asserts against the adapter the container runs - the
/// same result filter, the same branch middleware, the same two suppressions - rather than against
/// a hand-assembled approximation of them. The only additions are the probe surface, the probe
/// scheme and its policy; nothing in the production registration is replaced.
/// </para>
/// </summary>
internal sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly TestDatabase _database;
    private readonly bool _useProbeAuthentication;
    private readonly TimeProvider? _clock;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly IReadOnlyDictionary<string, string?>? _settings;

    private ApiFactory(
        TestDatabase database,
        bool useProbeAuthentication,
        TimeProvider? clock,
        Action<IServiceCollection>? configureServices,
        IReadOnlyDictionary<string, string?>? settings)
    {
        _database = database;
        _useProbeAuthentication = useProbeAuthentication;
        _clock = clock;
        _configureServices = configureServices;
        _settings = settings;
    }

    /// <summary>
    /// The database this host runs on, so a test can seed the rows an endpoint will then violate.
    /// The same instance the pipeline uses - seeding a second one would prove nothing.
    /// </summary>
    public TestDatabase Database => _database;

    /// <summary>
    /// Creates the host's database first, because <c>Program.cs</c> migrates at start-up (AD-20) and
    /// would abort against a database that does not exist.
    /// </summary>
    /// <param name="adminConnectionString">Connection string of the container's own database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="useProbeAuthentication">
    /// True - the default - registers the probe scheme last, which makes it the default and is what
    /// keeps the Epic 1 envelope tests asserting against a principal they control. The identity
    /// suite passes false so the real cookie and JWT schemes, and the path selector between them,
    /// are the ones under test.
    /// </param>
    /// <param name="clock">
    /// A clock for the host to issue tokens against (AD-13). Null takes the real one; a back-dated
    /// <see cref="FixedTimeProvider"/> is how token expiry is exercised without waiting for it.
    /// </param>
    /// <param name="configureServices">
    /// Registrations applied last, after everything else this factory adds, so they win the
    /// resolve. This is how a fake <c>IGeocoder</c> or <c>IEmailSender</c> takes the place of the
    /// real adapter: AD-12's ports are the two things in this system that reach outside the
    /// process, and a suite that let them do it would be asserting against somebody else's uptime.
    /// </param>
    /// <param name="settings">
    /// Configuration applied after <see cref="TestConfiguration.Defaults"/>, so a suite can add a key
    /// the defaults leave out or override one they set. This is how a behaviour that is chosen by
    /// configuration - <c>Session:CookieSecurePolicy</c> is the first - gets asserted against a real
    /// host booted the way the container boots, rather than against the option object in isolation.
    /// </param>
    public static async Task<ApiFactory> CreateAsync(
        string adminConnectionString,
        CancellationToken cancellationToken,
        bool useProbeAuthentication = true,
        TimeProvider? clock = null,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        var database = await TestDatabase.CreateAsync(adminConnectionString, cancellationToken);

        return new ApiFactory(database, useProbeAuthentication, clock, configureServices, settings);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The Web project directory, so the host resolves its static asset manifest and its
        // appsettings.json exactly as the container does.
        builder.UseContentRoot(RepositoryLayout.ProjectDirectory("DriveTrack.Web"));

        // AD-19: the connection string, the JWT settings and the first administrator all arrive as
        // configuration, here as they do in compose. The signing key is not optional - startup
        // aborts without one - so every host in this suite supplies it, and the failing case is
        // asserted deliberately in InfrastructureRegistrationTests.
        builder.UseSetting("ConnectionStrings:Default", _database.ConnectionString);

        foreach (var setting in TestConfiguration.Defaults())
        {
            builder.UseSetting(setting.Key, setting.Value);
        }

        // After the defaults, so a suite's own key wins over one of theirs. Leaving a key out of
        // Defaults() and setting it here is what keeps "nothing configured" an honest case: the
        // default-behaviour test boots with no entry at all, exactly as a container with no such
        // line in its environment block does.
        if (_settings is not null)
        {
            foreach (var setting in _settings)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }

        builder.ConfigureTestServices(services =>
        {
            if (_clock is not null)
            {
                // Added, not TryAdded: AddInfrastructure registers the system clock with TryAdd, so
                // the only way to replace it from here is to register last and win the resolve.
                services.AddSingleton(_clock);
            }

            // The probe controllers live in this assembly; adding it as an application part is what
            // lets the production pipeline route to them without DriveTrack.Web shipping one.
            services.AddControllers()
                .ConfigureApplicationPartManager(manager =>
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(ProbeController).Assembly)));

            // The probe policy is always registered: the probe controller declares it, and an
            // endpoint referring to a policy the container does not hold fails at request time even
            // when no test calls it.
            services.AddAuthorizationBuilder()
                .AddPolicy(
                    ProbeAuthentication.PolicyName,
                    policy => policy.RequireClaim(
                        ProbeAuthentication.ClaimType,
                        ProbeAuthentication.ClaimValue));

            if (_useProbeAuthentication)
            {
                // Registered last, so this scheme wins the default and [Authorize] has something to
                // challenge with. The production schemes are still registered underneath; this only
                // changes which one an endpoint with no explicit scheme resolves to.
                services.AddAuthentication(ProbeAuthentication.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, ProbeAuthenticationHandler>(
                        ProbeAuthentication.SchemeName,
                        _ => { });
            }

            // Last of all, so a suite can replace anything above it rather than only add to it.
            _configureServices?.Invoke(services);
        });
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // After the host, so nothing is still holding a connection when the database is dropped.
        await _database.DisposeAsync();
    }
}
