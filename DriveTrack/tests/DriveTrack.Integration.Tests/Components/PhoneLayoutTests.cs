using System.Text.RegularExpressions;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// The join between a screen's markup and the phone layer that reads it.
/// <para>
/// Everything else about these screens is asserted on one side or the other: the render tests read
/// the HTML and never open a stylesheet, and <c>DesignTokenTests</c> reads the stylesheets for the
/// values their declarations carry and never looks at a selector. Between the two, the wrapper
/// <c>&lt;div&gt;</c> and the <c>::deep</c> that hangs off it are load-bearing and unasserted -
/// delete either and the rows stay a table on a phone with the whole suite green.
/// </para>
/// <para>
/// A silent failure by construction, which is why it is worth a class. A scoped rule written
/// without <c>::deep</c> compiles to a selector carrying this component's scope attribute on an
/// element DtDataTable rendered, so it matches nothing and the browser reports nothing; a dropped
/// wrapper does the same from the other end. Read from source in the style
/// <c>DataTableTests.The_header_sticks_inside_the_scroll_container_and_paints_over_the_rows</c>
/// reads DtDataTable's own <c>::deep</c>, and paired with a render so the wrapper is asserted where
/// it actually has to appear rather than only where it is written.
/// </para>
/// </summary>
public class PhoneLayoutTests
{
    /// <summary>
    /// Every screen that collapses its table on a phone: the directory it lives in under
    /// <c>Components/Pages</c>, the wrapper its stylesheet anchors on and the class its cells mark
    /// their own name with. A table rather than a run of near-identical facts, so a screen joining
    /// the collapse is a row.
    /// <para>
    /// The directory is blank for the screens that sit directly under <c>Pages</c>:
    /// <c>Path.Combine</c> drops an empty segment, so one reader serves both depths without a
    /// branch.
    /// </para>
    /// </summary>
    public static TheoryData<string, string, string, string> Screens() => new()
    {
        { "Vehicles", "", "dt-vehicles", "dt-vehicle-label" },
        { "Drivers", "", "dt-drivers", "dt-driver-label" },
        { "Shifts", "", "dt-shifts", "dt-shift-label" },
        { "MyShifts", "", "dt-my-shifts", "dt-shift-label" },
        { "MyDeliveries", "", "dt-my-deliveries", "dt-my-delivery-label" },
        { "Clients", "Admin", "dt-clients", "dt-client-label" },
        { "Dispatchers", "Admin", "dt-dispatchers", "dt-dispatcher-label" },
        { "Notifications", "Admin", "dt-notifications", "dt-notification-label" },
        { "Reviews", "", "dt-reviews", "dt-review-label" },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task The_phone_collapse_is_anchored_on_a_wrapper_the_screen_really_renders(
        string screen,
        string directory,
        string wrapper,
        string label)
    {
        // One half: the element is on the page. Rendered rather than read off the markup, because
        // what the stylesheet needs is a wrapper in the output around the table - and a screen can
        // lose that while the file still contains the class somewhere.
        var html = await RenderAsync(screen);

        // The table inside the wrapper rather than the class somewhere on the page. Every rule
        // below the breakpoint is written `.{wrapper} ::deep …`, so a closing `</div>` moved above
        // the table leaves the class rendered, this assertion's weaker form satisfied and every
        // collapse rule matching nothing - which is the silent failure this class exists for, from
        // the one direction a `Contains` cannot see.
        var body = BodyOf(html, wrapper);

        Assert.Contains("dt-table-scroll", body, StringComparison.Ordinal);
        Assert.Contains("dt-table-row", body, StringComparison.Ordinal);

        // The other half: the stylesheet hangs off that same name. `::deep` on the head, because
        // the `<thead>` is DtDataTable's element and carries that component's scope - without it
        // the rule compiles to one that matches nothing and the head survives the collapse,
        // naming columns that are no longer laid out as columns.
        var css = SharedMarkup.ReadComponent("Pages", directory, screen + ".razor.css");

        Assert.Contains($".{wrapper} ::deep thead", css, StringComparison.Ordinal);

        // And the label reveal is anchored on the wrapper too, without a `::deep`: the span is
        // written in this screen's own RowTemplate, so it already carries this scope. That is the
        // rule that replaces the dropped head, so an unanchored copy of it would leak the reveal
        // onto every other table this screen might one day hold.
        Assert.Contains($".{wrapper} .{label}", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The markup inside one wrapper <c>&lt;div&gt;</c>, walked to its own closing tag rather than
    /// to the first one: the table it holds renders several <c>&lt;div&gt;</c>s of its own, so a
    /// non-greedy match would stop inside the very thing being looked for.
    /// <para>
    /// Internal because <c>ReviewScreenTests</c> asks the same question of the one wrapper with
    /// two possible occupants, and a second copy of a tag walker is a second set of bugs.
    /// </para>
    /// </summary>
    internal static string BodyOf(string html, string wrapper)
    {
        var opening = new Regex(
            $@"<div\b[^>]*class=""{Regex.Escape(wrapper)}""[^>]*>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        var start = opening.Match(html);

        Assert.True(start.Success, $@"The screen renders no <div class=""{wrapper}"">.");

        var body = start.Index + start.Length;
        var depth = 1;

        var tags = new Regex(
            @"<(?<close>/?)div\b[^>]*>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        foreach (Match tag in tags.Matches(html, body))
        {
            depth += tag.Groups["close"].Value.Length == 0 ? 1 : -1;

            if (depth == 0)
            {
                return html[body..tag.Index];
            }
        }

        Assert.Fail($@"The <div class=""{wrapper}""> is never closed.");

        return string.Empty;
    }

    /// <summary>
    /// One of the screens, rendered through the stubs its own suite already keeps. Borrowed rather
    /// than duplicated: a second set of stubs is a second answer to what these screens are given,
    /// and this class has no opinion about that.
    /// </summary>
    private static Task<string> RenderAsync(string screen) => screen switch
    {
        "Vehicles" => FleetScreenTests.RenderVehiclesAsync(),
        "Drivers" => FleetScreenTests.RenderDriversAsync(),
        "Shifts" => ShiftScreenTests.RenderRosterAsync(),
        "MyShifts" => ShiftScreenTests.RenderOwnAsync(ShiftScreenTests.Roster),
        "MyDeliveries" => DeliveryScreenTests.RenderOwnDeliveriesAsync(),
        "Clients" => AdministrationScreenTests.RenderClientsAsync(),
        "Dispatchers" => AdministrationScreenTests.RenderDispatchersAsync(),
        "Notifications" => NotificationScreenTests.RenderLogAsync(),
        "Reviews" => ReviewScreenTests.RenderReviewsAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, "No such screen."),
    };
}
