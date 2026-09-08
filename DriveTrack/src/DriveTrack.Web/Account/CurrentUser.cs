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
/// Two sources, in order. A REST request or a statically rendered page has an
/// <c>HttpContext</c> and its principal is the answer. An interactive Blazor circuit does not — the
/// request that opened it finished long ago — so the caller comes from the
/// <c>AuthenticationStateProvider</c> the circuit maintains. Both produce the same claims, because
/// both schemes issue them through the names in <c>DriveTrackClaimTypes</c>.
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

    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User ?? CircuitPrincipal();

    /// <summary>
    /// The caller, or null when there is none. Every member answers from this one condition: a
    /// principal the handler accepted <em>and</em> a user-id claim that parses. Two conditions -
    /// IsAuthenticated checking the claim and the readers checking only the identity - would let
    /// Role and the subtype ids answer for a caller IsAuthenticated reports as anonymous.
    /// </summary>
    private ClaimsPrincipal? AuthenticatedPrincipal =>
        Principal is { Identity.IsAuthenticated: true } principal
        && ReadInt(principal, DriveTrackClaimTypes.UserId) is not null
            ? principal
            : null;

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

        var state = provider.GetAuthenticationStateAsync();

        return state.IsCompletedSuccessfully ? state.Result.User : null;
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
