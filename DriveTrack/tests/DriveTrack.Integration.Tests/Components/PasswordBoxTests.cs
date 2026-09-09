using DriveTrack.Application.Common;
using DriveTrack.Web.Components.Pages.Admin;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// FR-50's rule, asserted directly: an empty password box leaves the stored password alone.
/// <para>
/// This is the decision the whole story turns on, and it is the one no other test can reach. The
/// screen tests stub every write to throw, so they never enter a save handler, and the HTTP suites
/// never render a screen — swapping <see cref="Optional{T}.Absent"/> for a present value inside a
/// component's <c>@code</c> block would ship green and silently overwrite a password with an empty
/// string. Lifting the choice into <see cref="PasswordBox"/> is what makes it assertable, exactly as
/// <c>SessionExpiry.NoticeKey</c> was lifted out of its boundary component.
/// </para>
/// </summary>
public class PasswordBoxTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_box_the_administrator_did_not_fill_in_is_absent(string? typed)
    {
        // Absent, not present-null and not an empty string: the command has to say "I am not
        // changing this", which is the only reading under which the field's own hint is true.
        var answer = PasswordBox.ToOptional(typed);

        Assert.False(answer.HasValue);
        Assert.Equal(Optional<string?>.Absent, answer);
    }

    [Fact]
    public void A_box_with_a_password_in_it_carries_that_password_untouched()
    {
        var answer = PasswordBox.ToOptional("New-Passw0rd");

        Assert.True(answer.HasValue);
        Assert.Equal("New-Passw0rd", answer.Value);
    }

    [Fact]
    public void A_password_that_merely_starts_or_ends_with_a_space_is_kept_as_typed()
    {
        // Whitespace decides whether the box counts as filled; it is never trimmed out of a value
        // the caller did mean. A password silently shortened here would be one the account holder
        // could never reproduce.
        var answer = PasswordBox.ToOptional(" New-Passw0rd ");

        Assert.True(answer.HasValue);
        Assert.Equal(" New-Passw0rd ", answer.Value);
    }

    [Fact]
    public void The_two_answers_are_distinguishable()
    {
        // The failure this file exists to catch: if the empty case ever became a present value, the
        // two would compare equal in kind and an untouched box would start writing a password.
        Assert.NotEqual(PasswordBox.ToOptional(null), PasswordBox.ToOptional("New-Passw0rd"));
        Assert.NotEqual(PasswordBox.ToOptional(string.Empty), Optional<string?>.Present(null));
    }
}
