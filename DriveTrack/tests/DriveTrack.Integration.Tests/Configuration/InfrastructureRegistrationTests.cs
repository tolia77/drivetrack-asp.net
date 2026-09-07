using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DriveTrack.Integration.Tests.Configuration;

/// <summary>
/// AD-19 / NFR-10 / NFR-11 at the registration boundary: configuration arrives from the
/// environment, a missing connection string aborts startup, and EF diagnostics are on only
/// in Development. None of these need a database, so they run without a container.
/// </summary>
public class InfrastructureRegistrationTests
{
    private const string ValidConnectionString =
        "Host=localhost;Port=5432;Database=drivetrack;Username=drivetrack;Password=irrelevant";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_connection_string_throws_naming_the_variable(string? value)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(
                BuildConfiguration(value),
                new TestHostEnvironment(Environments.Production)));

        Assert.Contains("ConnectionStrings:Default", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__Default", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_environment_enables_sensitive_data_logging_and_detailed_errors()
    {
        using var context = CreateContext(Environments.Development);

        var options = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>();

        Assert.NotNull(options);
        Assert.True(options.IsSensitiveDataLoggingEnabled);
        Assert.True(options.DetailedErrorsEnabled);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Coursework")]
    public void Non_development_environment_disables_sensitive_data_logging_and_detailed_errors(
        string environmentName)
    {
        using var context = CreateContext(environmentName);

        var options = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>();

        Assert.NotNull(options);
        Assert.False(options.IsSensitiveDataLoggingEnabled);
        Assert.False(options.DetailedErrorsEnabled);
    }

    private static AppDbContext CreateContext(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(
            BuildConfiguration(ValidConnectionString),
            new TestHostEnvironment(environmentName));

        var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
    }

    private static IConfiguration BuildConfiguration(string? connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
            })
            .Build();

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "DriveTrack.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
