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

    private ApiFactory(TestDatabase database) => _database = database;

    /// <summary>
    /// The database this host runs on, so a test can seed the rows an endpoint will then violate.
    /// The same instance the pipeline uses - seeding a second one would prove nothing.
    /// </summary>
    public TestDatabase Database => _database;

    /// <summary>
    /// Creates the host's database first, because <c>Program.cs</c> migrates at start-up (AD-20) and
    /// would abort against a database that does not exist.
    /// </summary>
    public static async Task<ApiFactory> CreateAsync(
        string adminConnectionString,
        CancellationToken cancellationToken)
    {
        var database = await TestDatabase.CreateAsync(adminConnectionString, cancellationToken);

        return new ApiFactory(database);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The Web project directory, so the host resolves its static asset manifest and its
        // appsettings.json exactly as the container does.
        builder.UseContentRoot(RepositoryLayout.ProjectDirectory("DriveTrack.Web"));

        // AD-19: the connection string arrives as configuration, here as it does in compose.
        builder.UseSetting("ConnectionStrings:Default", _database.ConnectionString);

        builder.ConfigureTestServices(services =>
        {
            // The probe controllers live in this assembly; adding it as an application part is what
            // lets the production pipeline route to them without DriveTrack.Web shipping one.
            services.AddControllers()
                .ConfigureApplicationPartManager(manager =>
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(ProbeController).Assembly)));

            // Registered last, so this scheme wins the default and [Authorize] has something to
            // challenge with. Epic 2 replaces it with the cookie and JWT schemes, which attach the
            // same EnvelopeAuthenticationEvents methods this handler calls.
            services.AddAuthentication(ProbeAuthentication.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, ProbeAuthenticationHandler>(
                    ProbeAuthentication.SchemeName,
                    _ => { });

            services.AddAuthorizationBuilder()
                .AddPolicy(
                    ProbeAuthentication.PolicyName,
                    policy => policy.RequireClaim(
                        ProbeAuthentication.ClaimType,
                        ProbeAuthentication.ClaimValue));
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
