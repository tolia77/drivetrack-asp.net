using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Persistence;
using DriveTrack.Web.Components;

// AD-1: this file is the composition root and the only file in DriveTrack.Web
// permitted to name a DriveTrack.Infrastructure type. LayeringTests enforces that.

const string CorsPolicyName = "DriveTrackCors";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// AD-19: connection string, diagnostics switches and every other environment-specific
// value arrive from configuration, which in the container means environment variables.
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

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

app.UseCors(CorsPolicyName);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
