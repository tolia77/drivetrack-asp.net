using System.Net;
using System.Text.RegularExpressions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Notifications;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// FR-28's screen, rendered.
/// <para>
/// Rendered rather than read as source, because no arrangement of words can tell a table that shows
/// its rows from one that shows a blank rectangle — which is exactly what the baseline's lists did
/// while loading and while empty (FR-82, NFR-22). The three states are asserted one apiece, and the
/// loading one is read off the component's first render pass, which is the only moment it exists.
/// </para>
/// </summary>
public class NotificationScreenTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly NotificationAttemptSummary[] Attempts =
    [
        new(
            2,
            41,
            NotificationKind.StatusChange,
            "olena@drivetrack.test",
            Noon,
            NotificationOutcome.Failed,
            "Не вдалося підключитися до поштового сервера."),
        new(
            1,
            40,
            NotificationKind.StatusChange,
            "maria@drivetrack.test",
            Noon,
            NotificationOutcome.Sent,
            null),
    ];

    /// <summary>The heading row: the screen's title, and nothing else this screen has to offer.</summary>
    private static readonly Regex PageHead = new(
        @"<div\b[^>]*class=""[^""]*\bdt-page-head\b[^""]*""[^>]*>(?<body>.*?)</div>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A column heading.</summary>
    private static readonly Regex HeaderCell = new(
        @"<th\b[^>]*>(?<body>.*?)</th>",
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

    /// <summary>The per-cell name span, matched by the class this screen marks it with.</summary>
    private static readonly Regex LabelSpan = new(
        @"<span\b[^>]*class=""[^""]*\bdt-notification-label\b[^""]*""[^>]*>(?<body>.*?)</span>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Every_attempt_reaches_the_shared_table_with_its_outcome_and_its_error()
    {
        // FR-28's whole point: the row an administrator can read, and enough of it to act on. A
        // screen that showed the recipient and swallowed the reason would be the original's log
        // line with a table around it.
        var html = await RenderAsync(Attempts);

        Assert.Contains("dt-table-scroll", html, StringComparison.Ordinal);
        Assert.Equal(Attempts.Length, SharedMarkup.Occurrences(html, "dt-table-row"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-status"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-empty"));

        var text = WebUtility.HtmlDecode(html);

        foreach (var attempt in Attempts)
        {
            Assert.Contains(attempt.Recipient, text, StringComparison.Ordinal);
        }

        Assert.Contains(Attempts[0].Error!, text, StringComparison.Ordinal);

        // Drawn in the order the log handed them, which is the whole of what newest-first buys an
        // administrator: the repository orders the page and this screen adds no sort of its own, so
        // the newest attempt is the first row. The sibling dispatch board does re-sort what it
        // fetches, so "the table draws the list it was given" is a property of this screen rather
        // than of the shared table, and nothing else here would notice a sort arriving.
        Assert.True(
            text.IndexOf(Attempts[0].Recipient, StringComparison.Ordinal)
                < text.IndexOf(Attempts[1].Recipient, StringComparison.Ordinal),
            "The newest attempt is not the first row on the screen.");

        // One of each outcome, each as a word rather than as the enum member the column holds
        // (AD-18, AD-21). The member names are the thing that must not be on the screen.
        Assert.Equal(1, SharedMarkup.Occurrences(html, "dt-notification-sent"));
        Assert.Equal(1, SharedMarkup.Occurrences(html, "dt-notification-failed"));

        Assert.DoesNotContain(nameof(NotificationOutcome.Sent), text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(NotificationOutcome.Failed), text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(NotificationKind.StatusChange), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_log_says_so_in_this_screens_own_words()
    {
        // NFR-22: the empty state is a row that says there is nothing, not an absence of rows. And
        // it is this screen's sentence rather than the table's default, because "nothing here" and
        // "nothing has been sent yet" are different pieces of news.
        var html = await RenderAsync([]);

        Assert.Equal(1, SharedMarkup.Occurrences(html, "dt-table-empty"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-row"));

        var empty = SharedMarkup.TextOf(SharedMarkup.ElementWithClass(html, "tr", "dt-table-empty"));

        Assert.True(SharedMarkup.IsUkrainian(empty), $"The empty state reads '{empty}'.");
        Assert.False(SharedMarkup.HasLatinWord(empty), $"The empty state reads '{empty}'.");
    }

    [Fact]
    public async Task The_screen_shows_the_shared_loading_row_while_the_log_is_being_read()
    {
        // The state that exists so a fetch does not look like a broken screen, read off the first
        // render pass - which is the only moment it exists, because by quiescence the rows have
        // arrived and the flag is false again.
        var html = await ComponentRenderer.RenderFirstPassAsync<Web.Components.Pages.Admin.Notifications>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ICurrentUser>(new StubCaller());
                services.AddSingleton<INotificationLogService>(new NeverAnswers());
            });

        Assert.Equal(1, SharedMarkup.Occurrences(html, "dt-table-status"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-row"));
        Assert.Equal(0, SharedMarkup.Occurrences(html, "dt-table-empty"));
    }

    [Fact]
    public async Task Every_word_the_screen_renders_of_its_own_is_Ukrainian()
    {
        // NFR-14, read back from the rendered page rather than from the source: a key that resolved
        // to its own name would read as Latin here and nowhere else. The rows themselves are data -
        // an email address is Latin by nature - so they are deliberately not in scope.
        var html = await RenderAsync(Attempts);

        var chrome = new List<string>
        {
            SharedMarkup.ElementWithClass(html, "thead", "dt-table-head"),
            SharedMarkup.ElementWithClass(html, "span", "dt-notification-sent"),
            SharedMarkup.ElementWithClass(html, "span", "dt-notification-failed"),
        };

        chrome.AddRange(Regex.Matches(html, @"<h1[^>]*>(?<body>.*?)</h1>", RegexOptions.Singleline, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["body"].Value));

        Assert.NotEmpty(chrome);

        foreach (var fragment in chrome)
        {
            var text = SharedMarkup.TextOf(fragment);

            Assert.True(SharedMarkup.IsUkrainian(text), $"A rendered fragment reads '{text}'.");
            Assert.False(SharedMarkup.HasLatinWord(text), $"A rendered fragment reads '{text}'.");
        }
    }

    [Theory]
    [InlineData(ListNotificationAttemptsQueryValidator.MaximumLimit, 1)]
    [InlineData(ListNotificationAttemptsQueryValidator.MaximumLimit - 1, 0)]
    public async Task A_full_page_says_there_may_be_more_and_a_short_one_does_not(
        int rows,
        int banners)
    {
        // NFR-27 bounds the read, and this is the other half of bounding it: the log grows by a row
        // per status change per delivery, so past a full page the older attempts are off the screen
        // with nothing saying so. A full page is all this screen can know - the query answers no
        // total (PRD section 8 excludes one) - so "there may be more" is the honest claim.
        var html = await RenderAsync(Page(rows));

        Assert.Equal(rows, SharedMarkup.Occurrences(html, "dt-table-row"));

        // The note now belongs to the table rather than sitting above it: it is a fact about what
        // is on screen, and a reader who has not looked at the rows yet has nothing to apply it to.
        // `dt-table-note` is the shared table's own hook, so this reads the one element that can
        // carry the claim rather than any warning-coloured box on the page.
        Assert.Equal(banners, SharedMarkup.Occurrences(html, "dt-table-note"));

        if (banners == 0)
        {
            return;
        }

        // NFR-14: the sentence is the catalogue's, not a key that resolved to its own name.
        var text = SharedMarkup.TextOf(SharedMarkup.ElementWithClass(html, "p", "dt-table-note"));

        Assert.True(SharedMarkup.IsUkrainian(text), $"The truncation banner reads '{text}'.");
        Assert.False(SharedMarkup.HasLatinWord(text), $"The truncation banner reads '{text}'.");
    }

    [Fact]
    public async Task The_screen_asks_for_the_first_page_at_the_validator_s_own_maximum()
    {
        // The page the screen asks for is not observable in its markup, so without this nothing
        // holds it to the number the banner is compared against. A smaller limit would render fewer
        // rows with IsTruncated still measuring against MaximumLimit - a screen silently hiding
        // attempts while claiming there are no more - and a larger one would make the real
        // validator refuse every administrator. Both leave every other test here green.
        var log = new StubLog(Attempts);

        await ComponentRenderer.RenderAsync<Web.Components.Pages.Admin.Notifications>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ICurrentUser>(new StubCaller());
                services.AddSingleton<INotificationLogService>(log);
            });

        var asked = Assert.IsType<ListNotificationAttemptsQuery>(log.Asked);

        Assert.Equal(0, asked.Offset);
        Assert.Equal(ListNotificationAttemptsQueryValidator.MaximumLimit, asked.Limit);
    }

    [Fact]
    public async Task The_heading_sits_in_the_shared_page_head()
    {
        // <h1> rather than <h2>: `FocusOnNavigate Selector="h1"` in Routes.razor looks for one, and
        // on a page without it a keyboard user keeps the focus the previous screen had. In the
        // shared heading row because the product has one heading shape - this screen offers no
        // action at all, and the wrapper is written all the same so a reader moving between
        // screens is not shown two different ways of starting a page.
        var html = await RenderAsync(Attempts);

        var head = PageHead.Match(html);

        Assert.True(head.Success, "The screen has no heading row.");

        var body = head.Groups["body"].Value;

        Assert.Contains("<h1", body, StringComparison.Ordinal);

        // NFR-24: the heading carries the glyph the navigation already uses for this destination.
        Assert.Contains("<svg", body, StringComparison.Ordinal);

        // And nothing beside it. The log is read; there is no attempt on it that can be retried,
        // dismissed or deleted, so an action here would be one the screen does not have.
        Assert.DoesNotContain("<button", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_cell_carries_its_own_name_for_the_phone_layout()
    {
        // Below the phone breakpoint the head is dropped rather than hidden: a table laid out as
        // blocks is no longer a table to a screen reader - the implicit table, row and cell roles
        // go with the `display` they came from - so a `<thead>` left in place would name columns
        // that no longer exist to be announced under. The label inside each cell is what replaces
        // it, and that only works if every cell has one.
        //
        // Six columns, and the value a reader opens the screen for is the error, which is the last
        // of them and so the furthest off the edge of a handset.
        //
        // Cell by cell rather than by counting labels against cells across the row: two labels in
        // one cell and none in the next is the same total and a card with an unnamed line in it.
        var html = await RenderAsync(Attempts);

        var columns = HeaderCell.Matches(html)
            .Select(match => SharedMarkup.TextOf(match.Groups["body"].Value))
            .ToArray();

        Assert.NotEmpty(columns);

        var rows = BodyRow.Matches(html)
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        Assert.NotEmpty(rows);

        foreach (var row in rows)
        {
            var cells = Cell.Matches(row)
                .Select(match => match.Groups["body"].Value)
                .ToArray();

            Assert.Equal(columns.Length, cells.Length);

            for (var index = 0; index < cells.Length; index++)
            {
                var names = LabelSpan.Matches(cells[index])
                    .Select(match => SharedMarkup.TextOf(match.Groups["body"].Value))
                    .ToArray();

                // A cell with nothing in it carries no name either. The error column is empty on
                // every attempt that worked - the common row rather than the exception - and a
                // card painting a heading over a blank line promises a value the row does not
                // have. The two go together, so a name is asserted exactly where a value is.
                if (SharedMarkup.TextOf(cells[index]).Length == 0)
                {
                    Assert.Empty(names);

                    continue;
                }

                Assert.Equal(new[] { columns[index] }, names);
            }
        }
    }

    [Fact]
    public void The_screen_takes_no_authorization_decision_of_its_own()
    {
        // FR-12 / AD-2: IAccessGuard.RequireRole(Admin) inside NotificationLogService is the only
        // place this screen's audience is decided. A role on the page attribute would be a second
        // copy of that rule, worded differently, in a place no test of the rule would read.
        var screen = SharedMarkup.ReadComponent("Pages", "Admin", "Notifications.razor");

        Assert.Contains("@attribute [Authorize]", screen, StringComparison.Ordinal);

        var markup = Regex.Replace(
            screen, @"@\*.*?\*@", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("<AuthorizeView", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("[Authorize(Roles", markup, StringComparison.Ordinal);
    }

    /// <summary>A page of <paramref name="rows"/> attempts, each distinct enough to be its own row.</summary>
    private static NotificationAttemptSummary[] Page(int rows) =>
        [
            .. Enumerable.Range(1, rows).Select(index => new NotificationAttemptSummary(
                index,
                index,
                NotificationKind.StatusChange,
                $"client{index}@drivetrack.test",
                Noon,
                NotificationOutcome.Sent,
                null)),
        ];

    /// <summary>
    /// The screen with its rows, for the phone-layout suite next door: this class already keeps the
    /// stubs it is given, and a second set of them would be a second answer to what it reads.
    /// </summary>
    internal static Task<string> RenderLogAsync() => RenderAsync(Attempts);

    private static Task<string> RenderAsync(IReadOnlyList<NotificationAttemptSummary> attempts) =>
        ComponentRenderer.RenderAsync<Web.Components.Pages.Admin.Notifications>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ICurrentUser>(new StubCaller());
                services.AddSingleton<INotificationLogService>(new StubLog(attempts));
            });

    /// <summary>A signed-in administrator, and nothing else the screen reads.</summary>
    private sealed class StubCaller : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public UserId UserId => new(1);

        public UserRole Role => UserRole.Admin;

        public DriverId? DriverId => null;

        public ClientId? ClientId => null;
    }

    /// <summary>The log, answered without a database, remembering what it was asked for.</summary>
    private sealed class StubLog(IReadOnlyList<NotificationAttemptSummary> attempts)
        : INotificationLogService
    {
        /// <summary>The page the screen asked for, or null until it has asked.</summary>
        public ListNotificationAttemptsQuery? Asked { get; private set; }

        public Task<IReadOnlyList<NotificationAttemptSummary>> ListAsync(
            ListNotificationAttemptsQuery query,
            CancellationToken cancellationToken)
        {
            Asked = query;

            return Task.FromResult(attempts);
        }
    }

    /// <summary>
    /// A log that never answers, so the first render pass is taken while the screen is still
    /// waiting. Nothing awaits the task, and the renderer is disposed without it.
    /// </summary>
    private sealed class NeverAnswers : INotificationLogService
    {
        public Task<IReadOnlyList<NotificationAttemptSummary>> ListAsync(
            ListNotificationAttemptsQuery query,
            CancellationToken cancellationToken) =>
            new TaskCompletionSource<IReadOnlyList<NotificationAttemptSummary>>().Task;
    }
}
