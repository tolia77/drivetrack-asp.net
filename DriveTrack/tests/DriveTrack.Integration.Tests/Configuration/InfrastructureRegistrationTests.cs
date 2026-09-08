using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Users;
using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Identity;
using DriveTrack.Infrastructure.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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
                new TestHostEnvironment { EnvironmentName = Environments.Production }));

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
            new TestHostEnvironment { EnvironmentName = environmentName });

        var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
    }


    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short")]
    public void Missing_blank_or_short_signing_key_throws_naming_the_variable(string? signingKey)
    {
        // AD-19: a signing key with a committed fallback is a signing key everyone knows, so the
        // absence has to abort startup rather than surface at the first sign-in.
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(
                BuildConfiguration(ValidConnectionString, signingKey),
                new TestHostEnvironment()));

        Assert.Contains("Jwt:SigningKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Jwt__SigningKey", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sixty")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void A_lifetime_that_is_not_a_positive_whole_number_throws_naming_the_variable(string lifetime)
    {
        // An .env predating this key forwards the empty string, which does not fall back to the
        // option's default - it fails the binder with an opaque message at the first request. Caught
        // here instead, next to the signing key, and named the way the environment spells it.
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(ValidConnectionString);
        configuration["Jwt:LifetimeMinutes"] = lifetime;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(configuration, new TestHostEnvironment()));

        Assert.Contains("Jwt:LifetimeMinutes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Jwt__LifetimeMinutes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_lifetime_takes_the_default_rather_than_failing()
    {
        // Absent is not the same as blank: the option has a default, and a deployment that never set
        // the variable must keep working.
        var services = new ServiceCollection();
        services.AddLogging();

        // Genuinely absent, not set to null: nulling a key through the indexer leaves it present
        // with an empty value, which is the case the theory above covers.
        var values = TestConfiguration.Defaults();
        values["ConnectionStrings:Default"] = ValidConnectionString;
        values.Remove(JwtOptions.LifetimeMinutesConfigurationKey);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        services.AddInfrastructure(configuration, new TestHostEnvironment());

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<JwtOptions>>()
            .Value;

        Assert.True(options.LifetimeMinutes > 0);
    }

    [Fact]
    public void Identity_cannot_be_reached_outside_a_unit_of_work()
    {
        // AD-5, expressed as an absence. AddEntityFrameworkStores registers stores over a scoped
        // AppDbContext that AddInfrastructure has already deleted, and a resolvable UserManager
        // would be a second, non-transactional way to write a user. Removing the registrations turns
        // "never use UserManager directly" into a rule the container cannot satisfy.
        var provider = BuildProvider();

        Assert.Null(provider.GetService<AppDbContext>());
        Assert.Null(provider.GetService<IUserStore<ApplicationUser>>());
        Assert.Null(provider.GetService<IRoleStore<IdentityRole<int>>>());
        Assert.Null(provider.GetService<UserManager<ApplicationUser>>());
        Assert.Null(provider.GetService<RoleManager<IdentityRole<int>>>());
    }

    [Fact]
    public void Identitys_own_services_are_still_registered()
    {
        // The other half: removing the stores must not remove the hasher, the normalizer or the
        // options, because the per-scope managers are built from exactly those.
        using var scope = BuildProvider().CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetService<IPasswordHasher<ApplicationUser>>());
        Assert.NotNull(services.GetService<ILookupNormalizer>());
        Assert.NotNull(services.GetService<IdentityErrorDescriber>());
        Assert.NotEmpty(services.GetServices<IUserValidator<ApplicationUser>>());
        Assert.NotEmpty(services.GetServices<IPasswordValidator<ApplicationUser>>());
    }

    [Fact]
    public void The_guard_the_account_capability_and_the_token_issuer_are_registered()
    {
        using var scope = BuildProvider().CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetService<IAccessTokenIssuer>());
        Assert.NotNull(services.GetService<ScopedIdentityFactory>());
        Assert.NotNull(services.GetService<IdentitySeeder>());

        // The guard and the account capability are asserted as registrations rather than resolved:
        // both need ICurrentUser, which only an adapter can supply (AD-22), so a hostless provider
        // has nothing to give them.
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddInfrastructure(BuildConfiguration(ValidConnectionString), new TestHostEnvironment());

        Assert.Contains(
            registrations,
            descriptor => descriptor.ServiceType == typeof(IAccessGuard)
                && descriptor.ImplementationType == typeof(AccessGuard));
        Assert.Contains(registrations, descriptor => descriptor.ServiceType == typeof(IUserService));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(BuildConfiguration(ValidConnectionString));
        services.AddInfrastructure(BuildConfiguration(ValidConnectionString), new TestHostEnvironment());

        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfiguration(
        string? connectionString,
        string? signingKey = TestConfiguration.JwtSigningKey)
    {
        var values = TestConfiguration.Defaults();
        values["ConnectionStrings:Default"] = connectionString;
        values["Jwt:SigningKey"] = signingKey;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
