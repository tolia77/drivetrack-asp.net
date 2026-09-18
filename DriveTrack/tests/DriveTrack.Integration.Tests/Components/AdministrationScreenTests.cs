using System.Net;
using System.Text.RegularExpressions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Users;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Components.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// FR-81 / FR-82 / FR-85 for the two administration screens: each renders its roster through the
/// shared table, opens the shared dialog to edit and the shared confirmation to delete, and says all
/// of it in Ukrainian with an icon beside every action.
/// <para>
/// Rendered rather than read as source, because no arrangement of words can tell a screen that shows
/// its rows from one that shows a blank rectangle — which is precisely what the baseline's lists did
/// while loading and while empty.
/// </para>
/// </summary>
public class AdministrationScreenTests
{
    private static readonly ClientAccount[] Clients =
    [
        new(new UserId(11), new ClientId(1), "Олена", "Петренко", "olena@drivetrack.test", "+380441234567"),
        new(new UserId(12), new ClientId(2), "Марія", "Коваль", "maria@drivetrack.test", "+380509876543"),
    ];

    private static readonly DispatcherAccount[] Dispatchers =
    [
        new(new UserId(21), "Ігор", "Ковальчук", "ihor@drivetrack.test"),
        new(new UserId(22), "Богдан", "Мельник", "bohdan@drivetrack.test"),
    ];

    /// <summary>The heading row: the screen's title, and any action that is about the roster.</summary>
    private static readonly Regex PageHead = new(
        @"<div\b[^>]*class=""[^""]*\bdt-page-head\b[^""]*""[^>]*>(?<body>.*?)</div>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A column heading.</summary>
    private static readonly Regex HeaderCell = new(
        @"<th\b[^>]*>(?<body>.*?)</th>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>The actions heading, which keeps its name without printing it.</summary>
    private static readonly Regex ActionsHeader = new(
        @"<th\b[^>]*class=""[^""]*\bdt-table-actions\b[^""]*""[^>]*>(?<body>.*?)</th>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// One body row, so a claim can be made about every cell in every row rather than about the
    /// page as a whole - which a single missing label would satisfy.
    /// </summary>
    private static readonly Regex BodyRow = new(
        @"<tr\b[^>]*class=""[^""]*\bdt-table-row\b[^""]*""[^>]*>(?<body>.*?)</tr>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>One cell of a row, so a claim can be made cell by cell rather than row by row.</summary>
    private static readonly Regex Cell = new(
        @"<td\b[^>]*>(?<body>.*?)</td>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>The row's actions cell.</summary>
    private static readonly Regex ActionsCell = new(
        @"<td\b[^>]*class=""[^""]*\bdt-table-actions\b[^""]*""[^>]*>(?<body>.*?)</td>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task The_client_screen_puts_every_client_in_the_shared_table()
    {
        var html = await RenderClientsAsync();

        // FR-82: the shared table, in its rows state - not a spinner and not the empty row.
        Assert.Contains("dt-table-scroll", html, StringComparison.Ordinal);
        Assert.Equal(Clients.Length, SharedMarkup.Occurrences(html, "dt-table-row"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-status"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-empty"));

        // FR-46: name, email and phone number, for every client. Decoded first, because the
        // encoder writes an E.164 number's leading plus as a character reference.
        var text = WebUtility.HtmlDecode(html);

        foreach (var account in Clients)
        {
            Assert.Contains(account.FirstName, text, StringComparison.Ordinal);
            Assert.Contains(account.LastName, text, StringComparison.Ordinal);
            Assert.Contains(account.Email, text, StringComparison.Ordinal);
            Assert.Contains(account.PhoneNumber, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_client_screen_offers_an_edit_and_a_delete_on_every_row_with_an_icon_each()
    {
        // NFR-24: an action carries a glyph and its text, never one without the other.
        var html = await RenderClientsAsync();

        Assert.Equal(Clients.Length, SharedMarkup.Occurrences(html, "dt-client-edit"));
        Assert.Equal(Clients.Length, SharedMarkup.Occurrences(html, "dt-client-delete"));

        foreach (var action in new[] { "dt-client-edit", "dt-client-delete" })
        {
            var body = SharedMarkup.ElementWithClass(html, "button", action);
            var text = SharedMarkup.TextOf(body);

            Assert.Contains("<svg", body, StringComparison.Ordinal);
            Assert.True(SharedMarkup.IsUkrainian(text), $"The '{action}' action reads '{text}'.");
            Assert.False(SharedMarkup.HasLatinWord(text), $"The '{action}' action reads '{text}'.");
        }
    }

    [Fact]
    public async Task A_dispatcher_reading_the_roster_is_offered_no_action_that_could_only_fail()
    {
        // FR-48 puts a dispatcher on this screen to read it, and both writes behind the row actions
        // are RequireRole(Admin). Rendering buttons that answer 403 on every click is worse than
        // rendering none.
        //
        // FR-12 unchanged, and worth restating because the test cannot: this asserts what is shown,
        // never who may act. IAccessGuard inside the service still refuses the write, and it runs
        // whether or not the button was ever rendered.
        var html = await RenderClientsAsync(UserRole.Dispatcher);

        // The roster itself is still there - the whole reason a dispatcher opened the screen.
        Assert.Equal(Clients.Length, SharedMarkup.Occurrences(html, "dt-table-row"));
        Assert.Contains(Clients[0].Email, html, StringComparison.Ordinal);

        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-client-edit"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-client-delete"));
    }

    [Fact]
    public async Task The_client_screen_edits_through_the_shared_dialog_and_never_pre_fills_a_password()
    {
        // FR-85, and FR-8: only a hash is stored, so there is nothing to pre-fill the box with -
        // and an empty box is exactly how AD-23's absent case is expressed on a form.
        var html = await RenderClientsAsync();

        Assert.Contains(@"class=""dt-dialog""", html, StringComparison.Ordinal);
        Assert.Contains(@"aria-modal=""true""", html, StringComparison.Ordinal);
        Assert.Contains("dt-client-save", html, StringComparison.Ordinal);

        var password = Regex.Match(
            html,
            @"<input[^>]*id=""client-password""[^>]*>",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(password.Success, "The edit dialog has no password field.");
        Assert.Contains(@"type=""password""", password.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("value=", password.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_client_edit_dialog_carries_the_address_an_administrator_may_change()
    {
        // FR-92's administrator half, and the only route an administrator has to it: the service
        // accepts an email on UpdateClientCommand, but without this box nobody can send one. Asserted
        // as a render rather than as source, for the same reason the password field is.
        var html = await RenderClientsAsync();

        var email = Regex.Match(
            html,
            @"<input[^>]*id=""client-email""[^>]*>",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(email.Success, "The edit dialog has no email field.");
        Assert.Contains(@"type=""email""", email.Value, StringComparison.Ordinal);

        // Whether the box is *filled* is not a claim this render can make: ShowEditAsync populates
        // the form, and a static render never reaches a click. What it can say is that the field the
        // administrator needs is on the page at all. The filling is asserted below instead.
    }

    [Fact]
    public void The_client_edit_form_opens_carrying_the_row_and_sends_every_field_back()
    {
        // The claim the render above cannot make. AD-23 makes the command say what the form shows,
        // so a box the administrator leaves as they found it has to send the value already in it -
        // and FR-92 gave the address a rule, so an unfilled box is refused 422 rather than read as
        // "unchanged". Before this form was lifted out of the component, deleting the address line
        // turned every administrator edit of a client into a 422 with the suite still green.
        var form = new ClientEditForm();

        form.Fill(Clients[0]);

        Assert.Equal("Олена", form.FirstName, StringComparer.Ordinal);
        Assert.Equal("Петренко", form.LastName, StringComparer.Ordinal);
        Assert.Equal("olena@drivetrack.test", form.Email, StringComparer.Ordinal);
        Assert.Equal("+380441234567", form.PhoneNumber, StringComparer.Ordinal);

        // Never pre-filled: only a hash is stored (FR-8), and empty is how AD-23's absent case is
        // expressed on a form.
        Assert.Null(form.Password);

        var command = form.ToCommand();

        Assert.True(command.Email.HasValue);
        Assert.Equal("olena@drivetrack.test", command.Email.Value, StringComparer.Ordinal);
        Assert.True(command.FirstName.HasValue);
        Assert.Equal("Олена", command.FirstName.Value, StringComparer.Ordinal);
        Assert.True(command.PhoneNumber.HasValue);
        Assert.Equal("+380441234567", command.PhoneNumber.Value, StringComparer.Ordinal);

        // The untouched password box, which must reach the service as absent - a present null here
        // would be an administrator's every edit clearing the account's password.
        Assert.False(command.Password.HasValue);

        // And a typed one travels, so the two Optional<string?> fields at the end of the command are
        // pinned to their own values rather than to each other's.
        form.Password = "Новий-Пароль-1";

        var withPassword = form.ToCommand();

        Assert.True(withPassword.Password.HasValue);
        Assert.Equal("Новий-Пароль-1", withPassword.Password.Value, StringComparer.Ordinal);
        Assert.Equal("olena@drivetrack.test", withPassword.Email.Value, StringComparer.Ordinal);
    }

    [Fact]
    public async Task The_client_screen_confirms_a_deletion_before_it_happens()
    {
        // FR-81. The baseline deleted the moment the link was clicked; the shared confirmation is
        // what makes that impossible to forget on a new screen.
        var html = await RenderClientsAsync();

        Assert.Contains("dt-confirm-accept", html, StringComparison.Ordinal);
        Assert.Contains("dt-confirm-cancel", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dispatcher_screen_lists_creates_edits_and_deletes()
    {
        // FR-49 and FR-50 in one render: the roster, plus the create action this story adds and the
        // two the client screen also has.
        var html = await RenderDispatchersAsync();

        Assert.Equal(Dispatchers.Length, SharedMarkup.Occurrences(html, "dt-table-row"));

        foreach (var account in Dispatchers)
        {
            Assert.Contains(account.FirstName, html, StringComparison.Ordinal);
            Assert.Contains(account.Email, html, StringComparison.Ordinal);
        }

        Assert.Contains("dt-dispatcher-new", html, StringComparison.Ordinal);
        Assert.Contains("dt-dispatcher-create", html, StringComparison.Ordinal);
        Assert.Equal(Dispatchers.Length, SharedMarkup.Occurrences(html, "dt-dispatcher-edit"));
        Assert.Equal(Dispatchers.Length, SharedMarkup.Occurrences(html, "dt-dispatcher-delete"));
        Assert.Contains("dt-confirm-accept", html, StringComparison.Ordinal);

        // Three dialogs: create, edit and the confirmation, each its own modal.
        Assert.Equal(3, SharedMarkup.Occurrences(html, @"class=""dt-dialog"""));
    }

    [Fact]
    public async Task The_dispatcher_creation_dialog_asks_for_the_address_and_the_edit_dialog_does_not()
    {
        // FR-87 is story 7.3's: an address is settled once, at creation. A second field on the edit
        // form would be that story arriving early and half-done.
        var html = await RenderDispatchersAsync();

        Assert.Contains(@"id=""new-dispatcher-email""", html, StringComparison.Ordinal);
        Assert.Contains(@"id=""dispatcher-password""", html, StringComparison.Ordinal);
        Assert.DoesNotContain(@"id=""dispatcher-email""", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_word_a_screen_renders_of_its_own_is_Ukrainian(bool clients)
    {
        // NFR-14, read back from the rendered page rather than from the source: the table's headings,
        // its dialog titles and its actions are the product's words, and every one of them has to
        // arrive translated. The rows themselves are data - an email address is Latin by nature -
        // so they are deliberately not in scope here.
        var html = clients ? await RenderClientsAsync() : await RenderDispatchersAsync();

        var chrome = new List<string> { SharedMarkup.ElementWithClass(html, "thead", "dt-table-head") };

        chrome.AddRange(Matches(html, @"<h1[^>]*>(?<body>.*?)</h1>"));
        chrome.AddRange(Matches(html, @"<h2[^>]*dt-dialog-title[^>]*>(?<body>.*?)</h2>"));
        chrome.AddRange(Matches(html, @"<label[^>]*>(?<body>.*?)</label>"));
        chrome.AddRange(Matches(html, @"<div[^>]*form-text[^>]*>(?<body>.*?)</div>"));

        Assert.NotEmpty(chrome);

        foreach (var fragment in chrome)
        {
            var text = SharedMarkup.TextOf(fragment);

            Assert.True(SharedMarkup.IsUkrainian(text), $"A rendered fragment reads '{text}'.");
            Assert.False(SharedMarkup.HasLatinWord(text), $"A rendered fragment reads '{text}'.");
        }
    }

    [Fact]
    public void Neither_screen_takes_an_authorization_decision_of_its_own()
    {
        // The acceptance criterion, as a test: IAccessGuard.RequireRole inside the service is the
        // only place a request is *refused* for its role. A role on the page attribute or an
        // <AuthorizeView> here would be a second copy of that rule, worded differently, that no
        // test of the rule would ever read.
        //
        // What this does not pin, and cannot: Clients.razor reads ICurrentUser.Role in C# to hide
        // buttons a dispatcher's write would only fail on. That is a rendering decision the guard
        // still overrules, and it is invisible to a source scan by construction - so read this as
        // "no authorization construct in the markup", not as "no mention of a role anywhere".
        foreach (var name in new[] { "Clients.razor", "Dispatchers.razor" })
        {
            var screen = SharedMarkup.ReadComponent("Pages", "Admin", name);

            Assert.Contains("@attribute [Authorize]", screen, StringComparison.Ordinal);

            // Matched as constructs rather than as bare words, and read past the comments the same
            // way the sweep below does. A test that failed the build because a later comment used
            // the word "roles" would be reporting its own phrasing as a defect.
            var markup = WithoutComments(screen);

            Assert.DoesNotContain("<AuthorizeView", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("[Authorize(Roles", markup, StringComparison.Ordinal);
        }

        // And no component anywhere reintroduced the pattern. Razor comments are stripped first:
        // Home.razor explains in prose why it uses ICurrentUser instead, and an explanation of a
        // rule is not a breach of it.
        var offenders = Directory
            .GetFiles(SharedMarkup.ComponentsDirectory, "*.razor", SearchOption.AllDirectories)
            .Where(path => WithoutComments(File.ReadAllText(path))
                .Contains("<AuthorizeView", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Neither_controller_states_a_role_of_its_own()
    {
        // The same claim on the REST side. [Authorize] with no roles stops an anonymous request one
        // hop early; anything more would be the decision, in the wrong place.
        var api = Path.Combine(RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Api");

        var offenders = Directory
            .GetFiles(api, "*Controller.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var source = File.ReadAllText(path);

                return source.Contains("[Authorize(Roles", StringComparison.Ordinal)
                    || source.Contains("Policy =", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_guard_still_has_exactly_the_decisions_the_system_makes()
    {
        // NFR-6, and the readability half of the acceptance criterion: a reviewer asking "who may do
        // what" has these methods to read, not a scattering of attributes.
        //
        // The list is a pin rather than a limit, and each story that moves it has to argue for the
        // move here. Story 5.1 added RequireScope, because a driver seeing only their own rows is an
        // authorization decision and a repository Where written inside a service would be a second
        // place authorization lived. Story 5.3 adds RequireAssignedDriver for the same kind of
        // reason and not a weaker one: "the driver carrying this parcel, or dispatch, and never the
        // client whose delivery it is" (FR-34, FR-90) is a predicate none of the other three can
        // express - a client's scope admits their own delivery, so RequireScope would let them
        // change its status, and a role test inside the service would be authorization living
        // somewhere else again.
        //
        // Story 8.1 adds RequireChatParticipant, and the argument for it is the strongest of the
        // three: it is the one question in the system whose answer contradicts AD-4. The PRD locks
        // administrators out of chat as a product decision, so RequireRole(Dispatcher) - which
        // reads as "a dispatcher or an admin" by design - is not merely awkward here but wrong, and
        // RequireSelf cannot express it either because the thread key is a driver row id and a
        // dispatcher who owns no conversation must still pass. One member with a nullable argument
        // also answers "may this caller see the roster at all", because a null thread never equals
        // a driver's row id - two questions, one rule, one place to read it.
        //
        // Story 7.4 adds RequireDeliveryComposer, and its argument is the same shape: "may this
        // caller compose a delivery, and for which client row" is a question none of the five can
        // answer. RequireRole(Client) reads as "a client or an admin" by AD-4's design and an admin
        // carries no client row id, so a service using it would have to decide what a missing id
        // means - authorization living somewhere else again. RequireScope hands a driver a scope and
        // so cannot refuse one, and its answer is about which rows may be read rather than who may
        // open a new one. It serves two call sites, which is the test of a question rather than a
        // convenience: the request path and FR-104's address search behind the same form.
        //
        // Story 7.2 adds two, and they are a pair rather than two independent additions - which is
        // itself the argument, because the split between them is the product decision.
        // RequireReviewAuthor answers "may this caller write reviews, and as which client row": the
        // PRD retired FR-97, so an administrator authoring customer feedback is manufacturing it
        // rather than moderating it, and RequireRole(Client) - which reads as "a client or an
        // admin" by AD-4's design - is not merely awkward here but wrong. It is the second member
        // whose answer deliberately contradicts AD-4, and, as with RequireDeliveryComposer, an
        // admin carries no client row id for a service to fall back on. It serves two call sites:
        // authoring, and the author's own list, where the answer is also the scope the list is
        // narrowed by (AD-3).
        //
        // RequireReviewOwner answers "may this caller change *this* review" - its author, or an
        // admin moderating, and never a dispatcher. RequireSelf cannot express it, because a
        // review's owner is a ClientId rather than a UserId and AD-22 keeps the two from being
        // compared; RequireScope cannot, because it hands a dispatcher the unrestricted scope and
        // so cannot refuse one. Its nullable argument follows RequireAssignedDriver: a missing
        // review reaches the guard as a null that never matches, so it is refused rather than
        // handed to whoever asked for it.
        //
        // Story 4.2 adds two more, and they are a pair for the same kind of reason. RequireShiftScope
        // answers "whose shifts may this caller be shown", and it is the one addition where reusing
        // RequireScope would be a disclosure rather than a wording problem: a client's scope is
        // (null, clientId), whose driver half is null - which a shift query reads as "unrestricted
        // by driver" and answers with every shift in the system, when FR-115 gives a client no part
        // in shifts at all.
        //
        // RequireShiftOwner answers "may this caller act on *this* driver's shift". It is the one
        // member that could have been left out: RequireAssignedDriver already answers "this driver,
        // or dispatch, never a client" and would behave correctly. Its message says *delivery
        // lifecycle*, though, so every refused shift request would be logged as a delivery - and
        // RequireReviewOwner beside RequireReviewAuthor is the standing precedent for a
        // capability-specific member with the same structure. Its nullable argument follows
        // RequireAssignedDriver's for the same reason as the review one's.
        var members = typeof(IAccessGuard)
            .GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "RequireAssignedDriver",
                "RequireChatParticipant",
                "RequireDeliveryComposer",
                "RequireReviewAuthor",
                "RequireReviewOwner",
                "RequireRole",
                "RequireScope",
                "RequireSelf",
                "RequireShiftOwner",
                "RequireShiftScope",
            ],
            members);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Each_screen_carries_its_heading_in_the_shared_page_head(bool clients)
    {
        // <h1> rather than <h2>: `FocusOnNavigate Selector="h1"` in Routes.razor looks for one, and
        // on a page without it a keyboard user keeps the focus the previous screen had. In the
        // shared heading row because the product has one heading shape - the roster screen's head
        // holds nothing beside the title and is written all the same, so a reader moving between
        // screens is not shown two different ways of starting a page.
        var html = clients ? await RenderClientsAsync() : await RenderDispatchersAsync();

        var head = PageHead.Match(html);

        Assert.True(head.Success, "The screen has no heading row.");

        var body = head.Groups["body"].Value;

        Assert.Contains("<h1", body, StringComparison.Ordinal);

        // NFR-24: the heading carries the glyph the navigation already uses for this destination,
        // as every other screen's does.
        Assert.Contains("<svg", body, StringComparison.Ordinal);

        if (clients)
        {
            // Nothing beside it: every action on the roster is about one client and lives in that
            // client's row. A create action here would be one this screen does not have - FR-46
            // gives an administrator no way to open a client account.
            Assert.DoesNotContain("<button", body, StringComparison.Ordinal);

            return;
        }

        // And the dispatcher screen's create action is beside the heading rather than loose in a
        // paragraph above the table: it is about the roster the heading names. Found by its hook
        // and only then asserted about by its colour, because `btn btn-primary` is what a create
        // action looks like rather than what one is.
        //
        // `dt-dispatcher-new` rather than `-create`: the create dialog's own confirm button
        // already holds that name on this screen, and both are hooks the suite matches by.
        Assert.Contains("dt-dispatcher-new", body, StringComparison.Ordinal);
        Assert.Contains("btn-primary", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "dt-client-edit", "dt-client-delete")]
    [InlineData(false, "dt-dispatcher-edit", "dt-dispatcher-delete")]
    public async Task Every_row_action_sits_in_the_shared_actions_cell(
        bool clients,
        string edit,
        string delete)
    {
        // DtDataTable right-aligns the trailing cell through `.dt-table-row ::deep
        // td.dt-table-actions`, so a cell that does not wear the class its own heading wears is a
        // cell the shared rule cannot reach. Both screens used to mark it `dt-row-actions` and
        // style it themselves as a flex row - which ignores `text-align` outright, so the move and
        // the deletion of that rule are one change rather than two.
        var html = clients ? await RenderClientsAsync() : await RenderDispatchersAsync();

        var cells = ActionsCell.Matches(html)
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        // Before the count, because `0 == 0` is what a screen that rendered no rows at all would
        // answer - and the loop below would then assert nothing.
        Assert.NotEmpty(cells);

        Assert.Equal(SharedMarkup.Occurrences(html, "dt-table-row"), cells.Length);

        foreach (var cell in cells)
        {
            Assert.Contains(edit, cell, StringComparison.Ordinal);
            Assert.Contains(delete, cell, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("dt-row-actions", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dispatcher_reading_the_roster_gets_an_actions_cell_with_nothing_in_it()
    {
        // The other half of FR-48's hiding, and the half a phone makes visible. The per-cell name
        // is what replaces the dropped column heading below the breakpoint, so a label written
        // outside the `@if` that hides the buttons would give a dispatcher a card with a heading
        // reading "Дії" and nothing at all underneath it - a row promising an action it does not
        // offer, which is the outcome hiding the buttons exists to avoid.
        var html = await RenderClientsAsync(UserRole.Dispatcher);

        var cells = ActionsCell.Matches(html)
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        // The cell is still rendered: a row one cell short of its heading row is a table whose
        // columns no longer line up.
        Assert.NotEmpty(cells);
        Assert.Equal(SharedMarkup.Occurrences(html, "dt-table-row"), cells.Length);

        foreach (var cell in cells)
        {
            Assert.Equal(string.Empty, SharedMarkup.TextOf(cell));
            Assert.DoesNotContain("dt-client-label", cell, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true, "dt-client-label")]
    [InlineData(false, "dt-dispatcher-label")]
    public async Task Every_cell_carries_its_own_name_for_the_phone_layout(bool clients, string label)
    {
        // Below the phone breakpoint the head is dropped rather than hidden: a table laid out as
        // blocks is no longer a table to a screen reader - the implicit table, row and cell roles
        // go with the `display` they came from - so a `<thead>` left in place would name columns
        // that no longer exist to be announced under. The label inside each cell is what replaces
        // it, and that only works if every cell has one.
        //
        // Cell by cell rather than by counting labels against cells across the row: two labels in
        // one cell and none in the next is the same total and a card with an unnamed line in it.
        // And the label is checked against the heading of the column it is in, because a label
        // that exists and reads wrongly names the value rather than failing to name it.
        //
        // An administrator, on both screens: a dispatcher's actions cell is deliberately empty,
        // which the test above asserts on its own terms.
        var html = clients ? await RenderClientsAsync() : await RenderDispatchersAsync();

        var columns = HeaderCell.Matches(html)
            .Select(match => SharedMarkup.TextOf(match.Groups["body"].Value))
            .ToArray();

        Assert.NotEmpty(columns);

        var rows = BodyRow.Matches(html)
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        Assert.NotEmpty(rows);

        var span = LabelSpan(label);

        foreach (var row in rows)
        {
            var cells = Cell.Matches(row)
                .Select(match => match.Groups["body"].Value)
                .ToArray();

            Assert.Equal(columns.Length, cells.Length);

            for (var index = 0; index < cells.Length; index++)
            {
                var names = span.Matches(cells[index])
                    .Select(match => SharedMarkup.TextOf(match.Groups["body"].Value))
                    .ToArray();

                Assert.Equal(new[] { columns[index] }, names);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_actions_column_keeps_its_name_without_printing_it(bool clients)
    {
        // The buttons under it say what they do, so a column name printed above them competes with
        // them for the same width - and a column with no name at all leaves a screen reader moving
        // by cell with one unlabelled column. Hidden, not dropped, as on every other table here.
        var html = clients ? await RenderClientsAsync() : await RenderDispatchersAsync();

        var actions = ActionsHeader.Match(html);

        Assert.True(actions.Success, "The table has no actions heading.");
        Assert.Contains("dt-visually-hidden", actions.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("Дії", actions.Groups["body"].Value, StringComparison.Ordinal);
    }

    /// <summary>The per-cell name span, matched by the class the screen marks it with.</summary>
    private static Regex LabelSpan(string label) => new(
        $@"<span\b[^>]*class=""[^""]*\b{Regex.Escape(label)}\b[^""]*""[^>]*>(?<body>.*?)</span>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static string WithoutComments(string source) =>
        Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

    private static IEnumerable<string> Matches(string html, string pattern) =>
        Regex.Matches(html, pattern, RegexOptions.Singleline, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["body"].Value);

    internal static Task<string> RenderClientsAsync(UserRole role = UserRole.Admin) =>
        RenderAsync<Web.Components.Pages.Admin.Clients>(
            role,
            services => services.AddSingleton<IClientAdministrationService>(new StubClients()));

    internal static Task<string> RenderDispatchersAsync() =>
        RenderAsync<Web.Components.Pages.Admin.Dispatchers>(
            UserRole.Admin,
            services => services.AddSingleton<IDispatcherAdministrationService>(new StubDispatchers()));

    private static Task<string> RenderAsync<TComponent>(
        UserRole role,
        Action<IServiceCollection> roster)
        where TComponent : IComponent =>
        ComponentRenderer.RenderAsync<TComponent>(
            parameters: null,
            configureServices: services =>
            {
                // The caller's role, because FR-48 puts two different roles on the client screen and
                // they are offered different actions. The guard itself is not exercised here - it
                // lives in the service, and the service is stubbed - which is the point: what the
                // screen does with the role is hiding, not deciding.
                services.AddSingleton<ICurrentUser>(new StubCaller(role));

                roster(services);
            });

    /// <summary>A signed-in caller of a chosen role, and nothing else the screens read.</summary>
    private sealed class StubCaller(UserRole role) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public UserId UserId => new(1);

        public UserRole Role => role;

        public DriverId? DriverId => null;

        public ClientId? ClientId => null;
    }

    /// <summary>
    /// The roster, answered without a database. Every write throws: a static render never reaches
    /// one, so a screen that called it during rendering would fail loudly rather than pass quietly.
    /// </summary>
    private sealed class StubClients : IClientAdministrationService
    {
        public Task<IReadOnlyList<ClientAccount>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClientAccount>>(Clients);

        public Task<ClientAccount> GetAsync(UserId userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A rendered screen does not read one client.");

        public Task<ClientAccount> UpdateAsync(
            UserId userId,
            UpdateClientCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task DeleteAsync(UserId userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");
    }

    /// <inheritdoc cref="StubClients" />
    private sealed class StubDispatchers : IDispatcherAdministrationService
    {
        public Task<DispatcherAccount> CreateAsync(
            CreateDispatcherCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<IReadOnlyList<DispatcherAccount>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DispatcherAccount>>(Dispatchers);

        public Task<DispatcherAccount> GetAsync(UserId userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A rendered screen does not read one dispatcher.");

        public Task<DispatcherAccount> UpdateAsync(
            UserId userId,
            UpdateDispatcherCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task DeleteAsync(UserId userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");
    }
}
