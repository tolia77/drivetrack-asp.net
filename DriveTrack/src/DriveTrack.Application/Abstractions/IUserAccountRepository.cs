using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Abstractions;

/// <summary>
/// An account as this layer knows it: the identity fields plus the single role, and the subtype row
/// id when the role has one. Application never sees <c>ApplicationUser</c> or any other Identity
/// type (AD-1, AD-17) — the whole of ASP.NET Core Identity is behind this record and the port below.
/// </summary>
/// <param name="Id">The user row's id.</param>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">The address the account signs in with.</param>
/// <param name="Role">The single role (AD-4).</param>
/// <param name="DriverId">The driver row id, or null when the user is not a driver.</param>
/// <param name="ClientId">The client row id, or null when the user is not a client.</param>
public sealed record UserAccount(
    UserId Id,
    string FirstName,
    string LastName,
    string Email,
    UserRole Role,
    DriverId? DriverId,
    ClientId? ClientId);

/// <summary>
/// The identity fields of an account that does not exist yet. The password travels separately and
/// only ever reaches storage through Identity's <c>IPasswordHasher</c> (FR-8), which is why it is
/// not a property of this record.
/// </summary>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">The address the account will sign in with; also its user name.</param>
public sealed record NewUserAccount(string FirstName, string LastName, string Email);

/// <summary>
/// The Identity port (AD-1, AD-5). It joins the repository roster on <see cref="IUnitOfWork"/>, so
/// a user, its role row and its subtype row are staged against one context inside one transaction
/// and land together or not at all.
/// <para>
/// AD-6's rules hold here as they do for every other repository: materialized results, no
/// <c>IQueryable</c>, a cancellation token on every I/O method, and nothing Identity-shaped
/// crossing back into Application.
/// </para>
/// </summary>
public interface IUserAccountRepository
{
    /// <summary>Loads the account with that email, or null when there is none.</summary>
    Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>Loads the account with that id, or null when there is none.</summary>
    Task<UserAccount?> GetByIdAsync(UserId id, CancellationToken cancellationToken);

    /// <summary>Whether any account already holds that email, compared as Identity normalizes it.</summary>
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the account with its single role, hashing <paramref name="password"/> through
    /// Identity (FR-8). The returned record carries the database-assigned id, which the caller
    /// needs for the subtype row's foreign key.
    /// </summary>
    /// <exception cref="Common.ConflictException">The email is already in use.</exception>
    /// <exception cref="Common.ValidationException">Identity's own policy refused the password.</exception>
    Task<UserAccount> CreateAsync(
        NewUserAccount account,
        string password,
        UserRole role,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stages a change to the account's name, so it lands with the rest of the operation at
    /// <see cref="IUnitOfWork.CommitAsync"/> (FR-37).
    /// <para>
    /// Deliberately narrow: the two name fields and nothing else. The address and the credentials
    /// are the account capability's, and a general "update the user" method here would be the seam
    /// through which a second writer of them appeared.
    /// </para>
    /// <para>
    /// An id no account holds is not an error: the caller has already loaded the row it means to
    /// rename and answered 404 if there was none.
    /// </para>
    /// </summary>
    Task RenameAsync(
        UserId userId,
        string firstName,
        string lastName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stages the account for deletion, so it lands with the rest of the operation at
    /// <see cref="IUnitOfWork.CommitAsync"/> (FR-39).
    /// <para>
    /// Deleting the account rather than only the subtype row is the point: an account still holding
    /// role <c>Driver</c> with no driver row behind it is signable-in and carries a null driver
    /// claim, which is a worse state than either. The declared cascades do the rest — the driver
    /// row, its shifts and its chat messages go with it, and its deliveries are left unassigned.
    /// </para>
    /// <para>
    /// An id no account holds is not an error here: the caller has already loaded the row it means
    /// to delete and answered 404 if there was none.
    /// </para>
    /// </summary>
    Task DeleteAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="password"/> matches the stored hash for that user.</summary>
    Task<bool> VerifyPasswordAsync(UserId userId, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Ensures a role row exists for <paramref name="role"/>. Idempotent, and the only reason the
    /// role vocabulary needs a write path at all: the startup seeder (FR-9) creates the four rows
    /// once and nothing else ever adds one.
    /// </summary>
    Task EnsureRoleAsync(UserRole role, CancellationToken cancellationToken);

    /// <summary>
    /// Every account holding <paramref name="role"/>, ordered by name (FR-46, FR-49).
    /// <para>
    /// Unpaginated on purpose: the original's two administration lists are unpaginated and NFR-27
    /// preserves the paging that already existed rather than inventing more.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<UserAccount>> ListByRoleAsync(UserRole role, CancellationToken cancellationToken);

    /// <summary>
    /// Renames an account, staged for the caller's commit (FR-46, FR-50). Neither the email nor
    /// the role is touched — email editing is story 7.3, and AD-4 gives an account one role for
    /// its whole life.
    /// </summary>
    /// <exception cref="Common.NotFoundException">No account has that id.</exception>
    Task UpdateNameAsync(
        UserId userId,
        string firstName,
        string lastName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the stored password (FR-8, FR-46, FR-50). The plaintext reaches storage only
    /// through Identity's hasher, and Identity's own strength policy runs here, so a weak password
    /// is refused by the same rule and reported with the same code as at registration.
    /// </summary>
    /// <exception cref="Common.NotFoundException">No account has that id.</exception>
    /// <exception cref="Common.ValidationException">Identity's own policy refused the password.</exception>
    Task SetPasswordAsync(UserId userId, string password, CancellationToken cancellationToken);

}
