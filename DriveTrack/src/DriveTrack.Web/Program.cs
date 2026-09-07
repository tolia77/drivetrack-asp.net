using System.Globalization;
using System.Text.Json.Serialization;
using DriveTrack.Application;
using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Persistence;
using DriveTrack.Web.Api;
using DriveTrack.Web.Components;
using Microsoft.AspNetCore.Localization;

// AD-1: this file is the composition root and the only file in DriveTrack.Web
// permitted to name a DriveTrack.Infrastructure type. LayeringTests enforces that.

const string CorsPolicyName = "DriveTrackCors";
const string ApiPathPrefix = "/api";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// AD-19: connection string, diagnostics switches and every other environment-specific
// value arrive from configuration, which in the container means environment variables.
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

// AD-9: the validators live in Application and are discovered from its assembly. Registering them
// only makes them resolvable - an application service still invokes each one explicitly.
builder.Services.AddApplication();

// NFR-14: every user-facing string comes from a .resx beside its marker type. No ResourcesPath is
// configured, so IStringLocalizer<T> resolves the file from the type's full name and the folder
// layout under Resources/ is the whole convention.
builder.Services.AddLocalization();

// AD-7: the REST adapter. Every controller outcome and every failure leaves in one envelope.
builder.Services.AddControllers(options => options.Filters.Add<EnvelopeResultFilter>())
    .ConfigureApiBehaviorOptions(options =>
    {
        // Suppression 1: no automatic 400 ProblemDetails on invalid model state. Validation is
        // AD-9's explicit call inside an application service, not something an adapter filter does
        // behind the service's back and in a shape this contract does not define.
        options.SuppressModelStateInvalidFilter = true;

        // Suppression 2: this defaults to *on* and silently rewrites NotFound(), Conflict() and
        // Unauthorized() into framework problem documents - at exactly the status codes NFR-1
        // governs. Off, those results reach EnvelopeResultFilter with their status intact.
        options.SuppressMapClientErrors = true;
    })
    .AddJsonOptions(options =>
        // AD-21: an enum crosses the wire as its member name. An ordinal is a number whose meaning
        // changes the day someone reorders the enum, and every client would silently follow.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Epic 2 configures the cookie and JWT schemes and attaches EnvelopeAuthenticationEvents to both.
// The services are registered here so [Authorize] resolves and so the challenge and forbid paths
// exist to be attached to.
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

// NFR-12: an explicit origin list, never a wildcard. An unset list means no cross-origin
// caller is allowed, which is the safe default rather than an accidental open door.
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// AD-20: the schema is applied before the host accepts a single request. A failure here
// aborts startup rather than serving against a missing or stale schema.
await app.Services.GetRequiredService<DatabaseMigrator>()
    .MigrateAsync(app.Lifetime.ApplicationStopping);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// No UseHttpsRedirection: the compose topology terminates plain HTTP on the app container
// and TLS is not in scope for this story.

// NFR-14: the product is Ukrainian, so the culture is stated rather than negotiated. The provider
// chain is cleared, not merely narrowed: with one supported culture a negotiated result would be
// uk-UA anyway, but clearing it means a future second culture cannot be selected by an
// Accept-Language header or a query string without someone deciding to allow it.
var ukrainian = new CultureInfo("uk-UA");

app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(ukrainian),
    SupportedCultures = [ukrainian],
    SupportedUICultures = [ukrainian],
    RequestCultureProviders = [],
});

app.UseCors(CorsPolicyName);

// AD-7: branched onto /api so the Blazor shell keeps its HTML error and not-found pages while
// every REST path gets the envelope - including an unmatched route, which the status-code-pages
// middleware above would otherwise answer with HTML.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments(ApiPathPrefix),
    branch => branch.UseMiddleware<ApiEnvelopeMiddleware>());

app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> can boot this exact pipeline. The contract
/// is an HTTP contract; a test host that reassembled an approximation of these registrations would
/// be asserting against a replica of the adapter rather than the adapter.
/// </summary>
public partial class Program;
