using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Users;

/// <summary>
/// A client account as an administrator or dispatcher reads it (FR-46, FR-48). A DTO, so neither
/// <c>Client</c> nor <c>ApplicationUser</c> crosses an adapter boundary (AD-17).
/// </summary>
/// <param name="UserId">The account this client is. Every operation addresses a client by this.</param>
/// <param name="ClientId">The client row's own id, distinct from the user's (AD-22).</param>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">The address the account signs in with, and which an administrator may change (FR-92).</param>
/// <param name="PhoneNumber">Contact number in E.164 form.</param>
public sealed record ClientAccount(
    UserId UserId,
    ClientId ClientId,
    string FirstName,
    string LastName,
    string Email,
    string PhoneNumber);

/// <summary>
/// A dispatcher account as an administrator reads it (FR-49). There is no dispatcher subtype row
/// (DR-3), so this is the user and nothing else.
/// </summary>
/// <param name="UserId">The account.</param>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">The address the account signs in with; set at creation only.</param>
public sealed record DispatcherAccount(UserId UserId, string FirstName, string LastName, string Email);

/// <summary>
/// A partial edit of a client (FR-46). Every field is an <see cref="Optional{T}"/>: absent means
/// unchanged, present-null means the caller sent nothing in a field they did send.
/// <para>
/// The password is why the distinction has to be a type rather than a convention. A blank password
/// box on an edit form means "leave it alone", which an ordinary nullable string cannot say without
/// also being the way to say "clear it" — and clearing a password is not an operation this system
/// has.
/// </para>
/// </summary>
/// <param name="FirstName">Given name, or absent.</param>
/// <param name="LastName">Family name, or absent.</param>
/// <param name="PhoneNumber">Contact number in E.164 form, or absent.</param>
/// <param name="Password">A new password, or absent for unchanged (FR-50).</param>
/// <param name="Email">
/// The address the account signs in with, or absent for unchanged (FR-92). It rides the edit the
/// administrator is already performing rather than being an operation of its own, so a taken address
/// rolls the name and the phone number back with it (AD-5).
/// </param>
public sealed record UpdateClientCommand(
    Optional<string?> FirstName,
    Optional<string?> LastName,
    Optional<string?> PhoneNumber,
    Optional<string?> Password,
    Optional<string?> Email);

/// <summary>
/// What an administrator sends to open a dispatcher account (FR-49). Not partial: a new account has
/// no stored state to merge onto, so every field is required and plainly nullable.
/// </summary>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">The address the account will sign in with. The only chance to set it.</param>
/// <param name="Password">The chosen password; hashed by Identity and never stored as sent (FR-8).</param>
public sealed record CreateDispatcherCommand(
    string? FirstName,
    string? LastName,
    string? Email,
    string? Password);

/// <summary>
/// A partial edit of a dispatcher (FR-50). No email and no role: the first is story 7.3's, the
/// second is fixed for the life of an account (AD-4).
/// </summary>
/// <param name="FirstName">Given name, or absent.</param>
/// <param name="LastName">Family name, or absent.</param>
/// <param name="Password">A new password, or absent for unchanged (FR-50).</param>
public sealed record UpdateDispatcherCommand(
    Optional<string?> FirstName,
    Optional<string?> LastName,
    Optional<string?> Password);

/// <summary>
/// A client account after the payload has been merged onto what is stored (AD-23). This — not the
/// command — is what the validator sees, so a rule holds over the row as it will exist rather than
/// over the handful of fields this particular request happened to mention.
/// </summary>
/// <param name="FirstName">Given name as it will be.</param>
/// <param name="LastName">Family name as it will be.</param>
/// <param name="PhoneNumber">Contact number as it will be.</param>
/// <param name="Password">
/// The new password, or null for unchanged. There is no current plaintext to merge against — only
/// a hash is stored (FR-8) — so absent stays null here and means "not being set".
/// </param>
/// <param name="Email">The address as it will be. Unlike the password, this one is stored and merges.</param>
public sealed record ClientAccountState(
    string? FirstName,
    string? LastName,
    string? PhoneNumber,
    string? Password,
    string? Email);

/// <inheritdoc cref="ClientAccountState" />
/// <param name="FirstName">Given name as it will be.</param>
/// <param name="LastName">Family name as it will be.</param>
/// <param name="Password">The new password, or null for unchanged.</param>
public sealed record DispatcherAccountState(string? FirstName, string? LastName, string? Password);
