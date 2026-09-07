using DriveTrack.Application.Abstractions;
using DriveTrack.Infrastructure.Persistence;
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
    /// scope (AD-5), the clock (AD-13) and the <see cref="DatabaseMigrator"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The connection string is absent or blank. Failing here aborts startup before the host
    /// runs, rather than deferring the failure to the first request.
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

        return services;
    }
}
