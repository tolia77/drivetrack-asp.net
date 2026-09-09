using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Users;

/// <summary>
/// The client roster (FR-46, FR-47, FR-48). The original's <c>/clients</c> endpoints, expressed
/// once and reached by both the Blazor screen and the REST controller.
/// <para>
/// Reading is <c>RequireRole(Dispatcher)</c>, which AD-4 makes "a dispatcher or an admin": a
/// dispatcher needs the roster to attach a client to a delivery (FR-48). Writing is
/// <c>RequireRole(Admin)</c>. Every method takes that decision itself, inside
/// <see cref="Authorization.IAccessGuard"/>; the controller attribute and the navigation entry are
/// conveniences layered on top of a decision that was already made.
/// </para>
/// <para>
/// A client is addressed by its <see cref="UserId"/>, not by its <c>ClientId</c>. The account is
/// the thing being administered — the name and the password are the user's, and deleting a client
/// deletes the user — so the client row's own key would be an id for half of the operation.
/// </para>
/// </summary>
public interface IClientAdministrationService
{
    /// <summary>Every client, name, email and phone (FR-46, FR-48). Dispatcher or admin.</summary>
    Task<IReadOnlyList<ClientAccount>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One client (FR-46, FR-48). Dispatcher or admin.</summary>
    /// <exception cref="Common.NotFoundException">
    /// No account has that id, or the account is not a client.
    /// </exception>
    Task<ClientAccount> GetAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>
    /// Edits a client's name, phone number and password (FR-46). Admin only. Every field the
    /// command leaves absent stays exactly as it is (AD-23).
    /// </summary>
    Task<ClientAccount> UpdateAsync(
        UserId userId,
        UpdateClientCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a client's account (FR-47). Admin only. The client row goes with the user and its
    /// reviews go with the client; the deliveries it requested survive with no client.
    /// </summary>
    Task DeleteAsync(UserId userId, CancellationToken cancellationToken);
}
