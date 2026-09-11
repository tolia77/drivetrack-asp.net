using DriveTrack.Application.Common;
using DriveTrack.Application.Users;

namespace DriveTrack.Web.Components.Pages.Admin;

/// <summary>
/// What the client edit dialog is bound to, and the two decisions that dialog takes: which stored
/// values a freshly opened form carries, and which of them the update command sends.
/// <para>
/// Both live in a class rather than in <c>Clients.razor</c>'s <c>@code</c> block for the reason
/// <see cref="PasswordBox"/> does, and the reason is sharper here than usual: the test harness takes
/// a static first render only, so no test can click Edit, and until this lift nothing in the tree
/// observed either decision. Dropping the address out of <see cref="Fill"/> would have sent a
/// present-null address on every administrator edit — refused 422 by
/// <c>ClientAccountStateValidator</c>, which FR-92 gave a rule — with the whole suite still green.
/// </para>
/// </summary>
internal sealed class ClientEditForm
{
    /// <summary>The bound given-name box.</summary>
    public string? FirstName { get; set; }

    /// <summary>The bound family-name box.</summary>
    public string? LastName { get; set; }

    /// <summary>The bound address box (FR-92).</summary>
    public string? Email { get; set; }

    /// <summary>The bound phone box.</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>The bound password box; null whenever the administrator has typed nothing.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Loads the row the administrator clicked Edit on into the boxes they are about to see.
    /// </summary>
    /// <param name="client">The client account as the roster last read it.</param>
    /// <remarks>
    /// Every stored field is carried, because AD-23 makes the command say what the form shows: a box
    /// left as the administrator found it has to send the value that was already there, and an empty
    /// address box is refused rather than read as "unchanged". The password is the one exception —
    /// only a hash is stored (FR-8), so there is nothing to pre-fill it with, and empty is exactly
    /// what "leave the password alone" looks like.
    /// </remarks>
    public void Fill(ClientAccount client)
    {
        ArgumentNullException.ThrowIfNull(client);

        FirstName = client.FirstName;
        LastName = client.LastName;
        Email = client.Email;
        PhoneNumber = client.PhoneNumber;
        Password = null;
    }

    /// <summary>The command for a filled-in edit form (AD-23).</summary>
    /// <returns>The partial edit this form describes.</returns>
    /// <remarks>
    /// Named arguments throughout: the command's last two parameters are both
    /// <c>Optional&lt;string?&gt;</c>, so transposing the password and the address would compile and
    /// would send the administrator's new password as the account's sign-in address.
    /// </remarks>
    public UpdateClientCommand ToCommand() =>
        new(
            FirstName: Optional<string?>.Present(FirstName),
            LastName: Optional<string?>.Present(LastName),
            PhoneNumber: Optional<string?>.Present(PhoneNumber),
            Password: PasswordBox.ToOptional(Password),
            Email: Optional<string?>.Present(Email));
}
