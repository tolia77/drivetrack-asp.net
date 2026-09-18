using System.Net;
using System.Text.RegularExpressions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Users;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Components.Account;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// FR-87, FR-88 and FR-92 as the one screen a user actually reaches them through.
/// <para>
/// Rendered rather than read as source, because no arrangement of words can tell a screen that
/// offers a client their phone number from one that offers a dispatcher a box with nowhere to store
/// what they type. Statically, so the assertions are about the first pass a browser would receive;
/// the one decision a click would exercise is lifted into <see cref="ProfileEdit"/> and asserted
/// directly.
/// </para>
/// </summary>
public class ProfileScreenTests
{
    /// <summary>The heading row: the screen's title, and nothing else this screen puts beside it.</summary>
    private static readonly Regex PageHead = new(
        @"<div\b[^>]*class=""[^""]*\bdt-page-head\b[^""]*""[^>]*>(?<body>.*?)</div>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task The_screen_carries_its_heading_in_the_shared_page_head_and_nothing_beside_it()
    {
        // <h1> rather than <h2>: `FocusOnNavigate Selector="h1"` in Routes.razor looks for one, and
        // on a page without it a keyboard user keeps the focus the previous screen had. In the
        // shared heading row because the product has one heading shape - this screen has no action
        // of its own to put beside the title and is written the same way all the same, so a reader
        // moving between screens is not shown two different ways of starting a page.
        var html = await RenderAsync(Client());

        var head = PageHead.Match(html);

        Assert.True(head.Success, "The screen has no heading row.");

        var body = head.Groups["body"].Value;

        Assert.Contains("<h1", body, StringComparison.Ordinal);

        // NFR-24: the heading carries the glyph the navigation already uses for this destination.
        // It was the last of the signed-in application screens whose <h1> had none. Outside that
        // set the claim does not hold and is not made here: SignIn, Register and Error still carry
        // a bare `dt-type-h1`, none of them being a destination the navigation names.
        Assert.Contains("<svg", body, StringComparison.Ordinal);

        // And the three things a user can change are not copied up here. They are actions on the
        // record the card holds rather than on the screen, so they stay in that card's head - and a
        // second copy beside the title would be the same control offered twice, which is what the
        // exactly-once count above this would catch and this states the reason for.
        Assert.DoesNotContain("<button", body, StringComparison.Ordinal);

        foreach (var action in new[] { "dt-profile-edit", "dt-profile-password", "dt-profile-email" })
        {
            Assert.DoesNotContain(action, body, StringComparison.Ordinal);
            Assert.Equal(1, SharedMarkup.Occurrences(html, action));
        }
    }

    [Fact]
    public async Task The_screen_offers_an_edit_a_password_change_and_an_address_change()
    {
        // FR-87 / FR-88 / FR-92 are one thing a user came here to do, so they are one screen - but
        // three dialogs, because they are three operations with three different refusals.
        var html = await RenderAsync(Client());

        Assert.Equal(3, SharedMarkup.Occurrences(html, @"class=""dt-dialog"""));
        Assert.Contains(@"aria-modal=""true""", html, StringComparison.Ordinal);

        foreach (var action in new[] { "dt-profile-edit", "dt-profile-password", "dt-profile-email" })
        {
            Assert.Equal(1, SharedMarkup.Occurrences(html, action));
        }

        foreach (var save in new[] { "dt-profile-save", "dt-password-save", "dt-email-save" })
        {
            Assert.Contains(save, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_screen_still_reads_out_the_profile_it_is_about_to_edit()
    {
        // The read-out FR-5 shipped is not replaced by the editing: a user has to see what they are
        // changing, and a driver's licence number is still on the page.
        var html = await RenderAsync(Driver());
        var text = WebUtility.HtmlDecode(html);

        Assert.Contains("Ігор", text, StringComparison.Ordinal);
        Assert.Contains("Ковальчук", text, StringComparison.Ordinal);
        Assert.Contains("ihor@drivetrack.test", text, StringComparison.Ordinal);
        Assert.Contains("AB123456", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_a_client_is_offered_a_phone_number()
    {
        // DR-3 gives no role but a client a row to keep a number in. A box a dispatcher, a driver or
        // an administrator could fill in would be refused on every save, which is worse than not
        // offering it - so the negative half renders all three rather than standing on one.
        //
        // What a static render can say about the box is that it is there. Whether it arrives
        // *filled* is ShowEditAsync's work, and a static render never reaches a click - so the
        // stored number is asserted where this pass really does put it, in the read-only row.
        var client = await RenderAsync(Client());

        Assert.Contains(@"id=""profile-phone""", client, StringComparison.Ordinal);

        var row = SharedMarkup.TextOf(
            WebUtility.HtmlDecode(SharedMarkup.ElementWithClass(client, "dl", "row")));

        Assert.Contains("+380441234567", row, StringComparison.Ordinal);

        foreach (var profile in new[] { Dispatcher(), Driver(), Admin() })
        {
            var html = await RenderAsync(profile);

            Assert.DoesNotContain(@"id=""profile-phone""", html, StringComparison.Ordinal);

            // And no read-out row either: there is nothing to show, so nothing claims there is.
            Assert.DoesNotContain("+380", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task No_password_box_is_ever_pre_filled()
    {
        // FR-8: only a hash is stored, so there is nothing to pre-fill them with - and a refused
        // change must not render the submitted password back either.
        var html = await RenderAsync(Client());

        foreach (var id in new[]
        {
            "profile-current-password",
            "profile-new-password",
            "profile-new-password-confirmation",
        })
        {
            var box = Regex.Match(
                html,
                @"<input[^>]*id=""" + id + @"""[^>]*>",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));

            Assert.True(box.Success, $"The password dialog has no '{id}' field.");
            Assert.Contains(@"type=""password""", box.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("value=", box.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Every_word_the_screen_renders_of_its_own_is_Ukrainian()
    {
        // NFR-14, read back from the rendered page rather than from the source: the heading, the
        // dialog titles, the field labels and every action are the product's words. The read-out
        // values are data - an email address is Latin by nature - so they are not in scope here.
        var html = await RenderAsync(Client());

        var chrome = new List<string>();

        chrome.AddRange(Matches(html, @"<h1[^>]*>(?<body>.*?)</h1>"));
        chrome.AddRange(Matches(html, @"<h2[^>]*dt-dialog-title[^>]*>(?<body>.*?)</h2>"));
        chrome.AddRange(Matches(html, @"<label[^>]*>(?<body>.*?)</label>"));
        chrome.AddRange(Matches(html, @"<dt[^>]*>(?<body>.*?)</dt>"));

        Assert.NotEmpty(chrome);

        foreach (var action in new[]
        {
            "dt-profile-edit",
            "dt-profile-password",
            "dt-profile-email",
            "dt-profile-save",
            "dt-profile-cancel",
            "dt-password-save",
            "dt-password-cancel",
            "dt-email-save",
            "dt-email-cancel",
        })
        {
            var body = SharedMarkup.ElementWithClass(html, "button", action);

            // NFR-24: an action carries a glyph and its text, never one without the other.
            Assert.Contains("<svg", body, StringComparison.Ordinal);

            chrome.Add(body);
        }

        foreach (var fragment in chrome)
        {
            var text = SharedMarkup.TextOf(fragment);

            Assert.True(SharedMarkup.IsUkrainian(text), $"A rendered fragment reads '{text}'.");
            Assert.False(SharedMarkup.HasLatinWord(text), $"A rendered fragment reads '{text}'.");
        }
    }

    [Fact]
    public void The_form_leaves_the_phone_number_absent_for_a_caller_who_has_no_row_for_one()
    {
        // The one decision the screen takes, lifted where a test can reach it. Sending the field as
        // a present null instead would refuse a dispatcher's own rename on every save, and a private
        // method inside a generated component class is reachable by nothing.
        var dispatcher = ProfileEdit.ToProfileCommand("Ігор", "Ковальчук", null, isClient: false);

        Assert.False(dispatcher.PhoneNumber.HasValue);
        Assert.True(dispatcher.FirstName.HasValue);
        Assert.Equal("Ігор", dispatcher.FirstName.Value);

        // And a client's number travels, including the present-null that asks to clear it - which
        // the merged state refuses rather than the screen silently dropping.
        var client = ProfileEdit.ToProfileCommand("Олена", "Петренко", "+380441234567", isClient: true);

        Assert.True(client.PhoneNumber.HasValue);
        Assert.Equal("+380441234567", client.PhoneNumber.Value);

        var cleared = ProfileEdit.ToProfileCommand("Олена", "Петренко", null, isClient: true);

        Assert.True(cleared.PhoneNumber.HasValue);
        Assert.Null(cleared.PhoneNumber.Value);
    }

    [Fact]
    public void The_edit_form_opens_carrying_the_stored_profile()
    {
        // The other half of the same lift, and the half nothing observed until now. AD-23 makes the
        // command say what the form shows, so a box the user leaves as they found it has to send the
        // value that was already in it. Drop a line from Fill and every self-service save sends a
        // present-null name, which ProfileStateValidator refuses 422 - while the ToProfileCommand
        // test above, handed string literals rather than anything a form produced, carries on
        // passing. A static render cannot reach the click that calls this, so this is where it is
        // asserted instead.
        var form = new ProfileEditForm();

        form.Fill(Client());

        Assert.Equal("Олена", form.FirstName, StringComparer.Ordinal);
        Assert.Equal("Петренко", form.LastName, StringComparer.Ordinal);
        Assert.Equal("+380441234567", form.PhoneNumber, StringComparer.Ordinal);

        // Never pre-filled, by the same rule the render asserts about the markup (FR-8).
        Assert.Null(form.CurrentPassword);
        Assert.Null(form.NewPassword);
        Assert.Null(form.NewPasswordConfirmation);

        // A driver has no client row, so the phone travels as the null it is rather than as a stale
        // number from whoever the form was filled from last.
        form.Fill(Dispatcher());

        Assert.Null(form.PhoneNumber);
    }

    [Fact]
    public void The_screen_takes_no_authorization_decision_of_its_own()
    {
        // AD-2 / FR-12: RequireSelf inside IUserService is the only place a request is refused, and
        // it is what gives an administrator the client's half of FR-92. A role on the page attribute
        // or an <AuthorizeView> here would be a second copy of that rule, worded differently, that
        // no test of the rule would ever read.
        var screen = SharedMarkup.ReadComponent("Account", "Profile.razor");

        Assert.Contains("@attribute [Authorize]", screen, StringComparison.Ordinal);

        var markup = Regex.Replace(
            screen, @"@\*.*?\*@", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("<AuthorizeView", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("[Authorize(Roles", markup, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Matches(string html, string pattern) =>
        Regex.Matches(html, pattern, RegexOptions.Singleline, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["body"].Value);

    private static UserProfile Client() =>
        new(
            new UserId(11),
            "Олена",
            "Петренко",
            "olena@drivetrack.test",
            UserRole.Client,
            LicenseNumber: null,
            PhoneNumber: "+380441234567");

    private static UserProfile Dispatcher() =>
        new(
            new UserId(21),
            "Ігор",
            "Ковальчук",
            "ihor@drivetrack.test",
            UserRole.Dispatcher,
            LicenseNumber: null,
            PhoneNumber: null);

    private static UserProfile Admin() =>
        new(
            new UserId(41),
            "Марія",
            "Коваль",
            "maria@drivetrack.test",
            UserRole.Admin,
            LicenseNumber: null,
            PhoneNumber: null);

    private static UserProfile Driver() =>
        new(
            new UserId(31),
            "Ігор",
            "Ковальчук",
            "ihor@drivetrack.test",
            UserRole.Driver,
            LicenseNumber: "AB123456",
            PhoneNumber: null);

    private static Task<string> RenderAsync(UserProfile profile) =>
        ComponentRenderer.RenderAsync<Web.Components.Account.Profile>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ICurrentUser>(new StubCaller(profile));
                services.AddSingleton<IUserService>(new StubUsers(profile));
            });

    /// <summary>A signed-in caller matching the profile under render, and nothing else.</summary>
    private sealed class StubCaller(UserProfile profile) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public UserId UserId => profile.UserId;

        public UserRole Role => profile.Role;

        public DriverId? DriverId => null;

        public ClientId? ClientId => null;
    }

    /// <summary>
    /// One profile, answered without a database. Every write throws: a static render never reaches
    /// one, so a screen that called it during rendering would fail loudly rather than pass quietly.
    /// </summary>
    private sealed class StubUsers(UserProfile profile) : IUserService
    {
        public Task<UserProfile> GetProfileAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(profile);

        public Task<AuthenticatedSession> RegisterClientAsync(
            RegisterClientCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A rendered profile does not open an account.");

        public Task<AuthenticatedSession> SignInAsync(
            SignInCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A rendered profile does not sign anybody in.");

        public Task<UserProfile> UpdateProfileAsync(
            UserId userId,
            UpdateProfileCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task ChangePasswordAsync(
            UserId userId,
            ChangePasswordCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<UserProfile> ChangeEmailAsync(
            UserId userId,
            ChangeEmailCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");
    }
}
