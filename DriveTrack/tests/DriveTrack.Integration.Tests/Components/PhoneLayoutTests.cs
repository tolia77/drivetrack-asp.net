namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// The join between the four fleet screens' markup and the phone layer that reads it.
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
    /// The four screens, each with the wrapper its stylesheet anchors on and the class its cells
    /// mark their own name with. A table rather than four near-identical facts, so a fifth screen
    /// joining the collapse is a row.
    /// </summary>
    public static TheoryData<string, string, string> Screens() => new()
    {
        { "Vehicles", "dt-vehicles", "dt-vehicle-label" },
        { "Drivers", "dt-drivers", "dt-driver-label" },
        { "Shifts", "dt-shifts", "dt-shift-label" },
        { "MyShifts", "dt-my-shifts", "dt-shift-label" },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task The_phone_collapse_is_anchored_on_a_wrapper_the_screen_really_renders(
        string screen,
        string wrapper,
        string label)
    {
        // One half: the element is on the page. Rendered rather than read off the markup, because
        // what the stylesheet needs is a wrapper in the output around the table - and a screen can
        // lose that while the file still contains the class somewhere.
        var html = await RenderAsync(screen);

        Assert.Contains($@"class=""{wrapper}""", html, StringComparison.Ordinal);

        // The other half: the stylesheet hangs off that same name. `::deep` on the head, because
        // the `<thead>` is DtDataTable's element and carries that component's scope - without it
        // the rule compiles to one that matches nothing and the head survives the collapse,
        // naming columns that are no longer laid out as columns.
        var css = SharedMarkup.ReadComponent("Pages", screen + ".razor.css");

        Assert.Contains($".{wrapper} ::deep thead", css, StringComparison.Ordinal);

        // And the label reveal is anchored on the wrapper too, without a `::deep`: the span is
        // written in this screen's own RowTemplate, so it already carries this scope. That is the
        // rule that replaces the dropped head, so an unanchored copy of it would leak the reveal
        // onto every other table this screen might one day hold.
        Assert.Contains($".{wrapper} .{label}", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// One of the four screens, rendered through the stubs its own suite already keeps. Borrowed
    /// rather than duplicated: a second set of stubs is a second answer to what these screens are
    /// given, and this class has no opinion about that.
    /// </summary>
    private static Task<string> RenderAsync(string screen) => screen switch
    {
        "Vehicles" => FleetScreenTests.RenderVehiclesAsync(),
        "Drivers" => FleetScreenTests.RenderDriversAsync(),
        "Shifts" => ShiftScreenTests.RenderRosterAsync(),
        "MyShifts" => ShiftScreenTests.RenderOwnAsync(ShiftScreenTests.Roster),
        _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, "No such screen."),
    };
}
