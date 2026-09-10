using System.Net;
using System.Text.RegularExpressions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Users;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
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
        // The list is a pin rather than a limit, and story 5.1 moves it deliberately. AD-3 promised
        // a scope predicate and IAccessGuard's own documentation said it would arrive "with the
        // first capability that has a caller for it"; deliveries are that caller, because a driver
        // seeing only their own rows is an authorization decision and a repository Where written
        // inside a service would be a second place authorization lived. Adding a fourth member has
        // to be an edit to this line for the same reason adding the third was.
        var members = typeof(IAccessGuard)
            .GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["RequireRole", "RequireScope", "RequireSelf"], members);
    }

    private static string WithoutComments(string source) =>
        Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

    private static IEnumerable<string> Matches(string html, string pattern) =>
        Regex.Matches(html, pattern, RegexOptions.Singleline, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["body"].Value);

    private static Task<string> RenderClientsAsync(UserRole role = UserRole.Admin) =>
        RenderAsync<Web.Components.Pages.Admin.Clients>(
            role,
            services => services.AddSingleton<IClientAdministrationService>(new StubClients()));

    private static Task<string> RenderDispatchersAsync() =>
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
