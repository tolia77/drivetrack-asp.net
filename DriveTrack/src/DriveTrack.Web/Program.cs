using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using DriveTrack.Application;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Deliveries;
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

// AD-3's first step, made concrete. Two schemes, chosen by path: a browser navigating the Blazor
// shell carries a cookie, a REST client carries a bearer token, and the policy scheme below is what
// decides which handler a given request is even offered to.
const string SelectorScheme = AuthenticationSchemes.Selector;
const string CookieScheme = AuthenticationSchemes.Cookie;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        // FR-119's signature comes back from the browser as a base64 PNG, which is a single JS
        // interop return value and therefore a single SignalR message. The default ceiling on one
        // of those is 32 KiB, and a signature drawn on a full-width pad at a phone's device pixel
        // ratio is comfortably larger than that - so without this line the transport closes the
        // circuit mid-capture, and the driver loses the typed recipient, the chosen photographs
        // and the screen behind the dialog with no message at all.
        //
        // Sized from ProofAssetRules rather than picked, for the reason the REST adapter's body cap
        // is: the validator decides what a legal asset is, and a transport that refused first would
        // be a second, stricter definition of one (AD-9). Base64 is four bytes per three, and the
        // slack covers the JSON framing the payload travels inside.
        options.MaximumReceiveMessageSize =
            (ProofAssetRules.MaximumAssetBytes * 4 / 3) + (64 * 1024);
    });

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

        // AD-23: a partial update has to be able to say "I did not send this field" and "I sent
        // this field empty" differently. Without this factory a PATCH-shaped body cannot.
        options.JsonSerializerOptions.Converters.Add(new OptionalJsonConverterFactory());
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
            MachineSurface.PrefersBearer(context.Request)
                ? JwtBearerDefaults.AuthenticationScheme
                : CookieScheme)
    .AddCookie(CookieScheme, options =>
    {
        options.Cookie.Name = "drivetrack.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/sign-in";
        // FR-79: forbidden and unauthenticated are different failures. A signed-in caller the
        // handler refuses is told they lack access, not told to sign in - which they already have.
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;

        // AD-7's third suppression. This handler serves the Blazor shell as well as anything else
        // that is not a machine surface, so it branches: a REST path and the asset route get the
        // envelope, a browser navigating to a protected page still gets the redirect it expects.
        //
        // The asset route is on the envelope side even though a browser is exactly who asks for it.
        // A redirect there is answered at status 200 with a page of HTML where an <img> expected an
        // image, so the browser renders a broken image and nothing anywhere says why; an enveloped
        // 401 is a status a caller - and a test - can read.
        options.Events.OnRedirectToLogin = context =>
            MachineSurface.WantsEnvelope(context.Request.Path)
                ? EnvelopeAuthenticationEvents.WriteChallengeAsync(context.HttpContext)
                : Redirect(context);

        options.Events.OnRedirectToAccessDenied = context =>
            MachineSurface.WantsEnvelope(context.Request.Path)
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

// UseRequestLocalization sets the culture per request, and the product has work that runs on no
// request at all: the side-effect worker composes a client's notification from EmailText on its own
// thread, long after the request that queued it was answered. Without these two the notice would be
// formatted in whatever culture the machine happens to have, which is the one place NFR-15 would
// silently not hold. Set before the host serves anything, so every thread it starts inherits them.
CultureInfo.DefaultThreadCurrentCulture = ukrainian;
CultureInfo.DefaultThreadCurrentUICulture = ukrainian;

app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(ukrainian),
    SupportedCultures = [ukrainian],
    SupportedUICultures = [ukrainian],
    RequestCultureProviders = [],
});

app.UseCors(CorsPolicyName);

// AD-7: branched onto the machine surfaces so the Blazor shell keeps its HTML error and not-found
// pages while every REST path gets the envelope - including an unmatched route, which the
// status-code-pages middleware above would otherwise answer with HTML.
//
// The asset route is inside the branch as well, and only its failures are affected: the endpoint's
// success is a FileStreamHttpResult, which has already written its content type and its bytes by
// the time the middleware's backstop looks, so the bytes are left alone. A DriveTrackException
// thrown out of the capability, on the other hand, becomes the same envelope a REST caller gets.
app.UseWhen(
    context => MachineSurface.WantsEnvelope(context.Request.Path),
    branch => branch.UseMiddleware<ApiEnvelopeMiddleware>());

app.UseAuthentication();
app.UseAuthorization();

// After authentication and authorization, which is the documented order: antiforgery runs against
// an established principal rather than before one exists.
app.UseAntiforgery();

app.MapControllers();

// FR-122 / AD-26 / DR-14: the one way a stored proof asset's bytes reach anybody.
//
// A minimal-API endpoint rather than a controller action, and outside /api rather than inside it,
// for the two reasons MachineSurface states: no MVC filter sees it, so nothing wraps an image in
// the JSON envelope, and the scheme is chosen by the credential the caller actually presented, so
// the same URL serves a driver's <img> tag and a REST client's bearer token.
//
// RequireAuthorization and nothing more: which assets *this* caller may read is IAccessGuard's
// decision inside IProofOfDeliveryService, narrowed in the query (AD-2, AD-3), so an asset
// belonging to another client's delivery is not found rather than refused.
app.MapGet(
    MachineSurface.ProofAssetPrefix + "/{assetId:int}",
    async (int assetId, HttpContext context, IProofOfDeliveryService proofs, CancellationToken cancellationToken) =>
    {
        var asset = await proofs.OpenAssetAsync(assetId, cancellationToken);

        // These are bytes a driver uploaded, under a type that driver declared: the allowlist
        // checked what was *said* about the file and nothing anywhere looked inside it. Without
        // this header a browser is free to sniff the content and act on what it finds, so a
        // document that claims to be a PNG and is really markup would be rendered as markup, on
        // this application's own origin, to whoever may read the proof. The allowlist narrows who
        // can try; this is what makes the attempt worthless.
        context.Response.Headers.XContentTypeOptions = "nosniff";

        // The content type stored beside the bytes, so nothing guesses. The framework disposes the
        // stream once the response has been written.
        return Results.Stream(asset.Content, asset.ContentType);
    })
    .RequireAuthorization();

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
