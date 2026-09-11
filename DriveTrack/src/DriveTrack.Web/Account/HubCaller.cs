using System.Security.Claims;

namespace DriveTrack.Web.Account;

/// <summary>
/// The caller of the SignalR hub invocation currently being served, held for
/// <see cref="CurrentUser"/> to read.
/// <para>
/// <c>CurrentUser</c> reads two sources: the HTTP context, and the Blazor circuit's authentication
/// state. A hub invocation has neither. <c>IHttpContextAccessor.HttpContext</c> is not the caller's
/// request once a connection is established, and a hub scope is not a circuit scope — so without
/// this holder every guard call made from a hub method throws <c>AUTH_UNAUTHENTICATED</c> and the
/// whole capability is unreachable while looking correctly authorized.
/// </para>
/// <para>
/// Scoped, and that is what makes it safe. SignalR creates a dependency-injection scope per hub
/// method invocation, so one holder serves one invocation of one caller and cannot leak into
/// another's. The hub assigns it from <c>Context.User</c> as the first statement of every method;
/// anything that resolves it outside a hub scope reads null and falls through to the two sources
/// that were always there.
/// </para>
/// <para>
/// Public rather than internal, unlike its neighbours in this folder: it is a constructor parameter
/// of <c>ChatHub</c>, which SignalR reflects over and builds executors for, and a public hub with an
/// internal dependency does not compile.
/// </para>
/// </summary>
public sealed class HubCaller
{
    /// <summary>
    /// The principal SignalR established for this connection, or null when nothing is being served
    /// through a hub in this scope.
    /// </summary>
    public ClaimsPrincipal? Principal { get; set; }
}
