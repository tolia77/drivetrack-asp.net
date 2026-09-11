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

    /// <summary>The log, answered without a database.</summary>
    private sealed class StubLog(IReadOnlyList<NotificationAttemptSummary> attempts)
        : INotificationLogService
    {
        public Task<IReadOnlyList<NotificationAttemptSummary>> ListAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(attempts);
    }

    /// <summary>
    /// A log that never answers, so the first render pass is taken while the screen is still
    /// waiting. Nothing awaits the task, and the renderer is disposed without it.
    /// </summary>
    private sealed class NeverAnswers : INotificationLogService
    {
        public Task<IReadOnlyList<NotificationAttemptSummary>> ListAsync(
            CancellationToken cancellationToken) =>
            new TaskCompletionSource<IReadOnlyList<NotificationAttemptSummary>>().Task;
    }
}
