using DriveTrack.Application.Common;
using DriveTrack.Application.Users;

namespace DriveTrack.Web.Components.Account;

/// <summary>
/// FR-87's one screen-side decision, in one place: what the profile form sends when the caller has
/// no phone number to send.
/// <para>
/// It lives in a class rather than in <c>Profile.razor</c>'s <c>@code</c> block for the reason
/// <c>PasswordBox</c> does: a private method inside a generated component class is reachable by no
/// test, and this is the rule the whole screen turns on. A dispatcher whose form sent
/// <c>Present(null)</c> instead of <see cref="Optional{T}.Absent"/> would be refused on every save
/// of their own name, and nothing but this file can be pointed at to say so.
/// </para>
/// </summary>
internal static class ProfileEdit
{
    /// <summary>
    /// The command for a filled-in profile form.
    /// </summary>
    /// <param name="firstName">The bound given-name box.</param>
    /// <param name="lastName">The bound family-name box.</param>
    /// <param name="phoneNumber">The bound phone box; null for a caller who was never shown one.</param>
    /// <param name="isClient">Whether the caller has a client row, and so a phone number at all.</param>
    /// <remarks>
    /// The names are always present: the form renders them for every role, and an emptied box is a
    /// caller asking to clear a name, which the merged state refuses rather than quietly restores.
    /// <para>
    /// The phone number is absent for anyone but a client, because DR-3 gives them no row to store
    /// one in — sending the field at all is refused, which is right for a caller who typed one and
    /// wrong for a screen that never offered the box.
    /// </para>
    /// </remarks>
    public static UpdateProfileCommand ToProfileCommand(
        string? firstName,
        string? lastName,
        string? phoneNumber,
        bool isClient) =>
        new(
            Optional<string?>.Present(firstName),
            Optional<string?>.Present(lastName),
            isClient ? Optional<string?>.Present(phoneNumber) : Optional<string?>.Absent);
}

/// <summary>
/// What the three profile dialogs are bound to. Nullable throughout, because a box can be empty.
/// <para>
/// Out of <c>Profile.razor</c>'s <c>@code</c> block for the same reason <see cref="ProfileEdit"/> is
/// and with the same sharpness as <c>ClientEditForm</c>: the harness renders a static first pass, so
/// no test reaches the click that fills these boxes. Dropping a name out of <see cref="Fill"/> would
/// send a present-null name on every self-service save — refused 422 by <c>ProfileStateValidator</c>
/// — while <see cref="ProfileEdit.ToProfileCommand"/>'s own test, which is handed string literals,
/// carried on passing.
/// </para>
/// </summary>
internal sealed class ProfileEditForm
{
    /// <summary>The bound given-name box.</summary>
    public string? FirstName { get; set; }

    /// <summary>The bound family-name box.</summary>
    public string? LastName { get; set; }

    /// <summary>The bound phone box; a client is the only caller ever shown one (DR-3).</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>The bound address box (FR-92).</summary>
    public string? Email { get; set; }

    /// <summary>The bound current-password box (FR-88).</summary>
    public string? CurrentPassword { get; set; }

    /// <summary>The bound new-password box.</summary>
    public string? NewPassword { get; set; }

    /// <summary>The bound confirmation box.</summary>
    public string? NewPasswordConfirmation { get; set; }

    /// <summary>
    /// Loads the stored profile into the edit dialog's boxes (FR-87).
    /// </summary>
    /// <param name="profile">The profile as the screen last read it.</param>
    /// <remarks>
    /// The address is left alone: it is the email dialog's box, and that dialog fills it when it
    /// opens. The password boxes are never pre-filled at all — only a hash is stored (FR-8).
    /// </remarks>
    public void Fill(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        FirstName = profile.FirstName;
        LastName = profile.LastName;
        PhoneNumber = profile.PhoneNumber;
    }

    /// <summary>Empties the three password boxes.</summary>
    public void ClearPasswords()
    {
        CurrentPassword = null;
        NewPassword = null;
        NewPasswordConfirmation = null;
    }
}
