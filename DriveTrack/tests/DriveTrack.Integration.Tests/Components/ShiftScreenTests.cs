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

    /// <summary>One driver on duty and one who went home, so both branches of the badge are drawn.</summary>
    private static readonly ShiftSummary[] Roster =
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

    private static Task<string> RenderRosterAsync() =>
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

    private static Task<string> RenderOwnAsync(IReadOnlyList<ShiftSummary> shifts) =>
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
