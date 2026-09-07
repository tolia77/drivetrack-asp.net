using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DriveTrack.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> when scaffolding and scripting migrations.
/// <para>
/// AD-20 requires migrations to be generated from the same model the application runs, so
/// the tooling builds the context directly here rather than booting the web host — that keeps
/// migration generation independent of a running database and of the runtime environment.
/// The connection string is only used to pick the provider; <c>migrations add</c> never
/// opens a connection. A real value can still be supplied through
/// <c>ConnectionStrings__Default</c> for commands that do connect.
/// </para>
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string DesignTimeFallbackConnectionString =
        "Host=localhost;Port=5432;Database=drivetrack;Username=drivetrack;Password=design-time";

    /// <inheritdoc />
    public AppDbContext CreateDbContext(string[] args)
    {
        // Checked for whitespace, not just null: an exported-but-empty variable is the normal
        // shape on Linux, and passing "" to UseNpgsql throws instead of falling back.
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__Default");

        var connectionString = string.IsNullOrWhiteSpace(configured)
            ? DesignTimeFallbackConnectionString
            : configured;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}
