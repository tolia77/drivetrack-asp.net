using System.Text.RegularExpressions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Drivers;
using DriveTrack.Application.Vehicles;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// What the two fleet screens actually render, for a dispatcher, with the capabilities stubbed.
/// <para>
/// A source scan cannot make these claims. "The assignment control offers only unassigned vehicles"
/// and "every row names the vehicle its driver holds" are properties of the output, and the file
/// contains all the same words whether they hold or not. Rendering is the only thing that can tell.
/// </para>
/// <para>
/// Rendering is static — <see cref="ComponentRenderer"/> never reaches <c>OnAfterRenderAsync</c> and
/// dispatches no events — so what is asserted is the first pass a dispatcher sees: the table, the
/// dialogs' markup, and the choices the assignment control was built with.
/// </para>
/// </summary>
public class FleetScreenTests
{
    /// <summary>The vehicle a driver already holds, so it must never appear among the choices.</summary>
    private const int HeldVehicleId = 5;

    private static readonly Regex HeaderCell = new(
        @"<th\b[^>]*>(?<body>.*?)</th>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex Label = new(
        @"<label\b[^>]*>(?<body>.*?)</label>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>The heading row: the screen's title and the action that opens an empty form.</summary>
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

    /// <summary>
    /// An <c>option</c> and its value. The value is optional in the pattern because the renderer
    /// minimizes an empty attribute to a bare <c>value</c> — which is exactly the option FR-38's
    /// "no vehicle" choice is, so a pattern that required the quotes would skip the one option this
    /// suite most needs to see.
    /// </summary>
    private static readonly Regex Option = new(
        @"<option\s+value(?:=""(?<value>[^""]*)"")?[^>]*>(?<body>.*?)</option>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task The_roster_shows_a_drivers_standing_and_says_nothing_where_there_is_none()
    {
        // FR-98 at the surface the acceptance criterion names. Two drivers, and the distinction the
        // whole nullable field exists for: one has an average, the other has no reviews at all and
        // gets the "no value" text rather than a zero - which is a rating, and the worst one the
        // scale has.
        var html = await RenderDriversAsync();

        // 4.5 under uk-UA, which uses a comma for the decimal mark (NFR-15). Asserted as the
        // rendered string rather than as the number, because the formatting is half the claim.
        Assert.Contains(">4,5<", html, StringComparison.Ordinal);
        Assert.Contains("Немає оцінок", html, StringComparison.Ordinal);

        // AD-18 and AD-28: the band is Domain's reading of the number and the class is what that
        // band looks like. 4.5 is favourable; the unrated driver wears neither that class nor any
        // other band's, because the absence of a verdict is not a verdict.
        Assert.Contains("dt-rating dt-rating--favourable", html, StringComparison.Ordinal);
        Assert.Contains("dt-rating dt-rating--unrated", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-rating--unfavourable", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_driver_roster_renders_through_the_shared_table_and_names_each_vehicle()
    {
        var html = await RenderDriversAsync();

        // FR-82: the shared table, not a second one built beside it.
        Assert.Contains("dt-table-scroll", html, StringComparison.Ordinal);
        Assert.Contains("table-striped", html, StringComparison.Ordinal);

        // Both drivers, with the account fields the browser-side search runs over.
        Assert.Contains("Шевченко", html, StringComparison.Ordinal);
        Assert.Contains("Франко", html, StringComparison.Ordinal);
        Assert.Contains("taras@drivetrack.test", html, StringComparison.Ordinal);
        Assert.Contains("ВІ123456", html, StringComparison.Ordinal);

        // The one who holds a vehicle is shown holding it, and the one who holds none says so
        // rather than rendering an empty cell.
        Assert.Contains("Рено Мастер", html, StringComparison.Ordinal);
        Assert.Contains("АА1234ВВ", html, StringComparison.Ordinal);
        Assert.Contains("Без автомобіля", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_assignment_control_offers_only_free_vehicles_and_an_explicit_empty_choice()
    {
        // FR-45 and FR-38 in one control. The empty option is how a dispatcher releases a vehicle,
        // and a held vehicle among the choices would be a choice that can only be refused.
        var html = await RenderDriversAsync();
        var select = SelectBody(html, "driver-vehicle");

        var options = Option.Matches(select)
            .Select(match => (match.Groups["value"].Value, Text: SharedMarkup.TextOf(match.Groups["body"].Value)))
            .ToArray();

        Assert.NotEmpty(options);

        var empty = options[0];

        Assert.Equal(string.Empty, empty.Value);
        Assert.True(SharedMarkup.IsUkrainian(empty.Text), $"The empty choice reads '{empty.Text}'.");

        var offered = options.Skip(1).Select(option => option.Value).ToArray();

        Assert.Equal(new[] { "7", "9" }, offered);
        Assert.DoesNotContain(
            HeldVehicleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            offered);
    }

    [Fact]
    public async Task The_roster_confirms_a_deletion_through_the_shared_prompt()
    {
        // FR-81: the baseline deleted on the click that asked for it. The prompt is a component so
        // a screen cannot forget it, and this is what proves this screen did not.
        var html = await RenderDriversAsync();

        Assert.Contains("dt-confirm-accept", html, StringComparison.Ordinal);
        Assert.Contains("dt-confirm-cancel", html, StringComparison.Ordinal);

        // NFR-19: the dialog is the native element with the ARIA trio written out.
        Assert.Contains("<dialog", html, StringComparison.Ordinal);
        Assert.Contains(@"aria-modal=""true""", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fleet_screen_renders_its_rows_through_the_shared_table_and_prompt()
    {
        var html = await RenderVehiclesAsync();

        Assert.Contains("dt-table-scroll", html, StringComparison.Ordinal);
        Assert.Contains("Рено Мастер", html, StringComparison.Ordinal);
        Assert.Contains("АА1234ВВ", html, StringComparison.Ordinal);
        Assert.Contains("dt-confirm-accept", html, StringComparison.Ordinal);
        Assert.Contains("<dialog", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_column_heading_and_field_label_is_Ukrainian(bool drivers)
    {
        // NFR-14, asserted on the rendered chrome rather than on the source: a key that resolved to
        // its own name would read as Latin here and nowhere else. The data cells are excluded on
        // purpose - an email address is Latin by nature and is not a translated string.
        var html = drivers ? await RenderDriversAsync() : await RenderVehiclesAsync();

        var texts = HeaderCell.Matches(html)
            .Concat(Label.Matches(html))
            .Select(match => SharedMarkup.TextOf(match.Groups["body"].Value))
            .Where(text => text.Length > 0)
            .ToArray();

        Assert.NotEmpty(texts);

        var offenders = texts
            .Where(text => !SharedMarkup.IsUkrainian(text) || SharedMarkup.HasLatinWord(text))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task The_vehicle_form_captures_the_mileage_and_the_maintenance_date()
    {
        // FR-40 and FR-42 name both. PRD section 8 excludes the maintenance *alerting*, not the
        // fields - and a field no form can set is not captured-and-unread, it is a column that
        // stays null for good. They are on the form and deliberately not in the table: no screen in
        // this milestone reads them back.
        var html = await RenderVehiclesAsync();

        Assert.Contains(@"id=""vehicle-mileage""", html, StringComparison.Ordinal);
        Assert.Contains(@"id=""vehicle-maintenance""", html, StringComparison.Ordinal);

        var headings = HeaderCell.Matches(html)
            .Select(match => SharedMarkup.TextOf(match.Groups["body"].Value))
            .ToArray();

        Assert.Equal(4, headings.Length);
        Assert.DoesNotContain(headings, heading => heading.Contains("Пробіг", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_driver_form_offers_the_name_fields_FR37_names()
    {
        // FR-37: a dispatcher may change a driver's name, so the inputs exist. The address and the
        // password are create-only, which the rendered create form still shows.
        var html = await RenderDriversAsync();

        Assert.Contains(@"id=""driver-first-name""", html, StringComparison.Ordinal);
        Assert.Contains(@"id=""driver-last-name""", html, StringComparison.Ordinal);
        Assert.Contains(@"id=""driver-license-number""", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "dt-driver-create")]
    [InlineData(false, "dt-vehicle-create")]
    public async Task Each_screen_carries_a_heading_and_a_create_action(bool drivers, string hook)
    {
        // <h1> rather than <h2>: `FocusOnNavigate Selector="h1"` in Routes.razor looks for one, and
        // on a page without it a keyboard user keeps the focus the previous screen had.
        var html = drivers ? await RenderDriversAsync() : await RenderVehiclesAsync();

        Assert.Contains("<h1", html, StringComparison.Ordinal);

        // NFR-24: every action carries a glyph beside its text.
        Assert.Contains("<svg", html, StringComparison.Ordinal);

        // And the create action is beside the heading rather than loose in the row above the table:
        // it is about the collection the heading names, and the search box - which narrows that
        // same collection - is not.
        var head = PageHead.Match(html);

        Assert.True(head.Success, "The screen has no heading row.");

        var body = head.Groups["body"].Value;

        Assert.Contains("<h1", body, StringComparison.Ordinal);

        // Found by its hook and only then asserted about by its colour. `btn btn-primary` is what
        // a create action looks like rather than what one is, so a test that located the action by
        // the class would go on passing the day a second primary button lands anywhere on the page
        // - and would say nothing at all about where this one sits.
        Assert.Contains(hook, body, StringComparison.Ordinal);
        Assert.Contains("btn-primary", body, StringComparison.Ordinal);

        Assert.DoesNotContain(@"type=""search""", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "dt-driver-edit", "dt-driver-delete")]
    [InlineData(false, "dt-vehicle-edit", "dt-vehicle-delete")]
    public async Task Every_row_action_carries_a_hook_and_sits_in_the_shared_actions_cell(
        bool drivers,
        string edit,
        string delete)
    {
        // Two claims that only look like one. DtDataTable right-aligns the trailing cell through
        // `.dt-table-row ::deep td.dt-table-actions`, so a cell that does not wear the class its
        // own heading wears is a cell the shared rule cannot reach; and a button with no
        // `dt-<entity>-<verb>` hook can only be found by its colour, which is what every other
        // screen in the product stopped doing.
        var html = drivers ? await RenderDriversAsync() : await RenderVehiclesAsync();

        var cells = ActionsCell.Matches(html)
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        Assert.NotEmpty(cells);

        foreach (var cell in cells)
        {
            Assert.Contains(edit, cell, StringComparison.Ordinal);
            Assert.Contains(delete, cell, StringComparison.Ordinal);
        }

        // One such cell per row. Without this a screen that rendered the pair in a plain <td> and
        // an empty actions cell beside it would satisfy the loop above.
        Assert.Equal(SharedMarkup.Occurrences(html, "dt-table-row"), cells.Length);
    }

    [Theory]
    [InlineData(true, "dt-driver-label")]
    [InlineData(false, "dt-vehicle-label")]
    public async Task Every_cell_carries_its_own_name_for_the_phone_layout(bool drivers, string label)
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
        var html = drivers ? await RenderDriversAsync() : await RenderVehiclesAsync();

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

    /// <summary>The per-cell name span, matched by the class the screen marks it with.</summary>
    private static Regex LabelSpan(string label) => new(
        $@"<span\b[^>]*class=""[^""]*\b{Regex.Escape(label)}\b[^""]*""[^>]*>(?<body>.*?)</span>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_actions_column_keeps_its_name_without_printing_it(bool drivers)
    {
        // The buttons under it say what they do, so a column name printed above them competes with
        // them for the same width - and a column with no name at all leaves a screen reader moving
        // by cell with one unlabelled column. Hidden, not dropped, as on the two delivery screens.
        var html = drivers ? await RenderDriversAsync() : await RenderVehiclesAsync();

        var actions = ActionsHeader.Match(html);

        Assert.True(actions.Success, "The table has no actions heading.");
        Assert.Contains("dt-visually-hidden", actions.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("Дії", actions.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Each_screen_is_reserved_to_the_two_roles_that_run_dispatch(bool drivers)
    {
        // The attribute is what routes a client to /access-denied instead of into an error
        // boundary (FR-79). It is a convenience and not the decision - IAccessGuard settles
        // that, and the API suites assert it - but nothing else pins the attribute, and its
        // absence is invisible in a diff. Read from source, in the style ShellRoutingTests uses.
        var page = SharedMarkup.ReadComponent("Pages", drivers ? "Drivers.razor" : "Vehicles.razor");

        Assert.Contains(
            "@attribute [Authorize(Roles = \"Dispatcher,Admin\")]",
            page,
            StringComparison.Ordinal);
    }

    // =====================================================================================
    // The two pure functions behind the screens
    //
    // Static rendering dispatches no events, so neither the search box nor the edit dialog can
    // be driven here. Both rules are therefore lifted out of the components and asserted
    // directly - which is the only thing that makes them regressions rather than comments.
    // =====================================================================================

    [Theory]
    // A blank needle keeps everything: an empty search box is not a filter.
    [InlineData("", true, true)]
    [InlineData("   ", true, true)]
    // FR-36 across the name, in the case a dispatcher would actually type.
    [InlineData("шевченко", true, false)]
    [InlineData("ФРАНКО", false, true)]
    // Across the email and the licence number.
    [InlineData("taras@", true, false)]
    [InlineData("ВІ654321", false, true)]
    // And across the vehicle, which is the field most easily dropped from the disjunction:
    // only the driver holding it matches.
    [InlineData("Мастер", true, false)]
    [InlineData("АА1234ВВ", true, false)]
    // A needle matching nothing keeps nothing.
    [InlineData("Коваленко", false, false)]
    public void The_driver_search_matches_across_name_email_licence_and_vehicle(
        string search,
        bool withVehicle,
        bool withoutVehicle)
    {
        Assert.Equal(
            withVehicle,
            Web.Components.Pages.Drivers.Matches(StubDriverService.WithVehicle, search));
        Assert.Equal(
            withoutVehicle,
            Web.Components.Pages.Drivers.Matches(StubDriverService.WithoutVehicle, search));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("транзіт", true)]
    [InlineData("вс5678ее", true)]
    [InlineData("Спрінтер", false)]
    public void The_vehicle_search_matches_across_the_model_and_the_plate(string search, bool expected)
    {
        var vehicle = StubVehicleService.Free[0];

        Assert.Equal(expected, Web.Components.Pages.Vehicles.Matches(vehicle, search));
    }

    [Fact]
    public void The_choices_are_the_free_vehicles_when_no_driver_is_being_edited()
    {
        var choices = Web.Components.Pages.Drivers.ChoicesFor(StubVehicleService.Free, driver: null);

        Assert.Equal(new[] { 7, 9 }, choices.Select(choice => choice.Id).ToArray());
    }

    [Fact]
    public void The_driver_being_edited_is_still_offered_the_vehicle_they_already_hold()
    {
        // The rule that breaks silently. A held vehicle is absent from the unassigned list, and
        // a select whose current value is not among its options reads back as null - so a
        // dispatcher editing only the licence number would send vehicleId: null and release the
        // vehicle without asking for it.
        var choices = Web.Components.Pages.Drivers.ChoicesFor(
            StubVehicleService.Free,
            StubDriverService.WithVehicle);

        Assert.Equal(new[] { HeldVehicleId, 7, 9 }, choices.Select(choice => choice.Id).ToArray());

        var held = choices[0];

        Assert.Equal("Рено Мастер", held.Model);
        Assert.Equal("АА1234ВВ", held.LicensePlate);
    }

    [Fact]
    public void A_driver_holding_nothing_adds_no_choice_and_no_duplicate_is_ever_offered()
    {
        Assert.Equal(
            new[] { 7, 9 },
            Web.Components.Pages.Drivers
                .ChoicesFor(StubVehicleService.Free, StubDriverService.WithoutVehicle)
                .Select(choice => choice.Id)
                .ToArray());

        // A driver whose vehicle is somehow already in the unassigned list must not be offered
        // it twice - a duplicate option is a select that cannot show which one is selected.
        var alreadyFree = StubDriverService.WithVehicle with { VehicleId = 7 };

        Assert.Equal(
            new[] { 7, 9 },
            Web.Components.Pages.Drivers
                .ChoicesFor(StubVehicleService.Free, alreadyFree)
                .Select(choice => choice.Id)
                .ToArray());
    }

    // -------------------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------------------

    internal static Task<string> RenderDriversAsync() =>
        ComponentRenderer.RenderAsync<Web.Components.Pages.Drivers>(
            parameters: null,
            configureServices: Register);

    internal static Task<string> RenderVehiclesAsync() =>
        ComponentRenderer.RenderAsync<Web.Components.Pages.Vehicles>(
            parameters: null,
            configureServices: Register);

    private static void Register(IServiceCollection services)
    {
        services.AddSingleton<ICurrentUser>(new StubDispatcher());
        services.AddSingleton<IDriverService>(new StubDriverService());
        services.AddSingleton<IVehicleService>(new StubVehicleService());
    }

    /// <summary>The inner HTML of the <c>select</c> with that id.</summary>
    private static string SelectBody(string html, string id)
    {
        var match = Regex.Match(
            html,
            $@"<select[^>]*\bid=""{Regex.Escape(id)}""[^>]*>(?<body>.*?)</select>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"No <select> with id '{id}' in:{Environment.NewLine}{html}");

        return match.Groups["body"].Value;
    }

    /// <summary>A signed-in dispatcher, which is the caller both screens open for.</summary>
    private sealed class StubDispatcher : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public UserId UserId => new(1);

        public UserRole Role => UserRole.Dispatcher;

        public DriverId? DriverId => null;

        public ClientId? ClientId => null;
    }

    /// <summary>
    /// Two drivers: one holding <see cref="HeldVehicleId"/>, one holding none. Writes are not
    /// exercised — static rendering dispatches no events — so they answer rather than record.
    /// </summary>
    private sealed class StubDriverService : IDriverService
    {
        /// <summary>A driver holding <see cref="HeldVehicleId"/>.</summary>
        internal static readonly DriverSummary WithVehicle = new(
            new DriverId(1),
            new UserId(10),
            "Тарас",
            "Шевченко",
            "taras@drivetrack.test",
            "ВІ123456",
            HeldVehicleId,
            "Рено Мастер",
            "АА1234ВВ",

            // FR-98: a driver clients have been pleased with. Half a point, so the roster is also
            // asserting that an average is rendered as an average rather than rounded to a star.
            Rating: 4.5,
            ReviewCount: 2,

            // FR-116: on duty right now. The roster does not render the flag - the assignment form
            // does - but the record carries it, so a stub that left it out would be asserting
            // against a shape the service cannot produce.
            OnDuty: true);

        /// <summary>A driver holding nothing.</summary>
        internal static readonly DriverSummary WithoutVehicle = new(
            new DriverId(2),
            new UserId(11),
            "Іван",
            "Франко",
            "ivan@drivetrack.test",
            "ВІ654321",
            null,
            null,
            null,
            null,
            0,
            OnDuty: false);

        public Task<IReadOnlyList<DriverSummary>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DriverSummary>>([WithVehicle, WithoutVehicle]);

        public Task<DriverSummary> GetAsync(DriverId id, CancellationToken cancellationToken) =>
            Task.FromResult(WithVehicle);

        public Task<DriverSummary> CreateAsync(
            CreateDriverCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(WithVehicle);

        public Task<DriverSummary> UpdateAsync(
            DriverId id,
            UpdateDriverCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(WithVehicle);

        public Task DeleteAsync(DriverId id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Three vehicles, of which one is held: the unassigned list is what the assignment control is
    /// built from, so it deliberately answers a strict subset of the fleet.
    /// </summary>
    private sealed class StubVehicleService : IVehicleService
    {
        private static readonly VehicleSummary Held =
            new(HeldVehicleId, "Рено Мастер", "АА1234ВВ", 1200m, 42_000, new DateOnly(2026, 12, 1));

        /// <summary>The vehicles no driver holds, which is what the assignment control is built from.</summary>
        internal static readonly VehicleSummary[] Free =
        [
            new(7, "Форд Транзіт", "ВС5678ЕЕ", 900m, 12_500, null),
            new(9, "Мерседес Спрінтер", "КА9012МН", 1500m, 88_000, new DateOnly(2027, 3, 15)),
        ];

        public Task<IReadOnlyList<VehicleSummary>> ListAsync(
            ListVehiclesQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VehicleSummary>>([Held, .. Free]);

        public Task<IReadOnlyList<VehicleSummary>> ListUnassignedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VehicleSummary>>(Free);

        public Task<VehicleSummary> GetAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(Held);

        public Task<VehicleSummary> CreateAsync(
            CreateVehicleCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Held);

        public Task<VehicleSummary> UpdateAsync(
            int id,
            UpdateVehicleCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Held);

        public Task DeleteAsync(int id, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
