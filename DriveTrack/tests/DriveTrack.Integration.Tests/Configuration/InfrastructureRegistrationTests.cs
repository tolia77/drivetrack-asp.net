using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Deliveries;
using DriveTrack.Application.Notifications;
using DriveTrack.Application.Users;
using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Email;
using DriveTrack.Infrastructure.Geocoding;
using DriveTrack.Infrastructure.Identity;
using DriveTrack.Infrastructure.Objects;
using DriveTrack.Infrastructure.Persistence;
using DriveTrack.Infrastructure.SideEffects;
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

    [Fact]
    public void The_two_outbound_ports_and_the_queue_that_runs_them_are_registered()
    {
        // AD-12, as a registration. The seam is only a seam if all four pieces are in the container:
        // the two ports, the queue a delivery operation writes to, and the hosted loop that drains
        // it. Three out of four is a system that queues work nothing ever performs, and nothing
        // fails loudly enough to say so - the delivery write succeeds either way, which is precisely
        // the guarantee that makes the omission invisible.
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddInfrastructure(BuildConfiguration(ValidConnectionString), new TestHostEnvironment());

        Assert.Contains(registrations, descriptor => descriptor.ServiceType == typeof(IGeocoder));
        Assert.Contains(registrations, descriptor => descriptor.ServiceType == typeof(IEmailSender));
        Assert.Contains(
            registrations,
            descriptor => descriptor.ServiceType == typeof(IDeliverySideEffectRunner));
        Assert.Contains(
            registrations,
            descriptor => descriptor.ServiceType == typeof(IDeliverySideEffects));
        Assert.Contains(
            registrations,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(DeliverySideEffectWorker));
        Assert.Contains(
            registrations,
            descriptor => descriptor.ServiceType == typeof(INotificationLogService));
    }

    [Fact]
    public void The_queue_the_worker_drains_is_the_queue_the_delivery_service_fills()
    {
        // Two AddSingleton<DeliverySideEffectQueue>-shaped registrations would be two queues: the
        // delivery service would write to one and the worker would read the other, every side effect
        // would be dropped, and nothing would fail - the write the caller waited for succeeded, and
        // the work that did not happen was never allowed to matter to them. So the identity is
        // asserted rather than assumed.
        var provider = BuildProvider();

        Assert.Same(
            provider.GetRequiredService<DeliverySideEffectQueue>(),
            provider.GetRequiredService<IDeliverySideEffects>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ten")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void A_geocoder_timeout_that_is_not_a_positive_whole_number_throws_naming_the_variable(
        string timeout)
    {
        // The same trap as Jwt__LifetimeMinutes, one section over: an .env predating this key
        // forwards the empty string, and an empty string is a value - it overrides the option's
        // default rather than falling back to it, and fails the binder with an opaque message. Here
        // that message would surface inside a background job, which is the one place nobody is
        // watching, so the failure is brought forward to startup.
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(ValidConnectionString);
        configuration[GeocoderOptions.TimeoutSecondsConfigurationKey] = timeout;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(configuration, new TestHostEnvironment()));

        Assert.Contains("Geocoder:TimeoutSeconds", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Geocoder__TimeoutSeconds", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("smtp")]
    [InlineData("0")]
    public void An_unusable_mail_port_throws_naming_the_variable(string port)
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(ValidConnectionString);
        configuration[SmtpOptions.PortConfigurationKey] = port;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(configuration, new TestHostEnvironment()));

        Assert.Contains("Smtp:Port", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Smtp__Port", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    public void An_unusable_tls_switch_throws_naming_the_variable(string useStartTls)
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(ValidConnectionString);
        configuration[SmtpOptions.UseStartTlsConfigurationKey] = useStartTls;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(configuration, new TestHostEnvironment()));

        Assert.Contains("Smtp:UseStartTls", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Smtp__UseStartTls", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unconfigured_relay_refuses_the_send_rather_than_reporting_success()
    {
        // The real transport, resolved from the real registration - every other suite replaces it,
        // so this is the only place it runs at all.
        //
        // The claim is narrow and load-bearing: with no relay configured nothing was sent, and the
        // transport has to say so. Returning quietly would have the runner write Outcome.Sent for a
        // notice that never left the building, which is worse than the silence FR-28 was written to
        // end - a record that is confidently wrong.
        var cancellationToken = TestContext.Current.CancellationToken;

        var sender = BuildProvider().GetRequiredService<IEmailSender>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync(
                new EmailMessage("olena@drivetrack.test", "Тема", "Текст"),
                cancellationToken));

        // Named the way an operator would search for it, like every other configuration failure here.
        Assert.Contains("Smtp__Host", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("objects:3900")]
    [InlineData("ftp://objects:3900")]
    public void An_object_store_endpoint_that_is_not_an_absolute_url_throws_naming_the_variable(
        string serviceUrl)
    {
        // The same trap as Jwt__LifetimeMinutes and Geocoder__TimeoutSeconds, one section over: an
        // .env predating this key forwards the empty string, and an empty string is a value - it
        // overrides the option's default rather than falling back to it. Here the consequence is
        // worse than an opaque binder message: with no endpoint the SDK resolves an Amazon one, so
        // a typo in the scheme would have a deployment signing requests to a service nobody chose.
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(ValidConnectionString);
        configuration[ObjectStoreOptions.ServiceUrlConfigurationKey] = serviceUrl;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(configuration, new TestHostEnvironment()));

        Assert.Contains("ObjectStore:ServiceUrl", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ObjectStore__ServiceUrl", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ObjectStoreOptions.BucketConfigurationKey, null)]
    [InlineData(ObjectStoreOptions.BucketConfigurationKey, "   ")]
    [InlineData(ObjectStoreOptions.AccessKeyConfigurationKey, null)]
    [InlineData(ObjectStoreOptions.AccessKeyConfigurationKey, "   ")]
    [InlineData(ObjectStoreOptions.SecretKeyConfigurationKey, null)]
    [InlineData(ObjectStoreOptions.SecretKeyConfigurationKey, "   ")]
    public void An_object_store_with_an_endpoint_but_no_bucket_or_key_throws_naming_the_variable(
        string key,
        string? value)
    {
        // The half the endpoint theory above cannot reach: a well-formed ServiceUrl passes
        // RequireAbsoluteUrl and the other three are never looked at again until the SDK needs them.
        // Without this the deployment starts cleanly and the first driver to capture a proof at a
        // door is refused by a message from inside AWSSDK.S3 that names no DriveTrack setting at
        // all - which is the one place a missing .env line must never be discovered.
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(ValidConnectionString);
        configuration[ObjectStoreOptions.ServiceUrlConfigurationKey] = "http://objects:3900";

        // A fully configured store first, so the one key this case is about is the only thing wrong
        // with it. Without this the bucket is blank in every case and the bucket's own refusal is
        // the one that answers, whichever key the theory meant to blank.
        configuration[ObjectStoreOptions.BucketConfigurationKey] = "drivetrack-proofs";
        configuration[ObjectStoreOptions.AccessKeyConfigurationKey] = "GK0123456789abcdef01234567";
        configuration[ObjectStoreOptions.SecretKeyConfigurationKey] = new string('0', 64);

        configuration[key] = value;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(configuration, new TestHostEnvironment()));

        // Both spellings, as everywhere else here: the one the code uses and the one the operator
        // edits.
        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            key.Replace(":", "__", StringComparison.Ordinal),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_object_store_port_and_the_proof_capability_are_registered()
    {
        // AD-26 as a registration. The port is a singleton because the S3 client holds a connection
        // pool and one per request exhausts sockets under exactly the load a fleet of drivers
        // uploading photographs produces; the capability is scoped because the guard inside it reads
        // the caller of the request or circuit it is serving.
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddInfrastructure(BuildConfiguration(ValidConnectionString), new TestHostEnvironment());

        Assert.Contains(
            registrations,
            descriptor => descriptor.ServiceType == typeof(IAssetStore)
                && descriptor.Lifetime == ServiceLifetime.Singleton);

        Assert.Contains(
            registrations,
            descriptor => descriptor.ServiceType == typeof(IProofOfDeliveryService)
                && descriptor.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void An_object_store_region_defaults_to_the_one_Garage_signs_for()
    {
        // Not a preference: SigV4 signs over the region, so a client that fell back to an AWS
        // default against a Garage node would be refused with a message mentioning no region at all.
        var options = BuildProvider().GetRequiredService<IOptions<ObjectStoreOptions>>().Value;

        Assert.Equal("garage", options.Region);

        // And the endpoint has no default, for the reason the geocoder's endpoints have none.
        Assert.Equal(string.Empty, options.ServiceUrl);
    }

    [Fact]
    public void An_unconfigured_geocoder_starts_and_asks_nobody_anything()
    {
        // Neither endpoint has a committed default, and this is the reason: a default would make
        // every environment that never configured one - a test host, a laptop, a marking run -
        // issue live requests to somebody else's public service the first time a delivery was
        // created. Blank is a state DR-11 already has an answer for.
        var provider = BuildProvider();

        var options = provider.GetRequiredService<IOptions<GeocoderOptions>>().Value;

        Assert.Equal(string.Empty, options.Endpoint);
        Assert.Equal(string.Empty, options.SearchEndpoint);
        Assert.True(options.TimeoutSeconds > 0);
        Assert.False(string.IsNullOrWhiteSpace(options.UserAgent));
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
