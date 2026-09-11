using DriveTrack.Application.Common;

namespace DriveTrack.Application.Users;

/// <summary>
/// A partial edit of a user's own profile (FR-87). Every field is an <see cref="Optional{T}"/>:
/// absent means unchanged, present-null means the caller sent the field and sent nothing in it.
/// <para>
/// There is no email and no password here. Both are separate operations with rules of their own —
/// an address can collide with another account's and a password needs the current one first — and
/// folding either into a general "save my profile" would hide that behind a field nobody had to
/// think about.
/// </para>
/// </summary>
/// <param name="FirstName">Given name, or absent.</param>
/// <param name="LastName">Family name, or absent.</param>
/// <param name="PhoneNumber">
/// Contact number in E.164 form, or absent. Only a client has a row to store one in (DR-3), so a
/// caller of any other role who sends it is refused rather than quietly ignored.
/// </param>
public sealed record UpdateProfileCommand(
    Optional<string?> FirstName,
    Optional<string?> LastName,
    Optional<string?> PhoneNumber);

/// <summary>
/// A profile after the payload has been merged onto what is stored (AD-23). This — not the command —
/// is what the validator sees, so a rule holds over the row as it will exist.
/// </summary>
/// <param name="FirstName">Given name as it will be.</param>
/// <param name="LastName">Family name as it will be.</param>
/// <param name="PhoneNumber">Contact number as it will be, or null when there is none.</param>
/// <param name="IsClient">
/// Whether this account has a client row behind it. Carried on the merged state rather than read as
/// a role inside the service, so "only a client has a phone number" is a property of the row the
/// validator is judging instead of a branch somewhere else that the validator cannot see.
/// </param>
public sealed record ProfileState(
    string? FirstName,
    string? LastName,
    string? PhoneNumber,
    bool IsClient);

/// <summary>
/// What a signed-in user sends to replace their own password (FR-88).
/// <para>
/// Not a merge, and not an <see cref="Optional{T}"/> anywhere: there is no stored plaintext to fall
/// back on, and every one of the three fields has to be present for the operation to mean anything.
/// The current password is what makes this different from an administrator's reset — a borrowed
/// session cannot change the password without knowing the old one.
/// </para>
/// </summary>
/// <param name="CurrentPassword">The password the account signs in with today.</param>
/// <param name="NewPassword">The password it will sign in with; hashed by Identity (FR-8).</param>
/// <param name="NewPasswordConfirmation">The new password again, to catch a typo before it is stored.</param>
public sealed record ChangePasswordCommand(
    string? CurrentPassword,
    string? NewPassword,
    string? NewPasswordConfirmation);

/// <summary>
/// What a signed-in user sends to replace the address their account signs in with (FR-92).
/// <para>
/// One field, and its own operation rather than a field on <see cref="UpdateProfileCommand"/>: this
/// is the only edit on the profile that can collide with another account, and the refusal it can
/// raise is a 409 rather than a 422.
/// </para>
/// </summary>
/// <param name="Email">The address the account will sign in with.</param>
public sealed record ChangeEmailCommand(string? Email);
