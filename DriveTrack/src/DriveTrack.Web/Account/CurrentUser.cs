using System.Globalization;
using System.Security.Claims;
using DriveTrack.Application.Authorization;
using DriveTrack.Domain.Identity;
using Microsoft.AspNetCore.Components.Authorization;

namespace DriveTrack.Web.Account;

/// <summary>
/// AD-22's adapter half: the one type in the system that reads a <c>ClaimsPrincipal</c> and answers
/// <see cref="ICurrentUser"/>.
/// <para>
/// Three sources, in order. A REST request or a statically rendered page has an
/// <c>HttpContext</c> and its principal is the answer. A SignalR hub invocation has no request of
/// its own and no circuit either, so the hub hands its <c>Context.User</c> to the scope's
/// <see cref="HubCaller"/> and it is read here. An interactive Blazor circuit has neither — the
/// request that opened it finished long ago — so the caller comes from the
/// <c>AuthenticationStateProvider</c> the circuit maintains. All three produce the same claims,
/// because every scheme issues them through the names in <c>DriveTrackClaimTypes</c>.
/// </para>
/// <para>
/// A source that cannot answer is skipped rather than treated as "anonymous, stop looking". That
/// matters where two of them coexist: an ambient <c>HttpContext</c> carrying no principal must not
/// shadow the hub or circuit caller that is actually being served, because the guard would then
/// refuse a correctly authenticated caller with <c>AUTH_UNAUTHENTICATED</c> and nothing would say
/// why.
/// </para>
/// <para>
/// Nothing in <c>DriveTrack.Application</c> names any of the types this file does, which is the
/// whole point of the port: the reading is adapter work, and the answer is a caller.
/// </para>
/// </summary>
internal sealed class CurrentUser(
    IHttpContextAccessor httpContextAccessor,
    IServiceProvider services) : ICurrentUser
{
    /// <inheritdoc />
    public bool IsAuthenticated => AuthenticatedPrincipal is not null;

    /// <inheritdoc />
    public UserId UserId => new(RequireInt(DriveTrackClaimTypes.UserId));

    /// <inheritdoc />
    public UserRole Role
    {
        get
        {
            var value = Require().FindFirstValue(DriveTrackClaimTypes.Role);

            if (value is null || !Enum.TryParse<UserRole>(value, ignoreCase: false, out var role))
            {
                throw new InvalidOperationException(
                    "The authenticated caller carries no recognised role claim.");
            }

            return role;
        }
    }

    /// <inheritdoc />
    public DriverId? DriverId =>
        AuthenticatedPrincipal is { } principal
        && ReadInt(principal, DriveTrackClaimTypes.DriverId) is { } value
            ? new DriverId(value)
            : null;

    /// <inheritdoc />
    public ClientId? ClientId =>
        AuthenticatedPrincipal is { } principal
        && ReadInt(principal, DriveTrackClaimTypes.ClientId) is { } value
            ? new ClientId(value)
            : null;

    /// <summary>
    /// The caller, or null when there is none. Every member answers from this one condition: a
    /// principal the handler accepted <em>and</em> a user-id claim that parses. Two conditions -
    /// IsAuthenticated checking the claim and the readers checking only the identity - would let
    /// Role and the subtype ids answer for a caller IsAuthenticated reports as anonymous.
    /// <para>
    /// The three sources are tried in order and the first that yields such a principal wins. A
    /// source present but unable to answer falls through to the next, which is what keeps an
    /// ambient anonymous HttpContext from shadowing the hub invocation actually being served.
    /// </para>
    /// </summary>
    private ClaimsPrincipal? AuthenticatedPrincipal =>
        Authenticated(httpContextAccessor.HttpContext?.User)
        ?? Authenticated(HubPrincipal())
        ?? Authenticated(CircuitPrincipal());

    /// <summary>
    /// The principal if it is one this adapter can answer for, null otherwise: accepted by a
    /// handler, and carrying a user-id claim that parses.
    /// </summary>
    private static ClaimsPrincipal? Authenticated(ClaimsPrincipal? principal) =>
        principal is { Identity.IsAuthenticated: true }
        && ReadInt(principal, DriveTrackClaimTypes.UserId) is not null
            ? principal
            : null;

    /// <summary>
    /// The principal of the hub invocation this scope is serving, or null when the scope is not a
    /// hub's.
    /// <para>
    /// Resolved from the scope rather than injected, so nothing outside a hub pays for a service it
    /// will never read and <c>ICurrentUser</c> keeps the shape it has always had.
    /// </para>
    /// </summary>
    private ClaimsPrincipal? HubPrincipal() => services.GetService<HubCaller>()?.Principal;

    /// <summary>
    /// The circuit's principal, or null when there is no circuit.
    /// <para>
    /// The provider's task is already complete on a server circuit — the state was set when the
    /// circuit was created — so this reads it without blocking. A task that is somehow not complete
    /// is treated as "no caller yet" rather than waited on: blocking a render to answer a property
    /// getter is how a circuit deadlocks, and an anonymous answer here surfaces as the ordinary
    /// <c>AUTH_UNAUTHENTICATED</c> that FR-13's boundary already handles.
    /// </para>
    /// </summary>
    private ClaimsPrincipal? CircuitPrincipal()
    {
        var provider = services.GetService<AuthenticationStateProvider>();

        if (provider is null)
        {
            return null;
        }

        try
        {
            var state = provider.GetAuthenticationStateAsync();

            return state.IsCompletedSuccessfully ? state.Result.User : null;
        }
        catch (InvalidOperationException)
        {
            // A provider that is resolvable but refuses to answer outside a Razor component's own
            // scope - which is every SignalR hub scope, because the container registers one
            // application-wide. "No caller here" is the honest answer and the one this adapter's
            // contract promises: a hub method that forgot to adopt its principal then surfaces as
            // the ordinary AUTH_UNAUTHENTICATED the guard raises, rather than as an opaque
            // framework exception nothing in the failure vocabulary describes.
            return null;
        }
    }

    private ClaimsPrincipal Require() =>
        AuthenticatedPrincipal
        ?? throw new InvalidOperationException(
                "There is no authenticated caller. Check ICurrentUser.IsAuthenticated, or let "
                    + "IAccessGuard answer - reading a caller that is not there would otherwise "
                    + "yield user zero and compare equal to nothing.");

    private int RequireInt(string claimType) =>
        ReadInt(Require(), claimType)
        ?? throw new InvalidOperationException(
            "The authenticated caller carries no '" + claimType + "' claim.");

    private static int? ReadInt(ClaimsPrincipal principal, string claimType)
    {
        var value = principal.FindFirstValue(claimType);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
