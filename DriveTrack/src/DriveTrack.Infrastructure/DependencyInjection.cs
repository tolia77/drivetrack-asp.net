using DriveTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    /// Registers the pooled <see cref="AppDbContext"/> factory and the
    /// <see cref="DatabaseMigrator"/>.
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

        services.AddSingleton<DatabaseMigrator>();

        return services;
    }
}
