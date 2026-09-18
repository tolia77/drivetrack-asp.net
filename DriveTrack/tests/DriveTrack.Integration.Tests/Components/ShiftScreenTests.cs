using System.Text.RegularExpressions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Drivers;
using DriveTrack.Application.Shifts;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using MyShiftsScreen = DriveTrack.Web.Components.Pages.MyShifts;
using ShiftsScreen = DriveTrack.Web.Components.Pages.Shifts;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// What the two shift screens actually render, with the capabilities stubbed (FR-109 to FR-117).
/// <para>
/// A source scan cannot make these claims. "A driver on duty is offered the way off it and not the
/// way on" is a property of the output, and the file contains all the same words whether it holds or
/// not.
/// </para>
/// <para>
/// FR-12 throughout: none of this is authorization. <c>IAccessGuard</c> inside the shift service
/// refuses a caller whatever these screens drew, and <c>ShiftManagementTests</c> is where that is
/// proved. What is asserted here is that a screen does not offer an action that could only ever
/// fail — and FR-116's marker on the delivery board's driver picker is asserted in
/// <c>DeliveryScreenTests</c>, where the board's own stubs already live.
/// </para>
/// </summary>
public class ShiftScreenTests
{
    private static readonly DateTimeOffset Morning = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The heading row: the screen's title and the action the screen is for.</summary>
    private static readonly Regex PageHead = new(
        @"<div\b[^>]*class=""[^""]*\bdt-page-head\b[^""]*""[^>]*>(?<body>.*?)</div>",
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

    /// <summary>The per-cell name span both shift screens mark with the one shift label class.</summary>
    private static readonly Regex LabelSpan = new(
        @"<span\b[^>]*class=""[^""]*\bdt-shift-label\b[^""]*""[^>]*>(?<body>.*?)</span>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A column heading, whose text each cell's own name has to match.</summary>
    private static readonly Regex HeaderCell = new(
        @"<th\b[^>]*>(?<body>.*?)</th>",
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

    /// <summary>One driver on duty and one who went home, so both branches of the badge are drawn.</summary>
    internal static readonly ShiftSummary[] Roster =
    [
        new(1, new DriverId(1), "Тарас Шевченко", Morning, null, true),
        new(2, new DriverId(2), "Олег Коваль", Morning.AddDays(-1), Morning.AddHours(-16), false),
    ];

    [Fact]
    public void Each_screen_is_routed_to_the_roles_that_have_one()
    {
        // FR-79's convenience, read off the source because an attribute is not something a render
        // can show: a wrong role is sent to /access-denied rather than into an error boundary.
        //
        // The pair is the point. The roster screen belongs to dispatch, and the personal one to a
        // driver and to nobody else - a dispatcher's scope narrows to nobody, so "my shifts" would
        // show them the whole roster under a heading that says otherwise.
        Assert.Contains(
            @"@attribute [Authorize(Roles = ""Dispatcher,Admin"")]",
            SharedMarkup.ReadComponent("Pages", "Shifts.razor"),
            StringComparison.Ordinal);

        Assert.Contains(
            @"@attribute [Authorize(Roles = ""Driver"")]",
            SharedMarkup.ReadComponent("Pages", "MyShifts.razor"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_roster_screen_names_every_drivers_shift_and_labels_its_state()
    {
        // FR-113: the whole roster, with the driver named rather than referred to by row id, and
        // each row's state as a badge. The two rows are one of each kind, so a badge that always
        // rendered the same way would fail here rather than pass on a page where everybody happens
        // to be on duty.
        var html = await RenderRosterAsync();

        Assert.Equal(Roster.Length, SharedMarkup.Occurrences(html, "dt-table-row"));

        Assert.Contains("Тарас Шевченко", html, StringComparison.Ordinal);
        Assert.Contains("Олег Коваль", html, StringComparison.Ordinal);

        // AD-28 and NFR-29: the two states are StatusLabels, so there is no colour in the markup for
        // the design-token gate to find - and no Bootstrap badge either. The shape as well as the
        // fill comes from `.dt-status--*` in the theme, which is what makes the two states tell
        // themselves apart for a reader who cannot see the difference between green and grey.
        Assert.Contains("dt-status dt-status--running", html, StringComparison.Ordinal);
        Assert.Contains("dt-status dt-status--finished", html, StringComparison.Ordinal);
        Assert.DoesNotContain("text-bg-", html, StringComparison.Ordinal);

        // And the words are the shift's rather than the driver's. A row in a history labelled "на
        // зміні" would be a present-tense claim about where somebody is now, which for every
        // finished row is a claim nobody checked - so the badge says the shift is open or over, and
        // the picker's wording is left to the picker.
        Assert.Contains("Відкрита", html, StringComparison.Ordinal);
        Assert.Contains("Завершена", html, StringComparison.Ordinal);

        // An open shift has no end, and says so rather than leaving a cell empty - which reads as a
        // missing value instead of as a shift that has not finished.
        Assert.Contains("Триває", html, StringComparison.Ordinal);

        // FR-114's two corrections, and the shared confirmation in front of the destructive one.
        Assert.Contains("dt-shift-edit", html, StringComparison.Ordinal);
        Assert.Contains("dt-shift-delete", html, StringComparison.Ordinal);
        Assert.Contains("dt-confirm-accept", html, StringComparison.Ordinal);

        // And no on-duty button: going on duty is a driver reporting that they have started work,
        // stamped by the server's clock. A dispatcher records a window instead, through the form.
        Assert.DoesNotContain("dt-shift-start", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-shift-end", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_roster_form_offers_a_driver_a_start_and_an_end()
    {
        // FR-113's correction form. The driver picker is only on the create path, because a shift is
        // one driver's stretch of time - moving one to another driver is deleting it and writing
        // another, and a movable driver would let an edit carry an open row to somebody who already
        // has one.
        var html = await RenderRosterAsync();

        Assert.Contains(@"id=""shift-driver""", html, StringComparison.Ordinal);
        Assert.Contains(@"id=""shift-started-at""", html, StringComparison.Ordinal);
        Assert.Contains(@"id=""shift-ended-at""", html, StringComparison.Ordinal);

        // Both instants are date-and-time controls: a shift that recorded only a date could not say
        // when somebody actually started.
        Assert.Equal(2, SharedMarkup.Occurrences(html, @"type=""datetime-local"""));

        // FR-117 said to a person, beside the box it is about.
        Assert.Contains("Завершену зміну відновити не можна", html, StringComparison.Ordinal);

        // FR-116 on this picker too, and asserted on the <option> text rather than on the page: the
        // stub holds one driver of each duty state, so both words appear somewhere in the markup
        // whether the marker is composed correctly, inverted, or dropped. Which name each word is
        // attached to is the only assertion that tells those three apart.
        var options = Regex.Matches(
                html,
                @"<option\b[^>]*>(?<body>.*?)</option>",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5))
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups["body"].Value).Trim())
            .ToArray();

        // The picker's words, not the badge's: this one is a claim about where a driver is now,
        // which is why it does not borrow ShiftStateLabel's "Відкрита"/"Завершена".
        Assert.Contains("Тарас Шевченко — На зміні", options);
        Assert.Contains("Олег Коваль — Не на зміні", options);
    }

    [Fact]
    public async Task A_driver_on_duty_is_offered_the_way_off_it_and_not_the_way_on()
    {
        // FR-109, and the reason the screen draws one action rather than two: whether a driver is on
        // duty is one predicate - an open shift exists - so the transition offered is the one that
        // predicate allows, and a driver never meets a button that could only be refused.
        var html = await RenderOwnAsync(Roster);

        Assert.Contains("dt-shift-end", html, StringComparison.Ordinal);
        Assert.Contains("Завершити зміну", html, StringComparison.Ordinal);

        Assert.DoesNotContain("dt-shift-start", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_driver_who_is_off_duty_is_offered_the_way_on()
    {
        // The other side of the predicate. Without it the assertion above would be satisfied by a
        // screen that never drew the start button at all.
        var html = await RenderOwnAsync([Roster[1]]);

        Assert.Contains("dt-shift-start", html, StringComparison.Ordinal);
        Assert.Contains("Почати зміну", html, StringComparison.Ordinal);

        Assert.DoesNotContain("dt-shift-end", html, StringComparison.Ordinal);

        // The header's badge follows the same predicate as the button, so the two can never
        // disagree about where the driver is.
        Assert.Contains("Не на зміні", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_driver_sees_no_driver_column_at_all()
    {
        // FR-112 at the surface. The rows are narrowed by the guard, and the screen has nowhere to
        // put somebody else's name: a driver column here would be a place a widening bug could
        // become visible to the wrong person.
        var html = await RenderOwnAsync(Roster);

        // The stub answers rows carrying the other driver's name - as the service would if the
        // narrowing were ever dropped - so this is a claim about the screen rather than about the
        // data it happened to be given.
        Assert.DoesNotContain("Олег Коваль", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Тарас Шевченко", html, StringComparison.Ordinal);

        // FR-112's own corrections are still there: a driver fixes a shift they logged an hour late.
        Assert.Contains("dt-shift-edit", html, StringComparison.Ordinal);
        Assert.Contains("dt-shift-delete", html, StringComparison.Ordinal);
        Assert.Contains("dt-confirm-accept", html, StringComparison.Ordinal);

        // The two vocabularies, side by side and each with its own subject: the header says where
        // the driver is now, the finished row says what became of that shift. Sharing one pair of
        // keys would have put "на зміні" on a row from last week - a present-tense claim about a
        // person that nobody checked.
        Assert.Contains("На зміні", html, StringComparison.Ordinal);
        Assert.Contains("Завершена", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_roster_screens_title_and_create_action_share_one_heading_row()
    {
        // The shape the two delivery screens settled. Recording a shift is about the roster the
        // heading names rather than about any row in it, so it belongs on that line; the search
        // box narrows that same collection and stays below.
        var html = await RenderRosterAsync();

        var head = PageHead.Match(html);

        Assert.True(head.Success, "The screen has no heading row.");

        var body = head.Groups["body"].Value;

        Assert.Contains("<h1", body, StringComparison.Ordinal);
        Assert.Contains("dt-shift-create", body, StringComparison.Ordinal);
        Assert.Contains("btn-primary", body, StringComparison.Ordinal);
        Assert.DoesNotContain(@"type=""search""", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_personal_screens_heading_row_carries_the_duty_badge_and_the_one_transition()
    {
        // This screen has exactly one action, and one action belongs where every other screen puts
        // its primary one. The badge goes up there with it because it is the fact that decides
        // which of the two transitions is drawn at all - read on a separate row, the claim and its
        // consequence were two things a reader had to put back together.
        var onDuty = PageHead.Match(await RenderOwnAsync(Roster));

        Assert.True(onDuty.Success, "The screen has no heading row.");

        var open = onDuty.Groups["body"].Value;

        Assert.Contains("<h1", open, StringComparison.Ordinal);

        // A DtBadge and not a ShiftStateLabel: this is a claim about the caller right now, and the
        // rows below say whether a shift is open or over. The two vocabularies must not converge.
        Assert.Contains("dt-badge", open, StringComparison.Ordinal);
        Assert.Contains("На зміні", open, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-status", open, StringComparison.Ordinal);

        Assert.Contains("dt-shift-end", open, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-shift-start", open, StringComparison.Ordinal);

        var offDuty = PageHead.Match(await RenderOwnAsync([Roster[1]]));

        Assert.True(offDuty.Success, "The screen has no heading row for a driver off duty.");

        var closed = offDuty.Groups["body"].Value;

        Assert.Contains("Не на зміні", closed, StringComparison.Ordinal);
        Assert.Contains("dt-shift-start", closed, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-shift-end", closed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_row_action_sits_in_the_shared_actions_cell(bool roster)
    {
        // DtDataTable right-aligns the trailing cell through `.dt-table-row ::deep
        // td.dt-table-actions`, so a cell that does not wear the class its own heading wears is a
        // cell the shared rule cannot reach. Both screens used to mark it `dt-row-actions`, which
        // is a class each of them styled nowhere.
        var html = roster ? await RenderRosterAsync() : await RenderOwnAsync(Roster);

        var cells = ActionsCell.Matches(html)
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        // Before the count, because `0 == 0` is what a screen that rendered no rows at all would
        // answer - and the loop below would then assert nothing.
        Assert.NotEmpty(cells);

        Assert.Equal(SharedMarkup.Occurrences(html, "dt-table-row"), cells.Length);

        foreach (var cell in cells)
        {
            Assert.Contains("dt-shift-edit", cell, StringComparison.Ordinal);
            Assert.Contains("dt-shift-delete", cell, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("dt-row-actions", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_cell_carries_its_own_name_for_the_phone_layout(bool roster)
    {
        // Below the phone breakpoint the head is dropped rather than hidden: a table laid out as
        // blocks is no longer a table to a screen reader - the implicit table, row and cell roles
        // go with the `display` they came from - so a `<thead>` left in place would name columns
        // that no longer exist to be announced under. The label inside each cell is what replaces
        // it, and that only works if every cell has one.
        //
        // Cell by cell rather than by counting labels against cells across the row: two labels in
        // one cell and none in the next is the same total and a card with an unnamed line in it.
        // And the label is checked against the heading of the column it is in, because on this
        // screen two of the columns are timestamps - a card showing two unnamed instants is worse
        // than one showing them under the wrong names only in that nobody could tell.
        var html = roster ? await RenderRosterAsync() : await RenderOwnAsync(Roster);

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

                Assert.Equal(new[] { columns[index] }, names);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_actions_column_keeps_its_name_without_printing_it(bool roster)
    {
        // The buttons under it say what they do, so a column name printed above them competes with
        // them for the same width - and a column with no name at all leaves a screen reader moving
        // by cell with one unlabelled column. Hidden, not dropped, as on the two delivery screens.
        var html = roster ? await RenderRosterAsync() : await RenderOwnAsync(Roster);

        var actions = ActionsHeader.Match(html);

        Assert.True(actions.Success, "The table has no actions heading.");
        Assert.Contains("dt-visually-hidden", actions.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("Дії", actions.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_roster_search_reads_the_driver_and_keeps_everything_when_it_is_blank()
    {
        // The one thing on the screen a render cannot reach: static rendering dispatches no events,
        // so the filter is exercised directly. Culture-aware and case-insensitive - a dispatcher
        // typing a name in lower case is looking for the same driver.
        Assert.True(ShiftsScreen.Matches(Roster[0], null));
        Assert.True(ShiftsScreen.Matches(Roster[0], "   "));

        Assert.True(ShiftsScreen.Matches(Roster[0], "шевченко"));
        Assert.True(ShiftsScreen.Matches(Roster[0], " Тарас "));

        Assert.False(ShiftsScreen.Matches(Roster[0], "Коваль"));
    }

    [Fact]
    public async Task Every_action_on_both_screens_carries_an_icon_beside_its_text()
    {
        // NFR-24. Asserted by counting rather than by naming, because the failure this catches is a
        // button added later without one - which no named assertion would notice.
        foreach (var html in new[] { await RenderRosterAsync(), await RenderOwnAsync(Roster) })
        {
            var bare = Regex.Matches(
                    html,
                    @"<button\b[^>]*>(?<body>.*?)</button>",
                    RegexOptions.Singleline,
                    TimeSpan.FromSeconds(5))
                .Select(match => match.Groups["body"].Value)
                .Where(body => !body.Contains("<svg", StringComparison.Ordinal))
                .ToArray();

            Assert.Empty(bare);
        }
    }

    [Fact]
    public async Task Nothing_on_either_screen_is_written_in_English()
    {
        // NFR-14 as the rendered page rather than as a source scan. UserFacingTextTests reads the
        // markup and cannot see a key that resolves to an English value or to the key itself, which
        // is exactly what a missing resource entry looks like.
        foreach (var html in new[]
                 {
                     await RenderRosterAsync(),
                     await RenderOwnAsync(Roster),
                     await RenderOwnAsync([Roster[1]]),
                 })
        {
            var text = Regex.Replace(
                html,
                @"<[^>]*>",
                " ",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5));

            var offenders = Regex.Matches(text, @"[A-Za-z]{3,}", RegexOptions.None, TimeSpan.FromSeconds(5))
                .Select(match => match.Value)
                .Where(word => !string.Equals(word, "DriveTrack", StringComparison.Ordinal))
                .ToArray();

            Assert.Empty(offenders);
        }
    }

    internal static Task<string> RenderRosterAsync() =>
        ComponentRenderer.RenderAsync<ShiftsScreen>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ICurrentUser>(new StubCaller(UserRole.Dispatcher, null));
                services.AddSingleton<IShiftService>(new StubShifts(Roster));
                services.AddSingleton<IDriverService>(new StubDrivers());

                // AD-13: the form opens on the injected clock's instant rather than on a blank box,
                // so the screen needs one even though nothing here writes.
                services.AddSingleton(TimeProvider.System);
            });

    internal static Task<string> RenderOwnAsync(IReadOnlyList<ShiftSummary> shifts) =>
        ComponentRenderer.RenderAsync<MyShiftsScreen>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ICurrentUser>(
                    new StubCaller(UserRole.Driver, new DriverId(1)));
                services.AddSingleton<IShiftService>(new StubShifts(shifts));
            });

    /// <summary>A signed-in caller of a chosen role, and nothing else the screens read.</summary>
    private sealed class StubCaller(UserRole role, DriverId? driverId) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public UserId UserId => new(1);

        public UserRole Role => role;

        public DriverId? DriverId => driverId;

        public ClientId? ClientId => null;
    }

    /// <summary>
    /// The shift list, answered without a database. Every write throws: a static render never
    /// reaches one, so a screen that called it during rendering would fail loudly rather than pass
    /// quietly.
    /// </summary>
    private sealed class StubShifts(IReadOnlyList<ShiftSummary> shifts) : IShiftService
    {
        public Task<IReadOnlyList<ShiftSummary>> ListAsync(
            ListShiftsQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(shifts);

        public Task<ShiftSummary> GetAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The shift screens read the list, not one row.");

        public Task<ShiftSummary> StartAsync(
            StartShiftCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<ShiftSummary> EndAsync(
            EndShiftCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<ShiftSummary> CreateAsync(
            CreateShiftCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<ShiftSummary> UpdateAsync(
            int id,
            UpdateShiftCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task DeleteAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<IReadOnlyList<DriverId>> ListOnDutyDriverIdsAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The shift screens read the rows, not the on-duty feed.");
    }

    /// <summary>
    /// The roster the roster screen's driver picker is built from. One driver on duty and one off,
    /// so the marker composed in C# has both branches covered.
    /// </summary>
    private sealed class StubDrivers : IDriverService
    {
        private static readonly DriverSummary[] Drivers =
        [
            new(
                new DriverId(1),
                new UserId(10),
                "Тарас",
                "Шевченко",
                "taras@drivetrack.test",
                "ВІ123456",
                VehicleId: null,
                VehicleModel: null,
                VehicleLicensePlate: null,
                Rating: null,
                ReviewCount: 0,
                OnDuty: true),
            new(
                new DriverId(2),
                new UserId(11),
                "Олег",
                "Коваль",
                "oleh@drivetrack.test",
                "ВІ654321",
                VehicleId: null,
                VehicleModel: null,
                VehicleLicensePlate: null,
                Rating: null,
                ReviewCount: 0,
                OnDuty: false),
        ];

        public Task<IReadOnlyList<DriverSummary>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DriverSummary>>(Drivers);

        public Task<DriverSummary> GetAsync(DriverId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The shift screens read the roster, not one driver.");

        public Task<DriverSummary> CreateAsync(
            CreateDriverCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<DriverSummary> UpdateAsync(
            DriverId id,
            UpdateDriverCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task DeleteAsync(DriverId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");
    }
}
