namespace DriveTrack.Application.Users;

/// <summary>
/// What a visitor sends to open a client account (FR-1).
/// <para>
/// The two password fields are both here on purpose: the confirmation is a rule about the request,
/// so it belongs to the request rather than to the adapter that happened to render the form.
/// </para>
/// </summary>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">The address the account will sign in with.</param>
/// <param name="PhoneNumber">Contact number in E.164 form.</param>
/// <param name="Password">The chosen password; hashed by Identity and never stored as sent (FR-8).</param>
/// <param name="PasswordConfirmation">Must equal <paramref name="Password"/>.</param>
public sealed record RegisterClientCommand(
    string? FirstName,
    string? LastName,
    string? Email,
    string? PhoneNumber,
    string? Password,
    string? PasswordConfirmation);
