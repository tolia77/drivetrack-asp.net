using System.Text.RegularExpressions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Application.Drivers;
using DriveTrack.Application.Users;
using DriveTrack.Application.Vehicles;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using DeliveryColumn = DriveTrack.Web.Components.Pages.Deliveries.DeliveryColumn;
using DeliveryFilter = DriveTrack.Web.Components.Pages.Deliveries.DeliveryFilter;
using DeliveryForm = DriveTrack.Web.Components.Pages.Deliveries.DeliveryForm;
using RequestForm = DriveTrack.Web.Components.Pages.MyDeliveries.RequestForm;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// What the two delivery screens actually render, with the capabilities stubbed.
/// <para>
/// A source scan cannot make these claims. "The dispatch board offers a create action and the
/// own-deliveries screen does not" and "a location with no address shows its coordinates" are
/// properties of the output, and the files contain all the same words whether they hold or not.
/// </para>
/// <para>
/// Rendering is static — <see cref="ComponentRenderer"/> never reaches <c>OnAfterRenderAsync</c> and
/// dispatches no events — so the sort and filter rules cannot be driven through the markup. Both
/// are therefore lifted out of the component as <c>internal static</c> members and asserted
/// directly, which is what makes them regressions rather than comments.
/// </para>
/// </summary>
public class DeliveryScreenTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly Regex HeaderCell = new(
        @"<th\b[^>]*>(?<body>.*?)</th>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex Label = new(
        @"<label\b[^>]*>(?<body>.*?)</label>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    // =====================================================================================
    // What each screen renders
    // =====================================================================================

    [Fact]
    public async Task The_dispatch_board_renders_its_rows_through_the_shared_table()
    {
        var html = await RenderDeliveriesAsync(UserRole.Dispatcher);

        // FR-82: the shared table, not a second one built beside it.
        Assert.Contains("dt-table-scroll", html, StringComparison.Ordinal);
        Assert.Contains("table-striped", html, StringComparison.Ordinal);

        // <h1> rather than <h2>: `FocusOnNavigate Selector="h1"` in Routes.razor looks for one, and
        // on a page without it a keyboard user keeps the focus the previous screen had.
        Assert.Contains("<h1", html, StringComparison.Ordinal);

        // Both parties named, which costs a second read the mapping has to make (Delivery has no
        // navigation to either), and the unassigned row saying so rather than rendering an empty
        // cell.
        Assert.Contains("Шевченко", html, StringComparison.Ordinal);
        Assert.Contains("Петренко", html, StringComparison.Ordinal);
        Assert.Contains("Без водія", html, StringComparison.Ordinal);
        Assert.Contains("Без замовника", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_location_with_no_resolved_address_shows_its_coordinates()
    {
        // FR-94, and the reason this story can ship before the geocoder does: every location it
        // stores has a null address, so a screen that rendered the address alone would show a
        // column of empty cells and nothing would say why.
        var html = await RenderDeliveriesAsync(UserRole.Dispatcher);

        Assert.Contains("50.4501; 30.5234", html, StringComparison.Ordinal);

        // And the other arm: once story 5.2 fills the cache, the address is what is shown.
        Assert.Contains("Львів, площа Ринок", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_driver_is_offered_no_way_to_open_a_delivery_at_all()
    {
        // The distinction the two screens exist to make, as a driver sees it. Opening, editing and
        // deleting a delivery on somebody's behalf is dispatch's, and asking for one is the client's
        // (FR-89) - so a driver gets none of it, and an action offered here could only ever be
        // refused.
        //
        // The timeline is the exception story 5.3 introduced, and it is not a counter-example: a
        // driver advances the parcel they are carrying (FR-26) and a client adds a note to their own
        // delivery (FR-107), so that dialog belongs on both screens. What must not appear is a form
        // that opens a delivery - dispatch's or the client's.
        var dispatch = await RenderDeliveriesAsync(UserRole.Dispatcher);
        var mine = await RenderMyDeliveriesAsync();

        Assert.Contains("btn btn-success", dispatch, StringComparison.Ordinal);
        Assert.Contains("Створити", dispatch, StringComparison.Ordinal);

        Assert.DoesNotContain("btn btn-success", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("Створити", mine, StringComparison.Ordinal);

        // No dispatch form, no edit form and no deletion prompt: the three dialogs the dispatch
        // board carries and this screen has no operation for.
        Assert.DoesNotContain("delivery-form", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-delivery-edit", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-confirm-accept", mine, StringComparison.Ordinal);

        // And none of the client's request markup either. Withheld rather than hidden: the dialog is
        // not rendered at all for a driver, so there is nothing on the page to reach.
        Assert.DoesNotContain("dt-delivery-request", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("request-form", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("Замовити", mine, StringComparison.Ordinal);

        // And the count, because the named checks above only rule out the dialogs that exist today:
        // a further one, added for some later capability, would slip past every one of them.
        //
        // Four for a driver, and each is an operation they genuinely have. The timeline is story
        // 5.3's; story 6.1 added the capture a driver performs at the door (FR-119) and the proof
        // either role may read afterwards (FR-122); the fourth is FR-21's read-only map, which the
        // dispatch board always had and this screen did not. The number is pinned rather than
        // relaxed for the reason it was pinned at one: it is the only assertion that notices a
        // dialog belonging to another role arriving here by a paste.
        Assert.Equal(4, SharedMarkup.Occurrences(mine, "<dialog"));
    }

    [Fact]
    public async Task A_client_is_offered_the_request_action_and_the_form_behind_it()
    {
        // FR-89 to FR-91 at the surface the intent names. The same screen, the same four dialogs a
        // driver gets, plus one: the request form, which exists for exactly one role.
        var mine = await RenderMyDeliveriesAsync(role: UserRole.Client);

        Assert.Contains("dt-delivery-request", mine, StringComparison.Ordinal);
        Assert.Contains("Замовити доставку", mine, StringComparison.Ordinal);

        // The form itself, and the two address boxes FR-104 puts above the maps.
        Assert.Contains("request-form", mine, StringComparison.Ordinal);
        Assert.Contains("dt-request-pickup-search", mine, StringComparison.Ordinal);
        Assert.Contains("dt-request-dropoff-search", mine, StringComparison.Ordinal);
        Assert.Contains("dt-request-submit", mine, StringComparison.Ordinal);

        // And what the form must not carry, which is the story rather than a styling choice: a
        // request names no driver, no client and no window, because RequestDeliveryCommand has no
        // field to bind one to.
        Assert.DoesNotContain("delivery-driver", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("delivery-client", mine, StringComparison.Ordinal);

        // The ids the dispatch board's window inputs actually carry, so a block pasted across from
        // it is what this catches - an id only this assertion has ever named would catch nothing.
        Assert.DoesNotContain("delivery-window-earliest", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("delivery-window-latest", mine, StringComparison.Ordinal);

        // Nor dispatch's own create action, which the driver's test rules out for a driver and
        // nothing ruled out here: a client asks for a delivery of their own and opens none for
        // anybody else, so the green button belongs on the dispatch board alone.
        Assert.DoesNotContain("btn btn-success", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("Створити", mine, StringComparison.Ordinal);

        // Still not the dispatch board's form, and still not its edit or delete prompts.
        Assert.DoesNotContain("delivery-form", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-delivery-edit", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-confirm-accept", mine, StringComparison.Ordinal);

        Assert.Equal(5, SharedMarkup.Occurrences(mine, "<dialog"));
    }

    [Fact]
    public async Task A_location_cell_is_an_action_on_every_screen_that_shows_one()
    {
        // FR-21 names no role: "clicking a location cell opens a read-only map centred on that
        // coordinate", in the same story that defines what a driver and a client see (FR-25,
        // FR-27). The dispatch board had it and the own-deliveries screen rendered the same
        // addresses as dead text.
        //
        // What is asserted is that the cell is a button rather than a label, because static
        // rendering cannot click it - the same limit the dispatch board's own map test records.
        var dispatch = await RenderDeliveriesAsync(UserRole.Dispatcher);
        var driver = await RenderMyDeliveriesAsync(StubDeliveryService.Assigned);
        var customer = await RenderMyDeliveriesAsync(StubDeliveryService.Assigned, UserRole.Client);

        Assert.Contains("dt-delivery-pickup", dispatch, StringComparison.Ordinal);
        Assert.Contains("dt-delivery-dropoff", dispatch, StringComparison.Ordinal);

        Assert.Contains("dt-my-delivery-pickup", driver, StringComparison.Ordinal);
        Assert.Contains("dt-my-delivery-dropoff", driver, StringComparison.Ordinal);

        // A client reads their own delivery's addresses too, and FR-27 withholds the driver, not
        // the destination.
        Assert.Contains("dt-my-delivery-pickup", customer, StringComparison.Ordinal);
        Assert.Contains("dt-my-delivery-dropoff", customer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Client, true)]
    [InlineData(UserRole.Driver, false)]
    [InlineData(UserRole.Dispatcher, false)]
    [InlineData(UserRole.Admin, false)]
    public void Only_a_client_is_offered_the_request_action(UserRole role, bool offered)
    {
        // Asserted directly for the reason the other two predicates on this screen are: the flag
        // decides whether the dialog is rendered at all, and inverting it would put a request form
        // on a driver's page - markup IAccessGuard.RequireDeliveryComposer would refuse the moment
        // it was used, but markup that should never have been drawn.
        Assert.Equal(offered, Web.Components.Pages.MyDeliveries.OffersRequest(role));
    }

    [Fact]
    public async Task Both_screens_offer_the_timeline_and_neither_pays_for_it_until_it_is_opened()
    {
        // FR-108 reaches every role that can see a delivery, so the action is on both screens. The
        // panel inside the dialog is built only once a row has been chosen - a component per row
        // would issue a timeline read per row on first paint, which is the cost this shape avoids
        // and which nothing else in the suite would notice.
        var dispatch = await RenderDeliveriesAsync(UserRole.Dispatcher);
        var mine = await RenderMyDeliveriesAsync(StubDeliveryService.Assigned);

        Assert.Contains("dt-delivery-timeline", dispatch, StringComparison.Ordinal);
        Assert.Contains("dt-delivery-timeline", mine, StringComparison.Ordinal);

        // The stub's entry would render its actor's name if the panel had been built.
        Assert.DoesNotContain("dt-timeline-list", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-timeline-list", mine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_screens_offer_the_proof_and_only_the_driver_is_offered_the_capture()
    {
        // FR-122 reaches every role that can see a delivery, so the read action is on both screens;
        // FR-119's capture happens at a door, so it is the driver's alone and appears on neither the
        // dispatch board nor a client's list. Without this, deleting either button leaves the whole
        // suite green - the dialog count next door is about the own-deliveries screen and counts
        // dialogs, not the actions that open them.
        var dispatch = await RenderDeliveriesAsync(UserRole.Dispatcher);
        var driver = await RenderMyDeliveriesAsync(StubDeliveryService.Assigned);
        var customer = await RenderMyDeliveriesAsync(StubDeliveryService.Assigned, UserRole.Client);

        Assert.Contains("dt-proof-open", dispatch, StringComparison.Ordinal);
        Assert.Contains("dt-proof-open", driver, StringComparison.Ordinal);
        Assert.Contains("dt-proof-open", customer, StringComparison.Ordinal);

        Assert.Contains("dt-proof-capture", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-proof-capture", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-proof-capture", customer, StringComparison.Ordinal);

        // And neither panel is built until a row is chosen, as the timeline's is not: a component
        // per row would issue a proof read per row on first paint.
        Assert.DoesNotContain("dt-proof-facts", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-proof-facts", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-proof-form", driver, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_reaches_the_own_deliveries_screen_and_its_history_action()
    {
        // The screen serves both narrowed roles, and nothing else in the suite renders it as a
        // client: FR-107 gives them the note field, so the row action has to be there for them too.
        var html = await RenderMyDeliveriesAsync(
            StubDeliveryService.Assigned, UserRole.Client);

        Assert.Contains("dt-delivery-timeline", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Driver, true)]
    [InlineData(UserRole.Client, false)]
    [InlineData(UserRole.Dispatcher, false)]
    [InlineData(UserRole.Admin, false)]
    public void Only_a_driver_is_drawn_the_transition_buttons_on_their_own_deliveries(
        UserRole role,
        bool expected)
    {
        // FR-90, asserted where it actually lives. The render above cannot make this claim: the
        // timeline panel is built only after a row is chosen, and static rendering dispatches no
        // event that could choose one - so inverting the flag would leave every render assertion in
        // this file green while a client was handed the buttons FR-90 says must not be drawn.
        //
        // The two dispatch roles are false here and that is not a contradiction: they read their
        // deliveries on /deliveries, where the panel is opened with the flag set outright.
        Assert.Equal(expected, Web.Components.Pages.MyDeliveries.OffersStatusChange(role));
    }

    [Fact]
    public async Task Only_an_administrator_is_offered_the_deletion_prompt()
    {
        // FR-24 reserves deletion to an admin, and IAccessGuard refuses a dispatcher who asks. FR-12
        // is unchanged by this: the markup hides, the guard decides.
        var admin = await RenderDeliveriesAsync(UserRole.Admin);
        var dispatcher = await RenderDeliveriesAsync(UserRole.Dispatcher);

        Assert.Contains("dt-delivery-delete", admin, StringComparison.Ordinal);
        Assert.Contains("dt-confirm-accept", admin, StringComparison.Ordinal);

        // NFR-19: the prompt is the native element with the ARIA trio written out.
        Assert.Contains(@"aria-modal=""true""", admin, StringComparison.Ordinal);

        Assert.DoesNotContain("dt-delivery-delete", dispatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-confirm-accept", dispatcher, StringComparison.Ordinal);

        // The edit action is a dispatcher's as much as an admin's, so hiding the delete must not
        // have hidden the row's other action with it.
        Assert.Contains("dt-delivery-edit", dispatcher, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_form_embeds_two_pickers_and_a_location_cell_opens_a_third_map()
    {
        // NFR-26 / FR-15 / FR-21: one map component, two jobs. Two pickers are rendered with the
        // form; the read-only one is built when a cell is clicked, which static rendering cannot
        // do - so what is asserted here is that the cell is an action rather than a dead label.
        var html = await RenderDeliveriesAsync(UserRole.Dispatcher);

        Assert.Equal(2, SharedMarkup.Occurrences(html, @"class=""dt-map"""));
        Assert.Contains("dt-delivery-pickup", html, StringComparison.Ordinal);
        Assert.Contains("dt-delivery-dropoff", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_form_shows_the_capacity_the_dispatcher_is_about_to_exceed()
    {
        // FR-103 says the rejection names both figures, and NFR-3 gives the envelope one message
        // per code - so the capacity reaches the dispatcher here, beside the weight input, at the
        // moment they could exceed it.
        var html = await RenderDeliveriesAsync(UserRole.Dispatcher);

        Assert.Contains("Вантажопідйомність автомобіля обраного водія", html, StringComparison.Ordinal);
        Assert.Contains(@"id=""delivery-weight""", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_driver_picker_names_each_drivers_standing_and_whether_they_are_on_duty()
    {
        // FR-98 and FR-116 at the moment a dispatcher chooses who carries a parcel. An <option>
        // holds text and nothing else, so both the rating and the duty marker are composed in C# -
        // which means all four branches are only covered if the stub has one driver of each kind.
        var html = await RenderDeliveriesAsync(UserRole.Dispatcher);

        var options = Regex.Matches(
                html,
                @"<option\b[^>]*>(?<body>.*?)</option>",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5))
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups["body"].Value).Trim())
            .ToArray();

        // 4,5 under uk-UA, which uses a comma for the decimal mark (NFR-15): the average is rendered
        // as an average rather than rounded to a whole star. The marker after it is the driver's
        // open shift, in the same words the shift screen's own driver picker uses - and not the
        // words of the state badge beside it, which labels a shift rather than a person.
        Assert.Contains("Тарас Шевченко — 4,5 — На зміні", options);

        // And the driver nobody has reviewed, who gets the catalogue's "no value" text. Never a
        // zero: that is a rating, and the worst one the scale has.
        //
        // FR-116's other half, and the one worth stating flatly: this driver is off duty and is
        // still in the list. The requirement asks the form to flag rather than to refuse, so an
        // <option> that had been filtered out - or disabled - would fail here rather than quietly
        // turning a note into a rule the guard never agreed to.
        Assert.Contains("Олег Коваль — Немає оцінок — Не на зміні", options);

        var offDuty = Regex.Match(
            html,
            @"<option\b[^>]*>[^<]*Олег Коваль[^<]*</option>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(offDuty.Success, "The picker dropped the off-duty driver instead of marking them.");
        Assert.DoesNotContain("disabled", offDuty.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_capacity_hint_reads_the_vehicle_the_selected_driver_holds()
    {
        // The other half of the assertion above, and the half the render cannot make: static
        // rendering dispatches no events, so the form has no driver selected and the hint is a dash
        // in that test whatever the lookup does. Keying the lookup on the wrong id, or dropping the
        // fleet fetch that feeds it, would leave the hint a dash for every driver and change
        // nothing above - so the number is checked here instead.
        var drivers = new[]
        {
            new DriverSummary(
                new DriverId(1),
                new UserId(10),
                "Тарас",
                "Шевченко",
                "taras@drivetrack.test",
                "ВІ123456",
                5,
                "Рено Мастер",
                "АА1234ВВ",
                Rating: null,
                ReviewCount: 0,

                // FR-116 has no part in the capacity lookup below, and saying so is the point of
                // stating it: a driver's duty state must not change which vehicle they hold.
                OnDuty: true),
            new DriverSummary(
                new DriverId(2),
                new UserId(11),
                "Іван",
                "Коваль",
                "ivan@drivetrack.test",
                "ВІ654321",
                VehicleId: null,
                VehicleModel: null,
                VehicleLicensePlate: null,
                Rating: null,
                ReviewCount: 0,
                OnDuty: false),
        };

        var vehicles = new[]
        {
            new VehicleSummary(5, "Рено Мастер", "АА1234ВВ", 1200m, 42_000, new DateOnly(2026, 12, 1)),
        };

        // The driver holding vehicle 5 shows that vehicle's capacity, not another's and not a dash.
        Assert.Equal(1200m, Web.Components.Pages.Deliveries.CapacityOf(drivers, vehicles, driverId: 1));

        // FR-38 makes a driver without a vehicle legal, and FR-103 leaves them unchecked: there is
        // no figure to show, which is the one case the dash legitimately means.
        Assert.Null(Web.Components.Pages.Deliveries.CapacityOf(drivers, vehicles, driverId: 2));

        // No driver named yet, and a driver the page never fetched: neither invents a capacity.
        Assert.Null(Web.Components.Pages.Deliveries.CapacityOf(drivers, vehicles, driverId: null));
        Assert.Null(Web.Components.Pages.Deliveries.CapacityOf(drivers, vehicles, driverId: 99));

        // The driver holds a vehicle that fell outside the page of fleet rows the form fetched.
        Assert.Null(Web.Components.Pages.Deliveries.CapacityOf(drivers, [], driverId: 1));
    }

    [Fact]
    public async Task The_dispatch_board_offers_every_filter_and_a_single_reset()
    {
        // FR-20 and FR-101: a filter per column, a creation-date range, and one action that clears
        // all of them. A per-field clear would leave a dispatcher hunting for the box still
        // narrowing the list.
        var html = await RenderDeliveriesAsync(UserRole.Dispatcher);

        foreach (var id in new[]
                 {
                     "delivery-filter-driver",
                     "delivery-filter-client",
                     "delivery-filter-pickup",
                     "delivery-filter-dropoff",
                     "delivery-filter-details",
                     "delivery-filter-notes",
                     "delivery-filter-status",
                     "delivery-filter-created-from",
                     "delivery-filter-created-to",
                     "delivery-filter-overdue",
                 })
        {
            Assert.Contains($@"id=""{id}""", html, StringComparison.Ordinal);
        }

        Assert.Contains("Скинути фільтри", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_column_heading_is_a_sort_control()
    {
        // DtDataTable renders whatever rows it is given and sorts nothing, so the control belongs
        // to the consumer - and a heading that was only a label would leave FR-20 unimplemented
        // while the table looked complete.
        var html = await RenderDeliveriesAsync(UserRole.Dispatcher);

        var sortable = HeaderCell.Matches(html)
            .Count(cell => cell.Groups["body"].Value.Contains("<button", StringComparison.Ordinal));

        // Nine sortable columns and the actions column, which is not one.
        Assert.Equal(9, sortable);

        // And the reordering is perceivable: without aria-sort the rows rearrange silently, and a
        // screen-reader user is given no way to tell which column did it. The board opens ordered
        // by the creation date, so exactly one heading claims a direction and the rest claim none.
        Assert.Equal(1, SharedMarkup.Occurrences(html, @"aria-sort=""ascending"""));
        Assert.Equal(8, SharedMarkup.Occurrences(html, @"aria-sort=""none"""));
    }

    [Fact]
    public void Each_heading_sorts_the_column_it_is_labelled_with()
    {
        // The join the two halves above leave open. Counting the buttons proves every column has a
        // control and driving Sort directly proves every column has an ordering, but neither can
        // tell whether the control on the "Driver" heading is wired to the driver column - swap two
        // SortHandler arguments and the screen sorts by the wrong thing while both stay green.
        //
        // Read from source rather than from the render, because the wiring is what is under test:
        // the rendered markup carries no trace of which column a click would sort by.
        var page = SharedMarkup.ReadComponent("Pages", "Deliveries.razor");

        var expected = new Dictionary<DeliveryColumn, string>
        {
            [DeliveryColumn.Driver] = "Driver",
            [DeliveryColumn.Client] = "Client",
            [DeliveryColumn.Pickup] = "Pickup",
            [DeliveryColumn.Dropoff] = "Dropoff",
            [DeliveryColumn.PackageDetails] = "PackageDetails",
            [DeliveryColumn.DeliveryNotes] = "DeliveryNotes",
            [DeliveryColumn.Status] = "Status",
            [DeliveryColumn.CreatedAt] = "CreatedAt",
            [DeliveryColumn.Overdue] = "Overdue",
        };

        // Every member, so a column added to the enum without a heading is caught here rather than
        // shipping as a sort nobody can reach.
        Assert.Equal(Enum.GetValues<DeliveryColumn>().Length, expected.Count);

        var wired = new Dictionary<DeliveryColumn, string>();

        foreach (Match cell in HeaderCell.Matches(page))
        {
            var body = cell.Groups["body"].Value;

            var column = Regex.Match(
                body,
                @"SortHandler\(DeliveryColumn\.(?<name>[A-Za-z]+)\)",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));

            if (!column.Success)
            {
                continue;
            }

            var key = Regex.Match(
                body,
                @"Localizer\[""(?<key>[^""]+)""\]",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));

            Assert.True(key.Success, $"The '{column.Groups["name"].Value}' heading renders no label.");

            wired[Enum.Parse<DeliveryColumn>(column.Groups["name"].Value)] = key.Groups["key"].Value;
        }

        Assert.Equal(expected, wired);
    }

    [Fact]
    public void Each_address_box_is_wired_to_the_point_it_is_labelled_with()
    {
        // The join the PlaceSearch tests below leave open, and the same shape of hole the sort
        // headings above have: driving the holders directly proves each one searches its own query
        // and assigns its own point, but nothing there can tell whether the *dropoff* box's button
        // is wired to the dropoff holder. Swap one argument and a dispatcher's pickup search fills
        // the dropoff list and moves the dropoff map, with every test in this file still green.
        //
        // Read from source, because a statically rendered page carries no trace of an event
        // handler's target - which is exactly why the wiring needs reading rather than rendering.
        var page = SharedMarkup.ReadComponent("Pages", "Deliveries.razor");

        foreach (var point in new[] { "Pickup", "Dropoff" })
        {
            var box = point.ToLowerInvariant();
            var holder = $"_form.{point}Search";

            // The input, its button, and the button on each match: every control in the box names
            // the same holder, and that holder is this point's.
            Assert.Equal(1, SharedMarkup.Occurrences(page, $@"@bind=""{holder}.Query"""));
            Assert.Equal(1, SharedMarkup.Occurrences(page, $"Enter({holder})"));
            Assert.Equal(1, SharedMarkup.Occurrences(page, $"Search({holder})"));
            Assert.Equal(1, SharedMarkup.Occurrences(page, $"Choose({holder}, match)"));

            // And the hooks that name the point are on the controls that carry them, so a renamed
            // class cannot quietly separate the two halves of this assertion.
            Assert.Equal(1, SharedMarkup.Occurrences(page, $@"dt-{box}-search"""));
            Assert.Equal(1, SharedMarkup.Occurrences(page, $@"dt-{box}-match"""));
            Assert.Equal(1, SharedMarkup.Occurrences(page, $@"dt-{box}-empty"""));

            // The list the matches are drawn from is this holder's too.
            Assert.Equal(1, SharedMarkup.Occurrences(page, $"in {holder}.Matches"));
        }
    }

    [Fact]
    public async Task The_own_deliveries_screen_names_no_counterparty_and_says_so_when_empty()
    {
        // FR-27 and FR-96 at the component tier. The type this screen renders has no party field at
        // all, so there is nothing to leak - and the empty state says what is empty rather than
        // rendering the blank rectangle the baseline shipped (FR-84).
        var html = await RenderMyDeliveriesAsync();

        Assert.DoesNotContain("Шевченко", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Петренко", html, StringComparison.Ordinal);
        Assert.Contains("Вам ще не призначено жодної доставки.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_own_deliveries_screen_still_renders_its_rows_and_marks_an_overdue_one()
    {
        var html = await RenderMyDeliveriesAsync(StubDeliveryService.Assigned);

        Assert.Contains("dt-table-scroll", html, StringComparison.Ordinal);
        Assert.Contains("Одна палета", html, StringComparison.Ordinal);
        Assert.Contains("Прострочено", html, StringComparison.Ordinal);
        Assert.Contains("У дорозі", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_column_heading_and_field_label_is_Ukrainian(bool dispatch)
    {
        // NFR-14, asserted on the rendered chrome rather than on the source: a key that resolved to
        // its own name would read as Latin here and nowhere else. The data cells are excluded on
        // purpose - a coordinate is digits and punctuation by nature.
        var html = dispatch
            ? await RenderDeliveriesAsync(UserRole.Dispatcher)
            : await RenderMyDeliveriesAsync();

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

    [Theory]
    [InlineData("Deliveries.razor", "Dispatcher,Admin")]
    [InlineData("MyDeliveries.razor", "Driver,Client")]
    public void Each_screen_is_reserved_to_the_roles_that_have_a_use_for_it(string fileName, string roles)
    {
        // The attribute is what routes a wrong role to /access-denied instead of into an error
        // boundary (FR-79). It is a convenience and not the decision - IAccessGuard settles that,
        // and the API suites assert it - but nothing else pins the attribute, and its absence is
        // invisible in a diff.
        var page = SharedMarkup.ReadComponent("Pages", fileName);

        Assert.Contains(
            $"@attribute [Authorize(Roles = \"{roles}\")]",
            page,
            StringComparison.Ordinal);
    }

    // =====================================================================================
    // FR-104's two address boxes
    //
    // Static rendering dispatches no events, so nothing above can press a search button or click
    // one of its answers. The state and the transitions therefore live on the form, where they can
    // be driven directly - which is the only way the wiring between a box and the point it fills
    // is asserted at all. Cross the two and every render assertion in this file still passes.
    // =====================================================================================

    [Fact]
    public async Task Each_box_searches_its_own_query()
    {
        // The mistake this exists for: SearchDropoffAsync sending the pickup box's text. Both boxes
        // look identical in the markup and a rendered page cannot tell the two apart.
        var form = new DeliveryForm();
        var asked = new List<string?>();

        form.PickupSearch.Query = "Хрещатик";
        form.DropoffSearch.Query = "Площа Ринок";

        await form.DropoffSearch.RunAsync(Recording(asked, []), TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { "Площа Ринок" }, asked);

        await form.PickupSearch.RunAsync(Recording(asked, []), TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { "Площа Ринок", "Хрещатик" }, asked);
    }

    [Fact]
    public async Task Choosing_a_match_sets_that_box_s_point_and_leaves_the_other_alone()
    {
        // The other half of the same mistake: ChooseDropoff assigning _form.Pickup. The dropoff map
        // would then stay where it was and the pickup map would jump to an address nobody chose for
        // it, and no render test could see either.
        var form = new DeliveryForm();
        var match = new PlaceMatch("Львів, площа Ринок, 1", new MapLocation(49.8419, 24.0315));

        await form.DropoffSearch.RunAsync(Answering([match]), TestContext.Current.CancellationToken);

        Assert.Single(form.DropoffSearch.Matches);
        Assert.Empty(form.PickupSearch.Matches);

        form.DropoffSearch.Choose(match);

        Assert.Equal(match.Point, form.Dropoff);
        Assert.Null(form.Pickup);

        // The list is cleared with the choice: one left standing beside a map that has already moved
        // invites a second click on a match that has already been applied.
        Assert.Empty(form.DropoffSearch.Matches);
        Assert.False(form.DropoffSearch.FoundNothing);
    }

    [Fact]
    public async Task A_search_that_matched_nothing_says_so_and_an_untouched_box_says_nothing()
    {
        // "No search has run" and "a search found nothing" are different states, and only the second
        // has anything to tell the dispatcher. One flag for both would make the hint appear under a
        // box nobody has used yet.
        var form = new DeliveryForm();

        Assert.False(form.PickupSearch.FoundNothing);

        await form.PickupSearch.RunAsync(Answering([]), TestContext.Current.CancellationToken);

        Assert.True(form.PickupSearch.FoundNothing);
        Assert.False(form.DropoffSearch.FoundNothing);
    }

    [Fact]
    public async Task A_refused_search_answers_the_failure_keys_and_leaves_the_box_usable()
    {
        // The 422 a query under three characters earns. The screen renders these keys through the
        // same catalogue the REST envelope uses (NFR-3), and the box has to come back out of its
        // busy state or the button stays disabled and reads as a hung screen.
        //
        // The refusal is left on the box rather than returned (DW-41): a caller handed it can put it
        // anywhere, and where it used to be put was the screen's one failure field, which the submit
        // path also writes.
        var form = new DeliveryForm();

        await form.PickupSearch.RunAsync(
            (_, _) => throw new ValidationException(
                ErrorCode.COMMON_VALIDATION_FAILED,
                "Too short.",
                []),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(form.PickupSearch.Failures);
        Assert.False(form.PickupSearch.IsBusy);
        Assert.Empty(form.PickupSearch.Matches);

        // And it is not also reported as a search that found nothing. The box's banner renders
        // directly above that line, so both at once would answer one click with two different
        // sentences - "this is why it was refused" and "there was nothing to find".
        Assert.False(form.PickupSearch.FoundNothing);
    }

    [Fact]
    public async Task A_box_is_busy_only_while_its_own_lookup_is_running()
    {
        // The busy flag is what disables the button, so an impatient second click cannot become a
        // second request to a public geocoder. Per box: searching for a pickup must not disable the
        // dropoff box.
        var form = new DeliveryForm();
        var observed = new List<(bool Pickup, bool Dropoff)>();

        await form.PickupSearch.RunAsync(
            (_, _) =>
            {
                observed.Add((form.PickupSearch.IsBusy, form.DropoffSearch.IsBusy));

                return Task.FromResult<IReadOnlyList<PlaceMatch>>([]);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([(true, false)], observed);
        Assert.False(form.PickupSearch.IsBusy);
    }

    [Fact]
    public async Task Each_box_on_the_request_form_searches_its_own_query()
    {
        // The client's form builds its own pair of boxes, so it can cross them in its own
        // constructor - and swapping the two lambdas there compiles, renders byte-identical markup
        // and leaves every render assertion in this file green. This is the only thing that sees it.
        var form = new RequestForm();
        var asked = new List<string?>();

        form.PickupSearch.Query = "Хрещатик";
        form.DropoffSearch.Query = "Площа Ринок";

        await form.DropoffSearch.RunAsync(Recording(asked, []), TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { "Площа Ринок" }, asked);

        await form.PickupSearch.RunAsync(Recording(asked, []), TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { "Площа Ринок", "Хрещатик" }, asked);
    }

    [Fact]
    public async Task Choosing_a_match_on_the_request_form_sets_that_box_s_point_and_leaves_the_other_alone()
    {
        // The other half of the same mistake, on the client's form: a dropoff box wired to the
        // pickup point would leave the dropoff map where it was and jump the pickup map to an
        // address nobody chose for it, and the request would be sent with both.
        var form = new RequestForm();
        var match = new PlaceMatch("Львів, площа Ринок, 1", new MapLocation(49.8419, 24.0315));

        await form.DropoffSearch.RunAsync(Answering([match]), TestContext.Current.CancellationToken);

        Assert.Single(form.DropoffSearch.Matches);
        Assert.Empty(form.PickupSearch.Matches);

        form.DropoffSearch.Choose(match);

        Assert.Equal(match.Point, form.Dropoff);
        Assert.Null(form.Pickup);

        // And the pickup box fills the pickup point, which is the half a one-sided assertion would
        // pass for a form that wired both boxes to the same field.
        var pickup = new PlaceMatch("Київ, вулиця Хрещатик, 1", new MapLocation(50.4472, 30.5222));

        form.PickupSearch.Choose(pickup);

        Assert.Equal(pickup.Point, form.Pickup);
        Assert.Equal(match.Point, form.Dropoff);
    }

    [Fact]
    public void The_request_form_sends_each_point_as_the_field_it_filled()
    {
        // The last place the pair can be crossed: the two points are adjacent arguments of the same
        // type in the command's constructor, so swapping them compiles, renders byte-identical
        // markup and leaves every assertion above green - and the parcel is then collected at the
        // address it was meant to be delivered to.
        var form = new RequestForm
        {
            Pickup = new MapLocation(50.4472, 30.5222),
            Dropoff = new MapLocation(49.8419, 24.0315),
            PackageDetails = "Одна палета",
            PackageWeightKg = 12.5m,
            DeliveryNotes = "Подзвонити за годину",
        };

        var command = form.ToCommand();

        Assert.Equal(new LocationInput(50.4472, 30.5222), command.Pickup);
        Assert.Equal(new LocationInput(49.8419, 24.0315), command.Dropoff);
        Assert.Equal("Одна палета", command.PackageDetails);
        Assert.Equal(12.5m, command.PackageWeightKg);
        Assert.Equal("Подзвонити за годину", command.DeliveryNotes);
    }

    [Fact]
    public void An_unpicked_point_is_sent_as_nothing_rather_than_as_a_guess()
    {
        // Null rather than a zero coordinate, which is a real place off the coast of Africa: the
        // capability refuses an absent point by name, and a guessed one it would accept.
        var command = new RequestForm().ToCommand();

        Assert.Null(command.Pickup);
        Assert.Null(command.Dropoff);
    }

    /// <summary>A search that records the query it was given and answers a fixed list.</summary>
    private static Func<SearchPlacesQuery, CancellationToken, Task<IReadOnlyList<PlaceMatch>>> Recording(
        List<string?> asked,
        IReadOnlyList<PlaceMatch> matches) =>
        (query, _) =>
        {
            asked.Add(query.Query);

            return Task.FromResult(matches);
        };

    /// <inheritdoc cref="Recording" />
    private static Func<SearchPlacesQuery, CancellationToken, Task<IReadOnlyList<PlaceMatch>>> Answering(
        IReadOnlyList<PlaceMatch> matches) =>
        (_, _) => Task.FromResult(matches);

    // =====================================================================================
    // The two pure functions behind the dispatch board
    // =====================================================================================

    [Fact]
    public void A_blank_filter_narrows_nothing()
    {
        // An empty filter panel is not a filter. Without this, a `Contains(value, "")` that
        // answered false would hide every row on first paint.
        Assert.True(Web.Components.Pages.Deliveries.Matches(Row(), new DeliveryFilter()));
    }

    [Theory]
    [InlineData("шевченко", true)]
    [InlineData("ШЕВЧЕНКО", true)]
    [InlineData("  Шевченко  ", true)]
    [InlineData("Франко", false)]
    public void The_driver_filter_matches_the_name_whatever_case_it_is_typed_in(string needle, bool expected)
    {
        Assert.Equal(
            expected,
            Web.Components.Pages.Deliveries.Matches(Row(), new DeliveryFilter { Driver = needle }));
    }

    [Fact]
    public void A_row_with_no_driver_is_narrowed_away_by_a_driver_filter()
    {
        // The case a `?? string.Empty` in the wrong place would get backwards: an unassigned
        // delivery matches nothing a dispatcher could type into the driver box.
        var unassigned = Row(assigned: false);

        Assert.False(Web.Components.Pages.Deliveries.Matches(
            unassigned,
            new DeliveryFilter { Driver = "Шевченко" }));

        Assert.True(Web.Components.Pages.Deliveries.Matches(unassigned, new DeliveryFilter()));
    }

    [Fact]
    public void The_location_filters_run_over_what_the_cell_shows()
    {
        // Which is the address when one is resolved and the coordinates when none is - so a
        // dispatcher can find a delivery by whatever the table is actually showing them.
        Assert.True(Web.Components.Pages.Deliveries.Matches(
            Row(),
            new DeliveryFilter { Pickup = "50.4501" }));

        Assert.True(Web.Components.Pages.Deliveries.Matches(
            Row(),
            new DeliveryFilter { Dropoff = "площа Ринок" }));
    }

    [Fact]
    public void Every_filter_narrows_at_once_rather_than_in_turn()
    {
        // The conjunction, which a per-column implementation would get wrong by taking the last box
        // typed into rather than all of them.
        Assert.False(Web.Components.Pages.Deliveries.Matches(
            Row(),
            new DeliveryFilter { Driver = "Шевченко", PackageDetails = "Холодильник" }));
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending, true)]
    [InlineData(DeliveryStatus.Delivered, false)]
    public void The_status_filter_shows_one_status_and_a_null_shows_them_all(
        DeliveryStatus status,
        bool expected)
    {
        Assert.Equal(
            expected,
            Web.Components.Pages.Deliveries.Matches(Row(), new DeliveryFilter { Status = status }));

        Assert.True(Web.Components.Pages.Deliveries.Matches(
            Row(),
            new DeliveryFilter { Status = null }));
    }

    [Theory]
    // The row was created on the seventh, and the range is inclusive at both ends.
    [InlineData(7, 7, true)]
    [InlineData(1, 30, true)]
    [InlineData(8, 30, false)]
    [InlineData(1, 6, false)]
    public void The_creation_range_is_inclusive_at_both_ends(int fromDay, int toDay, bool expected)
    {
        var filter = new DeliveryFilter
        {
            CreatedFrom = new DateOnly(2026, 9, fromDay),
            CreatedTo = new DateOnly(2026, 9, toDay),
        };

        Assert.Equal(expected, Web.Components.Pages.Deliveries.Matches(Row(created: Local(2026, 9, 7)), filter));
    }

    [Fact]
    public void The_overdue_filter_keeps_only_the_rows_whose_window_has_passed()
    {
        Assert.False(Web.Components.Pages.Deliveries.Matches(
            Row(overdue: false),
            new DeliveryFilter { OverdueOnly = true }));

        Assert.True(Web.Components.Pages.Deliveries.Matches(
            Row(overdue: true),
            new DeliveryFilter { OverdueOnly = true }));
    }

    [Fact]
    public void Resetting_is_a_new_filter_so_every_box_clears_at_once()
    {
        // FR-101, asserted on the shape rather than on the action: a fresh filter narrows nothing,
        // which is what the reset button produces.
        var narrowed = new DeliveryFilter { Driver = "Шевченко", OverdueOnly = true };

        Assert.False(Web.Components.Pages.Deliveries.Matches(Row(overdue: false), narrowed));
        Assert.True(Web.Components.Pages.Deliveries.Matches(Row(overdue: false), new DeliveryFilter()));
    }

    [Fact]
    public void A_second_click_on_a_heading_reverses_the_order_it_produced()
    {
        // The acceptance criterion, asserted where it actually lives: sorting is a function of the
        // rows already fetched, so "no second request is issued" is a property of a pure method
        // rather than a claim about the network.
        var rows = new[]
        {
            Row(id: 1, details: "Б"),
            Row(id: 2, details: "А"),
            Row(id: 3, details: "В"),
        };

        var ascending = Web.Components.Pages.Deliveries.Sort(
            rows, DeliveryColumn.PackageDetails, descending: false);

        var descending = Web.Components.Pages.Deliveries.Sort(
            rows, DeliveryColumn.PackageDetails, descending: true);

        Assert.Equal(new[] { 2, 1, 3 }, ascending.Select(row => row.Id).ToArray());
        Assert.Equal(new[] { 3, 1, 2 }, descending.Select(row => row.Id).ToArray());
    }

    [Fact]
    public void Sorting_leaves_the_rows_it_was_given_untouched()
    {
        // Pure, so the screen can re-sort on every render without the fetched page drifting.
        var rows = new[] { Row(id: 2, details: "Б"), Row(id: 1, details: "А") };

        Web.Components.Pages.Deliveries.Sort(rows, DeliveryColumn.PackageDetails, descending: false);

        Assert.Equal(new[] { 2, 1 }, rows.Select(row => row.Id).ToArray());
    }

    [Fact]
    public void Rows_that_compare_equal_keep_a_stable_order()
    {
        // Without the tie-break every render could hand the dispatcher a different order for the
        // same data, which reads as the table refreshing under them.
        var rows = new[] { Row(id: 3, details: "А"), Row(id: 1, details: "А"), Row(id: 2, details: "А") };

        Assert.Equal(
            new[] { 1, 2, 3 },
            Web.Components.Pages.Deliveries
                .Sort(rows, DeliveryColumn.PackageDetails, descending: false)
                .Select(row => row.Id)
                .ToArray());
    }

    [Fact]
    public void The_status_column_sorts_by_the_lifecycle_rather_than_by_its_label()
    {
        // Alphabetically "Доставлено" precedes "Очікує", which would put a finished delivery above
        // a waiting one for no reason a dispatcher could name.
        var rows = new[]
        {
            Row(id: 1, status: DeliveryStatus.Delivered),
            Row(id: 2, status: DeliveryStatus.Pending),
            Row(id: 3, status: DeliveryStatus.InTransit),
        };

        Assert.Equal(
            new[] { DeliveryStatus.Pending, DeliveryStatus.InTransit, DeliveryStatus.Delivered },
            Web.Components.Pages.Deliveries
                .Sort(rows, DeliveryColumn.Status, descending: false)
                .Select(row => row.Status)
                .ToArray());
    }

    [Fact]
    public void Every_column_the_headings_offer_has_an_ordering()
    {
        // The switch is total over the enum, and a column added without an ordering throws rather
        // than quietly rendering the rows in whatever order they arrived. Walked rather than listed
        // as theory data, so a column added later is covered without anyone remembering to add a
        // row here - and because the enum is internal, which a public theory parameter cannot be.
        //
        // Rows with a null driver and null notes are included on purpose: those are the arms a bare
        // key selector throws on.
        var rows = new[] { Row(id: 1), Row(id: 2, assigned: false, notes: null) };
        var columns = Enum.GetValues<DeliveryColumn>();

        Assert.NotEmpty(columns);

        foreach (var column in columns)
        {
            Assert.Equal(2, Web.Components.Pages.Deliveries.Sort(rows, column, descending: false).Count);
        }
    }

    // -------------------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------------------

    private static Task<string> RenderDeliveriesAsync(UserRole role) =>
        ComponentRenderer.RenderAsync<Web.Components.Pages.Deliveries>(
            parameters: null,
            services =>
            {
                services.AddSingleton<ICurrentUser>(new StubCaller(role));
                services.AddSingleton<IDeliveryService>(new StubDeliveryService());
                services.AddSingleton<IDriverService>(new StubDriverService());
                services.AddSingleton<IVehicleService>(new StubVehicleService());
                services.AddSingleton<IClientAdministrationService>(new StubClientRoster());
            });

    private static Task<string> RenderMyDeliveriesAsync(
        IReadOnlyList<AssignedDeliverySummary>? rows = null,
        UserRole role = UserRole.Driver) =>
        ComponentRenderer.RenderAsync<Web.Components.Pages.MyDeliveries>(
            parameters: null,
            services =>
            {
                services.AddSingleton<ICurrentUser>(new StubCaller(role));
                services.AddSingleton<IDeliveryService>(new StubDeliveryService(rows ?? []));
            });

    private static DateTimeOffset Local(int year, int month, int day) =>
        new DateTimeOffset(new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Local)).ToUniversalTime();

    /// <summary>One dispatch row, with every field a filter or an ordering reads.</summary>
    private static DeliverySummary Row(
        int id = 1,
        bool assigned = true,
        string details = "Одна палета",
        string? notes = "Подзвонити",
        DeliveryStatus status = DeliveryStatus.Pending,
        DateTimeOffset? created = null,
        bool overdue = false) =>
        new(
            id,
            assigned ? new DeliveryParty(1, "Тарас Шевченко") : null,
            new DeliveryParty(2, "Олена Петренко"),
            new LocationView(new MapLocation(50.4501, 30.5234), null),
            new LocationView(new MapLocation(49.8397, 24.0297), "Львів, площа Ринок"),
            details,
            12.5m,
            notes,
            null,
            null,
            status,
            created ?? Noon,
            overdue);

    /// <summary>A signed-in caller of a chosen role.</summary>
    private sealed class StubCaller(UserRole role) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public UserId UserId => new(1);

        public UserRole Role => role;

        public DriverId? DriverId => role == UserRole.Driver ? new DriverId(1) : null;

        public ClientId? ClientId => role == UserRole.Client ? new ClientId(2) : null;
    }

    /// <summary>
    /// Three deliveries for the dispatch board: one with both parties, one with neither, and one
    /// that is overdue. Writes are not exercised — static rendering dispatches no events — so they
    /// answer rather than record.
    /// </summary>
    private sealed class StubDeliveryService(IReadOnlyList<AssignedDeliverySummary>? assigned = null)
        : IDeliveryService
    {
        /// <summary>What the own-deliveries screen renders when it has rows.</summary>
        internal static readonly AssignedDeliverySummary[] Assigned =
        [
            new(
                1,
                new LocationView(new MapLocation(50.4501, 30.5234), null),
                new LocationView(new MapLocation(49.8397, 24.0297), "Львів, площа Ринок"),
                "Одна палета",
                12.5m,
                null,
                null,
                Noon,
                DeliveryStatus.InTransit,
                Noon,
                true),
        ];

        private static readonly DeliverySummary[] Board =
        [
            new(
                1,
                new DeliveryParty(1, "Тарас Шевченко"),
                new DeliveryParty(2, "Олена Петренко"),
                new LocationView(new MapLocation(50.4501, 30.5234), null),
                new LocationView(new MapLocation(49.8397, 24.0297), "Львів, площа Ринок"),
                "Одна палета",
                12.5m,
                "Подзвонити",
                null,
                Noon,
                DeliveryStatus.Pending,
                Noon,
                true),
            new(
                2,
                null,
                null,
                new LocationView(new MapLocation(50.4501, 30.5234), null),
                new LocationView(new MapLocation(49.8397, 24.0297), null),
                "Дві коробки",
                4m,
                null,
                null,
                null,
                DeliveryStatus.Delivered,
                Noon,
                false),
        ];

        public Task<IReadOnlyList<DeliverySummary>> ListAsync(
            ListDeliveriesQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeliverySummary>>(Board);

        public Task<IReadOnlyList<AssignedDeliverySummary>> ListMineAsync(
            ListDeliveriesQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(assigned ?? []);

        public Task<DeliverySummary> GetAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(Board[0]);

        public Task<AssignedDeliverySummary> GetMineAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The dispatch board never reads one delivery as its own.");

        public Task<IReadOnlyList<DeliverySummary>> ListByIdsAsync(
            IReadOnlyCollection<int> ids,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The dispatch board reads a page, not a set of ids.");

        public Task<DeliverySummary> CreateAsync(
            CreateDeliveryCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Board[0]);

        // Story 7.4's client request, for the reason every write above answers rather than records:
        // static rendering dispatches no events, so nothing here is ever called by a screen test -
        // it exists because widening IDeliveryService widens every implementation of it.
        public Task<AssignedDeliverySummary> RequestAsync(
            RequestDeliveryCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Assigned[0]);

        public Task<DeliverySummary> UpdateAsync(
            int id,
            UpdateDeliveryCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Board[0]);

        public Task DeleteAsync(int id, CancellationToken cancellationToken) => Task.CompletedTask;

        // Story 5.3's three members. Answering rather than recording, for the reason the writes
        // above do: static rendering dispatches no events, so nothing here is ever called by a
        // screen test - they exist because widening IDeliveryService widens every implementation of
        // it, which is the compile-time consequence the stub is here to carry.
        public Task<TimelineEntryView> ChangeStatusAsync(
            int id,
            ChangeDeliveryStatusCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Entry);

        public Task<TimelineEntryView> AddNoteAsync(
            int id,
            AddDeliveryNoteCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Entry);

        public Task<IReadOnlyList<TimelineEntryView>> ListTimelineAsync(
            int id,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TimelineEntryView>>([Entry]);

        // Story 5.2's address search, for the reason the three above are here: static rendering
        // dispatches no events, so no render test can press the search button - the member exists
        // because widening IDeliveryService widens every implementation of it.
        public Task<IReadOnlyList<PlaceMatch>> SearchPlacesAsync(
            SearchPlacesQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlaceMatch>>([]);

        private static TimelineEntryView Entry => new(
            1,
            "Тарас Шевченко",
            UserRole.Dispatcher,
            DeliveryStatus.Pending,
            DeliveryStatus.InTransit,
            null,
            Noon);
    }

    /// <summary>
    /// Two drivers: one holding the vehicle the capacity hint reads, and one holding nothing.
    /// <para>
    /// They differ in their standing as well, because the assignment picker renders it (FR-98) and
    /// the two branches of that are what a single stub cannot exercise: a rated driver shows the
    /// average, an unrated one shows the "no value" text and never a zero.
    /// </para>
    /// </summary>
    private sealed class StubDriverService : IDriverService
    {
        internal static readonly DriverSummary Rated = new(
            new DriverId(1),
            new UserId(10),
            "Тарас",
            "Шевченко",
            "taras@drivetrack.test",
            "ВІ123456",
            5,
            "Рено Мастер",
            "АА1234ВВ",
            Rating: 4.5,
            ReviewCount: 2,

            // FR-116: on duty, so the picker marks them as such. The pair below is one of each, so
            // both branches of the marker are covered by the one render.
            OnDuty: true);

        internal static readonly DriverSummary Unrated = new(
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

            // FR-116: off duty, and still offered. The assertion that this driver is in the picker
            // at all is what makes "flag, never block" a property of the screen.
            OnDuty: false);

        private static readonly DriverSummary Driver = Rated;

        public Task<IReadOnlyList<DriverSummary>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DriverSummary>>([Rated, Unrated]);

        public Task<DriverSummary> GetAsync(DriverId id, CancellationToken cancellationToken) =>
            Task.FromResult(Driver);

        public Task<DriverSummary> CreateAsync(
            CreateDriverCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Driver);

        public Task<DriverSummary> UpdateAsync(
            DriverId id,
            UpdateDriverCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Driver);

        public Task DeleteAsync(DriverId id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubVehicleService : IVehicleService
    {
        private static readonly VehicleSummary Held =
            new(5, "Рено Мастер", "АА1234ВВ", 1200m, 42_000, new DateOnly(2026, 12, 1));

        public Task<IReadOnlyList<VehicleSummary>> ListAsync(
            ListVehiclesQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VehicleSummary>>([Held]);

        public Task<IReadOnlyList<VehicleSummary>> ListUnassignedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VehicleSummary>>([]);

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

    private sealed class StubClientRoster : IClientAdministrationService
    {
        private static readonly ClientAccount Client = new(
            new UserId(11),
            new ClientId(2),
            "Олена",
            "Петренко",
            "olena@drivetrack.test",
            "+380441234567");

        public Task<IReadOnlyList<ClientAccount>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClientAccount>>([Client]);

        public Task<ClientAccount> GetAsync(UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult(Client);

        public Task<ClientAccount> UpdateAsync(
            UserId userId,
            UpdateClientCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Client);

        public Task DeleteAsync(UserId userId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
