using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using DriveTrack.Application;
using DriveTrack.Application.Authorization;
using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Identity;
using DriveTrack.Infrastructure.Persistence;
using DriveTrack.Web.Account;
using DriveTrack.Web.Api;
using DriveTrack.Web.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Localization;
using Microsoft.IdentityModel.Tokens;

// AD-1: this file is the composition root and the only file in DriveTrack.Web
// permitted to name a DriveTrack.Infrastructure type. LayeringTests enforces that.

const string CorsPolicyName = "DriveTrackCors";
const string ApiPathPrefix = "/api";

// AD-3's first step, made concrete. Two schemes, chosen by path: a browser navigating the Blazor
// shell carries a cookie, a REST client carries a bearer token, and the policy scheme below is what
// decides which handler a given request is even offered to.
const string SelectorScheme = AuthenticationSchemes.Selector;
const string CookieScheme = AuthenticationSchemes.Cookie;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// AD-19: connection string, JWT signing key, diagnostics switches and every other
// environment-specific value arrive from configuration, which in the container means environment
// variables.
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
    {
        // AD-21: an enum crosses the wire as its member name. An ordinal is a number whose meaning
        // changes the day someone reorders the enum, and every client would silently follow.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());

        // AD-22: the typed identities are a compile-time device, not a wire shape. Without these a
        // user id would leave as {"value":3}.
        options.JsonSerializerOptions.Converters.Add(new UserIdJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new DriverIdJsonConverter());
        options.JsonSerializerOptions.Converters.Add(new ClientIdJsonConverter());
    });

// The scheme selector, and the two handlers it forwards to.
//
// AddPolicyScheme is what makes "cookie off /api, bearer on /api/*" a single decision instead of a
// per-endpoint attribute nobody can audit. A cookie presented to /api/* is therefore not merely
// ignored - it is never looked at, because the JWT handler is the only one that runs there.
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services.AddAuthentication(SelectorScheme)
    .AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
        options.ForwardDefaultSelector = context =>
            context.Request.Path.StartsWithSegments(ApiPathPrefix)
                ? JwtBearerDefaults.AuthenticationScheme
                : CookieScheme)
    .AddCookie(CookieScheme, options =>
    {
        options.Cookie.Name = "drivetrack.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/sign-in";
        options.AccessDeniedPath = "/sign-in";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;

        // AD-7's third suppression. This handler serves the Blazor shell as well as anything else
        // that is not /api, so it branches: a REST path gets the envelope, a browser navigating to
        // a protected page still gets the redirect it expects.
        options.Events.OnRedirectToLogin = context =>
            context.Request.Path.StartsWithSegments(ApiPathPrefix)
                ? EnvelopeAuthenticationEvents.WriteChallengeAsync(context.HttpContext)
                : Redirect(context);

        options.Events.OnRedirectToAccessDenied = context =>
            context.Request.Path.StartsWithSegments(ApiPathPrefix)
                ? EnvelopeAuthenticationEvents.WriteForbiddenAsync(context.HttpContext)
                : Redirect(context);
    })
    .AddJwtBearer(options =>
    {
        // Claim types pass through exactly as they were issued. Left on, the handler renames a
        // handful of them to WS-Federation URIs and ICurrentUser would read none of them back.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = DriveTrackClaimTypes.Name,
            RoleClaimType = DriveTrackClaimTypes.Role,
        };

        // This handler only ever serves /api, so both events answer with the envelope
        // unconditionally. HandleResponse stops the default WWW-Authenticate 401 with no body.
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                context.HandleResponse();

                return EnvelopeAuthenticationEvents.WriteChallengeAsync(context.HttpContext);
            },
            OnForbidden = context => EnvelopeAuthenticationEvents.WriteForbiddenAsync(context.HttpContext),
        };
    });

builder.Services.AddAuthorization();

// AD-22: the caller, read from whichever source the current adapter has. Scoped, because a caller
// belongs to a request or to a circuit and to nothing longer-lived.
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

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

// FR-9: immediately after the migration, because the seeder writes rows into the tables it just
// created. Idempotent, so a restart against the same volume writes nothing.
await app.Services.GetRequiredService<IdentitySeeder>()
    .SeedAsync(app.Lifetime.ApplicationStopping);

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

app.UseAuthentication();
app.UseAuthorization();

// After authentication and authorization, which is the documented order: antiforgery runs against
// an established principal rather than before one exists.
app.UseAntiforgery();

app.MapControllers();

// FR-6: signing out clears the cookie. A POST, so it cannot be triggered by a link or an image,
// and on the cookie scheme by name because the selector would otherwise pick the bearer handler
// for a request that has no bearer token to revoke.
app.MapPost("/sign-out", async (HttpContext context, IAntiforgery antiforgery) =>
{
    // Validated explicitly. UseAntiforgery only checks endpoints that bind form values, and this one
    // binds none - so without this call the token NavMenu renders is never read and any cross-site
    // form could sign a user out.
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest();
    }

    await context.SignOutAsync(CookieScheme);

    return Results.Redirect("/");
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();

// The cookie handler's default redirect, kept as a named local so the two event branches above read
// as one decision - envelope on /api, redirect otherwise - rather than as two lambdas.
static Task Redirect(RedirectContext<CookieAuthenticationOptions> context)
{
    context.Response.Redirect(context.RedirectUri);

    return Task.CompletedTask;
}

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> can boot this exact pipeline. The contract
/// is an HTTP contract; a test host that reassembled an approximation of these registrations would
/// be asserting against a replica of the adapter rather than the adapter.
/// </summary>
public partial class Program;
