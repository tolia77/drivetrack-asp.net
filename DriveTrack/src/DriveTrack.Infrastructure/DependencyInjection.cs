using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Users;
using DriveTrack.Infrastructure.Identity;
using DriveTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
    }
}
