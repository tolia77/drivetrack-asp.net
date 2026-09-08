using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Authorization;

/// <summary>
/// The caller, expressed once (AD-22).
/// <para>
/// This is the <em>only</em> shape of a caller anywhere in <c>DriveTrack.Application</c>. No type in
/// this layer names <c>HttpContext</c>, <c>ClaimsPrincipal</c> or <c>AuthenticationStateProvider</c>
/// — an adapter reads whichever of those it has and answers these five members, so a service reads
/// the same caller whether the request arrived over REST, over a Blazor circuit or from a test.
/// </para>
/// <para>
/// The three identities stay non-interchangeable: <see cref="UserId"/>, <see cref="DriverId"/> and
/// <see cref="ClientId"/> are distinct types with no conversion between them, so an ownership check
/// cannot compare a user id against a driver row id and silently authorize the wrong caller.
/// </para>
/// </summary>
public interface ICurrentUser
{
    /// <summary>True when credentials were presented and accepted.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The calling user's id.</summary>
    /// <exception cref="InvalidOperationException">
    /// The caller is anonymous. Throwing rather than returning a default is what stops
    /// <c>default(UserId)</c> — user zero — being compared against a real row and passing.
    /// </exception>
    UserId UserId { get; }

    /// <summary>The calling user's single role (AD-4).</summary>
    /// <exception cref="InvalidOperationException">The caller is anonymous.</exception>
    UserRole Role { get; }

    /// <summary>The caller's driver row id, or null when the caller is not a driver.</summary>
    DriverId? DriverId { get; }

    /// <summary>The caller's client row id, or null when the caller is not a client.</summary>
    ClientId? ClientId { get; }
}
