using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// The join between a screen's markup and the phone layer that reads it.
/// <para>
/// Everything else about these screens is asserted on one side or the other: the render tests read
/// the HTML and never open a stylesheet, and <c>DesignTokenTests</c> reads the stylesheets for the
/// values their declarations carry and never looks at a selector. Between the two, the wrapper
/// <c>&lt;div&gt;</c> and the class on it that turns the collapse on are load-bearing and
/// unasserted - drop either and the rows stay a table on a phone with the whole suite green.
/// </para>
/// <para>
/// A silent failure by construction, which is why it is worth a class. The collapse is one block
/// in <c>_theme.scss</c> keyed on <c>dt-table-cards</c>, so there is no longer a per-screen
/// <c>::deep</c> to get wrong - what is left is the opt-in itself, and forgetting it produces a
/// screen that renders perfectly and scrolls sideways on a handset. Read from source in the style
/// <c>DataTableTests.The_header_sticks_inside_the_scroll_container_and_paints_over_the_rows</c>
/// reads DtDataTable's own <c>::deep</c>, and paired with a render so the wrapper is asserted where
/// it actually has to appear rather than only where it is written.
/// </para>
/// </summary>
public class PhoneLayoutTests
{
    /// <summary>The class a screen puts on its wrapper to take the shared collapse.</summary>
    private const string OptIn = "dt-table-cards";

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
        { "Deliveries", "", "dt-deliveries", "dt-delivery-label" },
        { "MyDeliveries", "", "dt-my-deliveries", "dt-my-delivery-label" },
        { "Clients", "Admin", "dt-clients", "dt-client-label" },
        { "Dispatchers", "Admin", "dt-dispatchers", "dt-dispatcher-label" },
        { "Notifications", "Admin", "dt-notifications", "dt-notification-label" },
        { "Reviews", "", "dt-reviews", "dt-review-label" },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task The_phone_collapse_is_opted_into_on_a_wrapper_the_screen_really_renders(
        string screen,
        string directory,
        string wrapper,
        string label)
    {
        // One half: the element is on the page, with both classes on it. Rendered rather than read
        // off the markup, because what the collapse needs is a wrapper in the output around the
        // table - and a screen can lose that while the file still contains the class somewhere.
        var html = await RenderAsync(screen);

        // Both names on the same element, which is the whole of the opt-in. Asserted against the
        // opening tag rather than against the page, or a screen that carried `dt-table-cards` on
        // some other element entirely would satisfy it while its own table stayed a table.
        var classes = ClassesOf(html, wrapper);

        Assert.Contains(OptIn, classes);

        // The table inside the wrapper rather than the class somewhere on the page. Every rule in
        // the shared block is written `.dt-table-cards …`, so a closing `</div>` moved above the
        // table leaves the class rendered, this assertion's weaker form satisfied and every
        // collapse rule matching nothing - which is the silent failure this class exists for, from
        // the one direction a `Contains` cannot see.
        var body = BodyOf(html, wrapper);

        Assert.Contains("dt-table-scroll", body, StringComparison.Ordinal);
        Assert.Contains("dt-table-row", body, StringComparison.Ordinal);

        // The other half: what is left in the screen's own stylesheet is the label reveal and
        // nothing else. Anchored on the wrapper and without a `::deep`, because the span is
        // written in this screen's own RowTemplate and already carries this scope. That is the
        // rule that replaces the dropped head, so an unanchored copy of it would leak the reveal
        // onto every other table this screen might one day hold.
        var css = SharedMarkup.ReadComponent("Pages", directory, screen + ".razor.css");

        Assert.Contains($".{wrapper} .{label}", css, StringComparison.Ordinal);

        // And the collapse itself is not here. `thead` is the tell: the head drop is the first rule
        // of the block and the one no screen has a reason of its own to write, so a copy of the
        // block finding its way back into a scoped stylesheet fails here rather than quietly
        // becoming the tenth duplicate.
        //
        // Read over the rules rather than over the file. These stylesheets explain themselves at
        // length, and a screen saying in a comment which head it no longer drops would otherwise
        // fail a test about what it declares - which would teach the next author to write around
        // the word instead of saying it.
        Assert.DoesNotContain("thead", WithoutComments(css), StringComparison.Ordinal);
    }

    /// <summary>
    /// The collapse is stated once, in the theme, on the opt-in class and at the token breakpoint.
    /// </summary>
    /// <remarks>
    /// The half that used to be nine assertions against nine stylesheets. It is a stronger guard
    /// than they were: a scoped copy could be broken silently by a missing <c>::deep</c>, and this
    /// block cannot be - it is not scoped to anything. What it can lose is the breakpoint, which is
    /// why that is read as the Sass expression rather than as a figure: the theme derives the edge
    /// from <c>$dt-breakpoint-phone</c> so the palette stays the only place it is written down, and
    /// a test looking for <c>640.98px</c> would go green the day somebody inlined it.
    /// </remarks>
    [Fact]
    public void The_collapse_is_stated_once_in_the_theme_at_the_token_breakpoint()
    {
        var theme = Theme();
        var block = PhoneBlockCarrying(theme, "." + OptIn);

        // The head drop, which is what the per-screen assertion above reads as the tell, and the
        // rules that make a row a card. Named individually rather than counted, because what
        // matters is that each of them is keyed on the opt-in class: an unanchored `thead
        // { display: none }` in the theme would collapse the head of every table in the product.
        Assert.Contains($".{OptIn} thead", block, StringComparison.Ordinal);
        Assert.Contains($".{OptIn} table,", block, StringComparison.Ordinal);
        Assert.Contains($".{OptIn} div.dt-table-scroll", block, StringComparison.Ordinal);
        Assert.Contains($".{OptIn} tr.dt-table-row", block, StringComparison.Ordinal);
        Assert.Contains($".{OptIn} tr.dt-table-row td", block, StringComparison.Ordinal);
        Assert.Contains($".{OptIn} tr.dt-table-row td.dt-table-actions", block, StringComparison.Ordinal);
        Assert.Contains($".{OptIn} tr.dt-table-row td.dt-table-window", block, StringComparison.Ordinal);
        Assert.Contains($".{OptIn} tr.dt-table-row td:empty", block, StringComparison.Ordinal);
        Assert.Contains($".{OptIn} tr.dt-table-row .btn", block, StringComparison.Ordinal);

        // The empty state's action too, which is a row of DtDataTable's that is not a data row -
        // and the only control on the screen when the table has nothing in it.
        Assert.Contains($".{OptIn} tr.dt-table-empty .btn", block, StringComparison.Ordinal);

        // Once. The item this block closes was nine copies of it, and a second one here would be
        // the duplication starting again from the place that ended it.
        Assert.Single(Regex.Matches(
            theme,
            @"\." + OptIn + @"\s+thead",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// The heading action clears the thumb floor on a phone, from its own block rather than from
    /// the collapse's.
    /// </summary>
    /// <remarks>
    /// Where the rule sits is the claim. <c>.dt-page-head</c> is drawn by screens with no table on
    /// them, so the floor is not part of the collapse and must not be inside it: narrow that block
    /// to the tables that use it, or delete it the day nothing does, and an unrelated floor would
    /// go with it silently. Both are stated at the same edge, which is what this reads - and reads
    /// as the Sass expression rather than as a figure, so inlining the breakpoint fails here.
    /// </remarks>
    [Fact]
    public void The_heading_action_clears_the_thumb_floor_from_a_block_of_its_own()
    {
        var theme = Theme();
        var block = PhoneBlockCarrying(theme, ".dt-page-head .btn");

        Assert.Contains("min-height: var(--dt-touch-target)", block, StringComparison.Ordinal);

        // And it is not the collapse's block. Asserted as two different blocks rather than by
        // reading the first one twice: `PhoneBlockCarrying` returns whichever query holds the
        // selector, so the two coinciding is exactly the arrangement this guards against.
        Assert.DoesNotContain("." + OptIn, block, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one phone media query in the theme that states a rule for <paramref name="selector"/>,
    /// braces balanced.
    /// </summary>
    /// <remarks>
    /// Found by the selector rather than by position: the theme states several phone media
    /// queries - the shell's own is the first - and picking a block by index would read whichever
    /// one happened to be written first.
    /// <para>
    /// Walked over the rules rather than over the file. The walk counts braces, and the comments
    /// in this file quote selectors and declarations that contain them, so a comment added later
    /// would desynchronise the count and the failure would read as "the theme states no such
    /// block" - a true-sounding message about something that had not changed.
    /// </para>
    /// </remarks>
    private static string PhoneBlockCarrying(string theme, string selector)
    {
        const string Query = "@media (max-width: $dt-breakpoint-phone-max)";

        for (var start = theme.IndexOf(Query, StringComparison.Ordinal);
             start >= 0;
             start = theme.IndexOf(Query, start + Query.Length, StringComparison.Ordinal))
        {
            var open = theme.IndexOf('{', start);
            var depth = 0;

            for (var index = open; index < theme.Length; index++)
            {
                if (theme[index] == '{')
                {
                    depth++;
                }
                else if (theme[index] == '}' && --depth == 0)
                {
                    var block = theme[start..(index + 1)];

                    if (block.Contains(selector, StringComparison.Ordinal))
                    {
                        return block;
                    }

                    break;
                }
            }
        }

        Assert.Fail($"_theme.scss states no '{Query}' block carrying '{selector}'.");

        return string.Empty;
    }

    /// <summary><c>_theme.scss</c> with its comments removed, which is what these tests read.</summary>
    private static string Theme() => WithoutComments(File.ReadAllText(Path.Combine(
        RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Styles", "_theme.scss")));

    /// <summary>A stylesheet's rules, with both comment forms Sass accepts taken out.</summary>
    private static string WithoutComments(string css) => Regex.Replace(
        Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5)),
        @"//[^\n]*",
        " ",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>The class attribute of the element the wrapper names, split into its own words.</summary>
    private static string[] ClassesOf(string html, string wrapper)
    {
        var start = Opening(wrapper).Match(html);

        Assert.True(start.Success, $@"The screen renders no <div class=""{wrapper}"">.");

        var attribute = Regex.Match(
            start.Value,
            @"class=""(?<classes>[^""]*)""",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        return attribute.Groups["classes"].Value.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// The opening tag of the <c>&lt;div&gt;</c> carrying one class.
    /// </summary>
    /// <remarks>
    /// The wrapper is matched as one class among whatever else the element carries, rather than as
    /// the whole attribute: a screen that adds a spacing utility beside its own hook - or the
    /// opt-in class this story puts there - has not moved the wrapper, and an assertion that failed
    /// on it would be reporting a change that preserved what it guards. Bounded on both sides by
    /// <c>[\w-]</c> rather than by <c>\b</c>, because a word boundary sits happily in the middle of
    /// <c>dt-reviews-empty</c> and would let a differently-named element stand in for the one being
    /// looked for.
    /// </remarks>
    private static Regex Opening(string wrapper) => new(
        $@"<div\b[^>]*\bclass=""[^""]*(?<![\w-]){Regex.Escape(wrapper)}(?![\w-])[^""]*""[^>]*>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The markup inside one wrapper <c>&lt;div&gt;</c>, walked to its own closing tag rather than
    /// to the first one: the table it holds renders several <c>&lt;div&gt;</c>s of its own, so a
    /// non-greedy match would stop inside the very thing being looked for.
    /// <para>
    /// Internal because <c>ReviewScreenTests</c> and <c>AnonymousScreenTests</c> ask the same
    /// question, and a second copy of a tag walker is a second set of bugs.
    /// </para>
    /// </summary>
    internal static string BodyOf(string html, string wrapper)
    {
        var start = Opening(wrapper).Match(html);

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
        "Deliveries" => DeliveryScreenTests.RenderBoardAsync(),
        "MyDeliveries" => DeliveryScreenTests.RenderOwnDeliveriesAsync(),
        "Clients" => AdministrationScreenTests.RenderClientsAsync(),
        "Dispatchers" => AdministrationScreenTests.RenderDispatchersAsync(),
        "Notifications" => NotificationScreenTests.RenderLogAsync(),
        "Reviews" => ReviewScreenTests.RenderReviewsAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, "No such screen."),
    };
}
