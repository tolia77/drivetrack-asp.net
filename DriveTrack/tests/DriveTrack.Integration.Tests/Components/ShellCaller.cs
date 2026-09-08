using DriveTrack.Application.Authorization;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// The three services the shell components resolve, stubbed so the navigation and the landing page
/// can be rendered for a caller the test chooses.
/// <para>
/// A source scan cannot tell a signed-in caller's menu from an anonymous one — swapping the two
/// guards leaves the file containing all the same words. Rendering for each caller in turn is the
/// only thing that can.
/// </para>
/// </summary>
internal static class ShellCaller
{
    /// <summary>Renders a shell component as the given caller sees it.</summary>
    /// <typeparam name="TComponent">The component to render.</typeparam>
    /// <param name="role">The caller's role, or null for an anonymous visitor.</param>
    public static Task<string> RenderAsync<TComponent>(UserRole? role)
        where TComponent : IComponent =>
        ComponentRenderer.RenderAsync<TComponent>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ICurrentUser>(new StubCurrentUser(role));
                services.AddSingleton<NavigationManager, StubNavigationManager>();
                services.AddSingleton<AntiforgeryStateProvider, StubAntiforgeryStateProvider>();
            });

    /// <summary>
    /// A caller with a role and nothing else. <see cref="UserId"/> throws exactly as the real
    /// adapter does, so a component that reads it outside the authenticated branch fails here too.
    /// </summary>
    private sealed class StubCurrentUser(UserRole? role) : ICurrentUser
    {
        public bool IsAuthenticated => role is not null;

        public UserId UserId => role is null
            ? throw new InvalidOperationException("There is no authenticated caller.")
            : new UserId(1);

        public UserRole Role => role
            ?? throw new InvalidOperationException("There is no authenticated caller.");

        public DriverId? DriverId => null;

        public ClientId? ClientId => null;
    }

    /// <summary>A navigation manager rooted at the application's base, so NavLink can resolve hrefs.</summary>
    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }

    /// <summary>
    /// No token: static rendering outside a request has no antiforgery state, and
    /// <c>AntiforgeryToken</c> renders nothing rather than failing when there is none.
    /// </summary>
    private sealed class StubAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }
}
