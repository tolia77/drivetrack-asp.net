using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Chat;
using DriveTrack.Application.Deliveries;
using DriveTrack.Application.Drivers;
using DriveTrack.Application.Notifications;
using DriveTrack.Application.Reviews;
using DriveTrack.Application.Shifts;
using DriveTrack.Application.Users;
using DriveTrack.Application.Vehicles;
using DriveTrack.Infrastructure.Email;
using DriveTrack.Infrastructure.Geocoding;
using DriveTrack.Infrastructure.Identity;
using DriveTrack.Infrastructure.Objects;
using DriveTrack.Infrastructure.Persistence;
using DriveTrack.Infrastructure.SideEffects;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveTrack.Infrastructure;

/// <summary>
/// Infrastructure's composition surface. AD-1 permits exactly one caller —
/// <c>DriveTrack.Web/Program.cs</c> — so every Infrastructure detail stays behind this method.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Configuration key holding the PostgreSQL connection string. Supplied as the
    /// <c>ConnectionStrings__Default</c> environment variable (AD-19); never committed.
    /// </summary>
    public const string ConnectionStringKey = "ConnectionStrings:Default";

    /// <summary>
    /// Registers the pooled <see cref="AppDbContext"/> factory, the per-operation persistence
    /// scope (AD-5), ASP.NET Core Identity's services without its stores or managers, the access
    /// guard, the account capability, the token issuer, the clock (AD-13) and the
    /// <see cref="DatabaseMigrator"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The connection string or the JWT signing key is absent, blank or too short. Failing here
    /// aborts startup before the host runs, rather than deferring the failure to the first request —
    /// and a signing key is exactly the kind of value whose absence must not be discovered by the
    /// first caller who tries to sign in.
    /// </exception>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var connectionString = configuration[ConnectionStringKey];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConnectionStringKey}' is missing or blank. " +
                "Set the 'ConnectionStrings__Default' environment variable " +
                "(see .env.example) before starting the application.");
        }

        // NFR-11 / AD-19: query and parameter diagnostics only in Development.
        var isDevelopment = environment.IsDevelopment();

        services.AddPooledDbContextFactory<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.EnableSensitiveDataLogging(isDevelopment);
            options.EnableDetailedErrors(isDevelopment);
        });

        // AddPooledDbContextFactory also registers the context itself, scoped, as a
        // convenience. AD-5 forbids exactly that registration: on a Blazor Server circuit a
        // scoped context lives for hours, accumulates tracked entities, serves stale reads and
        // throws on concurrent renders. Removing the descriptor turns "do not inject a context"
        // from a rule everyone has to remember into one the container cannot satisfy.
        services.RemoveAll<AppDbContext>();

        services.AddSingleton<DatabaseMigrator>();

        // AD-5: the only way to reach a context is to open a scope.
        services.AddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();

        // AD-13: time is injected so a time-dependent rule can be tested. DateTime.UtcNow and
        // database default timestamps appear nowhere. TryAdd, not Add: a caller that has
        // already registered a fake clock keeps it, where an unconditional Add would win by
        // being last and quietly hand every test the real one back.
        services.TryAddSingleton(TimeProvider.System);

        AddIdentity(services);
        AddAuthorizationAndAccounts(services, configuration);
        AddSideEffects(services, configuration);
        AddObjectStore(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers Identity's <em>services</em> — the password hasher, the lookup normalizer, the user
    /// and password validators, the error describer and <c>IdentityOptions</c> — and then deletes
    /// every registration through which those services could be used outside a unit of work.
    /// <para>
    /// <c>AddIdentityCore</c> rather than <c>AddIdentity</c>: the latter registers three
    /// authentication schemes and makes its own cookie the default, which would silently take over
    /// the scheme selection the composition root sets up by path.
    /// </para>
    /// <para>
    /// The four <c>RemoveAll</c> calls are the same move as <c>RemoveAll&lt;AppDbContext&gt;()</c>
    /// above. <c>AddEntityFrameworkStores</c> registers stores over a scoped context that no longer
    /// exists, and a resolvable <c>UserManager</c> would be a second, non-transactional way to write
    /// a user — the exact trap AD-5 exists to close. Removing them turns "never use UserManager
    /// directly" from a rule people remember into one the container cannot satisfy.
    /// </para>
    /// </summary>
    private static void AddIdentity(IServiceCollection services)
    {
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                // AD-4 / FR-1: one account per address, checked by Identity and backed by the
                // normalized-email unique index in the schema.
                options.User.RequireUniqueEmail = true;

                // FR-8's policy, stated rather than inherited, because a validator message keyed
                // AUTH_PASSWORD_TOO_WEAK is only honest if the threshold is written down.
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
            })
            .AddRoles<IdentityRole<int>>()
            .AddEntityFrameworkStores<AppDbContext>();

        services.RemoveAll<IUserStore<ApplicationUser>>();
        services.RemoveAll<IRoleStore<IdentityRole<int>>>();
        services.RemoveAll<UserManager<ApplicationUser>>();
        services.RemoveAll<RoleManager<IdentityRole<int>>>();

        // And the one registration that depends on the manager: Identity's claims-principal factory.
        // Nothing here uses it - ClaimsFactory writes the cookie's claims and JwtAccessTokenIssuer
        // the token's, from the same names - and leaving it behind makes the container fail its own
        // scope validation at startup in Development, which is where it would be found last.
        services.RemoveAll<IUserClaimsPrincipalFactory<ApplicationUser>>();

        // The one remaining way to reach Identity: per unit of work, over that scope's context,
        // with AutoSaveChanges off.
        services.AddSingleton<ScopedIdentityFactory>();
        services.AddSingleton<IdentitySeeder>();
    }

    private static void AddAuthorizationAndAccounts(IServiceCollection services, IConfiguration configuration)
    {
        var signingKey = configuration[JwtOptions.SigningKeyConfigurationKey];

        if (string.IsNullOrWhiteSpace(signingKey)
            || System.Text.Encoding.UTF8.GetByteCount(signingKey) < JwtOptions.MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"Configuration value '{JwtOptions.SigningKeyConfigurationKey}' is missing, blank or "
                    + $"shorter than {JwtOptions.MinimumSigningKeyBytes} bytes. Set the "
                    + "'Jwt__SigningKey' environment variable (see .env.example) to at least "
                    + "32 bytes of random material before starting the application.");
        }

        // Present but unusable is the case worth catching: an .env predating these keys sets the
        // value to the empty string, which does not fall back to the option's default - it fails the
        // binder with an opaque message at the first request instead.
        var lifetime = configuration[JwtOptions.LifetimeMinutesConfigurationKey];

        if (lifetime is not null
            && (!int.TryParse(lifetime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                || minutes <= 0))
        {
            throw new InvalidOperationException(
                $"Configuration value '{JwtOptions.LifetimeMinutesConfigurationKey}' is '{lifetime}', "
                    + "which is not a positive whole number of minutes. Set the "
                    + "'Jwt__LifetimeMinutes' environment variable (see .env.example), or remove it "
                    + "to take the default.");
        }

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();

        // AD-2: the guard reads the caller, and the caller is per request, so both are scoped.
        // ICurrentUser itself is registered by the adapter that can actually read a caller.
        services.AddScoped<IAccessGuard, AccessGuard>();
        services.AddScoped<IUserService, UserService>();

        // FR-46…FR-50: the two account-administration capabilities. Scoped for the same reason as
        // the guard - each reads the caller of the request or circuit it is serving.
        services.AddScoped<IClientAdministrationService, ClientAdministrationService>();
        services.AddScoped<IDispatcherAdministrationService, DispatcherAdministrationService>();
        // The fleet capabilities. Infrastructure is the one composition surface AD-1 permits, so a
        // capability that lives in Application is still registered here.
        services.AddScoped<IVehicleService, VehicleService>();
        services.AddScoped<IDriverService, DriverService>();

        // The product's central record (FR-14 to FR-27). Scoped like the rest: it reads the caller
        // of the request or circuit it is serving, and it takes the clock registered above so
        // FR-19's overdue rule is a function of an injected TimeProvider rather than of the machine.
        services.AddScoped<IDeliveryService, DeliveryService>();

        // FR-28's read side. Scoped for the same reason as the rest: the guard inside it reads the
        // caller of the request or circuit it is serving.
        services.AddScoped<INotificationLogService, NotificationLogService>();

        // FR-119 to FR-123. Scoped like every other capability - it reads the caller of the request
        // or circuit it is serving - even though the port it calls is a singleton below.
        services.AddScoped<IProofOfDeliveryService, ProofOfDeliveryService>();

        // FR-68 to FR-76. Scoped like every other capability - it reads the caller of the request,
        // the circuit or the hub invocation it is serving - and registered here because AD-1 makes
        // Infrastructure the one composition surface, even for a capability that lives in
        // Application.
        services.AddScoped<IChatService, ChatService>();

        // FR-62 to FR-67 and FR-98. Scoped like every other capability - it reads the caller of the
        // request or circuit it is serving - and registered here because AD-1 makes Infrastructure
        // the one composition surface, even for a capability that lives in Application.
        //
        // AD-24 shows up as a shape in the container: this one takes IDeliveryService and the
        // driver capability takes this one, so the two directions are declared rather than
        // discovered. Neither closes a cycle, because the Deliveries capability reads neither.
        services.AddScoped<IReviewService, ReviewService>();

        // FR-109 to FR-117. Scoped like every other capability - it reads the caller of the request
        // or circuit it is serving - and it takes the clock registered above, so going on and off
        // duty is stamped from an injected TimeProvider rather than from the machine (AD-13).
        //
        // AD-24 shows up in the container here too: the driver capability above takes this one for
        // FR-116's flag, and this one takes neither it nor the deliveries capability. The edge runs
        // one way only, which is what makes the pair resolvable at all.
        services.AddScoped<IShiftService, ShiftService>();
    }

    /// <summary>
    /// Registers AD-26's object-storage port: the settings, the eager check on them, and the Garage
    /// adapter behind <see cref="IAssetStore"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <c>ObjectStore:ServiceUrl</c> is present and is not an absolute URL — the empty string an
    /// <c>.env</c> predating this key forwards included — or an endpoint is configured and the
    /// bucket or either half of the key pair is blank.
    /// <para>
    /// Caught here rather than at the first capture, and the reason is where the failure would
    /// otherwise surface. This option has no usable default to fall back on: with no endpoint the
    /// SDK resolves an Amazon one, so a blank or misspelled value does not disable the store, it
    /// points it at a service nobody chose — and a blank bucket or key fails inside the SDK, at a
    /// door, in a message naming no DriveTrack setting at all.
    /// </para>
    /// </exception>
    private static void AddObjectStore(IServiceCollection services, IConfiguration configuration)
    {
        RequireAbsoluteUrl(configuration, ObjectStoreOptions.ServiceUrlConfigurationKey);

        // Only once there is an endpoint. With none the store is deliberately unconfigured and
        // GarageAssetStore says so by name at the first capture; demanding a bucket and a key pair
        // from a deployment that has no object store would refuse to start a system that works.
        if (!string.IsNullOrWhiteSpace(configuration[ObjectStoreOptions.ServiceUrlConfigurationKey]))
        {
            RequireValue(configuration, ObjectStoreOptions.BucketConfigurationKey, "bucket name");
            RequireValue(configuration, ObjectStoreOptions.AccessKeyConfigurationKey, "access key id");
            RequireValue(configuration, ObjectStoreOptions.SecretKeyConfigurationKey, "secret access key");
        }

        services.Configure<ObjectStoreOptions>(
            configuration.GetSection(ObjectStoreOptions.SectionName));

        // A singleton, like the two outbound ports above it. AmazonS3Client is safe for concurrent
        // use and holds a connection pool; one per request would exhaust sockets under exactly the
        // load a fleet of drivers uploading photographs produces.
        services.AddSingleton<IAssetStore, GarageAssetStore>();
    }

    /// <summary>
    /// Registers AD-12's outbound ports and the queue that runs them after a commit: the geocoder
    /// (FR-94, FR-104), the mail transport (FR-28), the job runner, the queue behind
    /// <see cref="IDeliverySideEffects"/> and the one background loop that drains it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A key in either section is present but unusable. An <c>.env</c> predating these keys forwards
    /// the empty string, which does not fall back to an option's default — it fails the binder with
    /// an opaque message at the first delivery somebody creates, which is both far from the cause
    /// and inside a background job where nobody is watching. Caught here instead, and named the way
    /// the environment spells it.
    /// </exception>
    private static void AddSideEffects(IServiceCollection services, IConfiguration configuration)
    {
        RequirePositiveInteger(configuration, GeocoderOptions.TimeoutSecondsConfigurationKey, "seconds");
        RequireNonNegativeInteger(
            configuration,
            GeocoderOptions.MinimumRequestIntervalMillisecondsConfigurationKey,
            "milliseconds");
        RequirePositiveInteger(configuration, SmtpOptions.PortConfigurationKey, "port number");
        RequireBoolean(configuration, SmtpOptions.UseStartTlsConfigurationKey);

        services.Configure<GeocoderOptions>(configuration.GetSection(GeocoderOptions.SectionName));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));

        // Read eagerly, and only after the checks above, because Get<T> runs the same binder they
        // exist to keep away from an unusable value. The timeout has to be known here rather than
        // per request: HttpClient.Timeout is set once, on the instance.
        var geocoder = configuration.GetSection(GeocoderOptions.SectionName).Get<GeocoderOptions>()
            ?? new GeocoderOptions();

        // One client for the process (NFR-8's other half): a client per lookup exhausts the socket
        // pool under load, and this one holds no per-request state - the agent header is set on the
        // request, beside the value it was read from.
        //
        // Constructed for the geocoder rather than registered as a service. `AddSingleton<HttpClient>`
        // would put a bare client in the container for every consumer, and the next thing to ask for
        // one would silently inherit a timeout tuned for a geocoding lookup - a dependency nothing
        // declares and nobody would look for.
        services.AddSingleton<IGeocoder>(provider => new NominatimGeocoder(
            new HttpClient { Timeout = TimeSpan.FromSeconds(geocoder.TimeoutSeconds) },
            provider.GetRequiredService<IOptions<GeocoderOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<NominatimGeocoder>>()));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        // Singletons, like the factory and the clock they are built from. A side effect belongs to
        // no request and to no circuit - that is the whole of AD-12 - so there is no scope for one
        // to live in, and the runner opens its own unit of work per job.
        services.AddSingleton<IDeliverySideEffectRunner, DeliverySideEffectRunner>();

        // The queue is registered as itself and then as the interface, resolving to the same
        // instance. Two registrations of the implementation type would be two queues: the delivery
        // service would fill one and the worker would drain the other, and nothing would ever fail
        // loudly enough to say so.
        services.AddSingleton<DeliverySideEffectQueue>();
        services.AddSingleton<IDeliverySideEffects>(provider =>
            provider.GetRequiredService<DeliverySideEffectQueue>());

        services.AddHostedService<DeliverySideEffectWorker>();
    }

    /// <summary>
    /// Refuses a configuration value that is present but is not a positive whole number, naming the
    /// key in both the spelling the code uses and the spelling the environment does.
    /// </summary>
    /// <remarks>
    /// Absent is not the same as blank, and only blank is refused: a deployment that never set the
    /// variable takes the option's default and must keep working.
    /// </remarks>
    private static void RequirePositiveInteger(
        IConfiguration configuration,
        string key,
        string unit)
    {
        var value = configuration[key];

        if (value is null)
        {
            return;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' is '{value}', which is not a positive whole number of "
                    + $"{unit}. Set the '{EnvironmentSpelling(key)}' environment variable "
                    + "(see .env.example), or remove it to take the default.");
        }
    }

    /// <summary>
    /// Refuses a configuration value that is present but is not a whole number of zero or more,
    /// naming the key in both the spelling the code uses and the spelling the environment does.
    /// </summary>
    /// <remarks>
    /// Zero is a setting rather than an absence for the value this guards: it turns the geocoder's
    /// request pacing off, which is what a self-hosted provider with no usage policy wants. Blank is
    /// still refused, for the reason <see cref="RequirePositiveInteger"/> refuses it — an
    /// <c>.env</c> predating the key forwards the empty string, and an empty string overrides the
    /// option's default rather than falling back to it.
    /// </remarks>
    private static void RequireNonNegativeInteger(
        IConfiguration configuration,
        string key,
        string unit)
    {
        var value = configuration[key];

        if (value is null)
        {
            return;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' is '{value}', which is not a whole number of {unit} "
                    + $"of zero or more. Set the '{EnvironmentSpelling(key)}' environment variable "
                    + "(see .env.example) to 0 to disable pacing, or remove it to take the default.");
        }
    }

    /// <summary>
    /// Refuses a configuration value that is absent or blank, naming the key in both the spelling
    /// the code uses and the spelling the environment does.
    /// <para>
    /// Unlike its siblings above, absent is refused here as well as blank. They guard values that
    /// have a working default; this one guards values that have none, and where the consequence of
    /// carrying on is a failure inside a third-party SDK at the first capture somebody makes.
    /// </para>
    /// </summary>
    private static void RequireValue(IConfiguration configuration, string key, string what)
    {
        if (string.IsNullOrWhiteSpace(configuration[key]))
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' is missing or blank, and an object store endpoint is "
                    + $"configured. Set the '{EnvironmentSpelling(key)}' environment variable "
                    + $"(see .env.example) to the {what} the store was provisioned with.");
        }
    }

    /// <inheritdoc cref="RequirePositiveInteger" />
    private static void RequireAbsoluteUrl(IConfiguration configuration, string key)
    {
        var value = configuration[key];

        if (value is null)
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' is '{value}', which is not an absolute http or https "
                    + $"URL. Set the '{EnvironmentSpelling(key)}' environment variable "
                    + "(see .env.example), or remove it to leave the object store unconfigured.");
        }
    }

    /// <inheritdoc cref="RequirePositiveInteger" />
    private static void RequireBoolean(IConfiguration configuration, string key)
    {
        var value = configuration[key];

        if (value is null)
        {
            return;
        }

        if (!bool.TryParse(value, out _))
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' is '{value}', which is not true or false. Set the "
                    + $"'{EnvironmentSpelling(key)}' environment variable (see .env.example), or "
                    + "remove it to take the default.");
        }
    }

    /// <summary>The key as an environment variable spells it, which is what an operator will search for.</summary>
    private static string EnvironmentSpelling(string key) =>
        key.Replace(":", "__", StringComparison.Ordinal);
}
