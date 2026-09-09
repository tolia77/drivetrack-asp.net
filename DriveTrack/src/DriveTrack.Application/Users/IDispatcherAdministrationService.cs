using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Users;

/// <summary>
/// Dispatcher accounts (FR-49, FR-50). The original's <c>/dispatchers</c> endpoints, and the first
/// way in the system to create a dispatcher at all — until now the startup seeder was the only
/// thing that could.
/// <para>
/// Every method is <c>RequireRole(Admin)</c>: a dispatcher administers deliveries, not colleagues.
/// </para>
/// <para>
/// Every method also addresses a <em>dispatcher</em>. A user id belonging to an admin, a driver or
/// a client answers <c>COMMON_NOT_FOUND</c>, so this surface can never rename or delete an account
/// it was not written to manage — and cannot be used to discover which ids exist.
/// </para>
/// </summary>
public interface IDispatcherAdministrationService
{
    /// <summary>Opens a dispatcher account and returns it (FR-49).</summary>
    /// <exception cref="Common.ConflictException">Another account already holds that email.</exception>
    Task<DispatcherAccount> CreateAsync(
        CreateDispatcherCommand command,
        CancellationToken cancellationToken);

    /// <summary>Every dispatcher (FR-49).</summary>
    Task<IReadOnlyList<DispatcherAccount>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One dispatcher (FR-49).</summary>
    /// <exception cref="Common.NotFoundException">
    /// No account has that id, or the account is not a dispatcher.
    /// </exception>
    Task<DispatcherAccount> GetAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>
    /// Edits a dispatcher's name and password (FR-50). A command that leaves the password absent
    /// leaves the stored password exactly as it is (AD-23).
    /// </summary>
    Task<DispatcherAccount> UpdateAsync(
        UserId userId,
        UpdateDispatcherCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a dispatcher's account (FR-50). The timeline entries, chat messages and proofs they
    /// touched survive with a null actor reference rather than being erased or made undeletable.
    /// </summary>
    Task DeleteAsync(UserId userId, CancellationToken cancellationToken);
}
