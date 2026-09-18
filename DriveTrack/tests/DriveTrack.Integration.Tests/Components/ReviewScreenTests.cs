using System.Text.RegularExpressions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Application.Reviews;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Components.Pages;
using DriveTrack.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using ReviewsScreen = DriveTrack.Web.Components.Pages.Reviews;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// What the reviews screen actually renders, per role, with the capabilities stubbed (FR-62 to
/// FR-67).
/// <para>
/// A source scan cannot make these claims. "A dispatcher gets the collection and no actions" and "a
/// client gets their own reviews and a way to write one" are properties of the output, and the file
/// contains all the same words whether they hold or not.
/// </para>
/// <para>
/// FR-12 throughout: none of this is authorization. <c>IAccessGuard</c> inside the review service
/// refuses a caller whatever this screen drew, and <c>ReviewTests</c> is where that is proved. What
/// is asserted here is that the screen does not offer an action that could only ever fail.
/// </para>
/// </summary>
public class ReviewScreenTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly AuthoredReviewSummary[] Mine =
    [
        new(1, 7, 5, RatingBand.Favourable, "усе було чудово", Noon),
        new(2, 8, 2, RatingBand.Unfavourable, "запізнилися на дві години", Noon),
    ];

    private static readonly ReviewSummary[] All =
    [
        new(
            1,
            7,
            5,
            RatingBand.Favourable,
            "усе було чудово",
            Noon,
            new DeliveryParty(3, "Олена Петренко"),
            new DeliveryParty(4, "Тарас Шевченко")),
        new(
            2,
            8,
            3,
            RatingBand.Neutral,
            "нічого особливого",
            Noon,
            new DeliveryParty(5, "Марія Коваль"),
            null),
    ];

    /// <summary>
    /// The caller's own deliveries, and deliberately not all reviewable. Delivery 8 is finished and
    /// already carries one of <see cref="Mine"/>; delivery 10 is still in transit. Only delivery 9
    /// may be reviewed, so a picker that offered any of the others — or a filter that had been
    /// deleted — shows up as an extra option rather than as nothing at all.
    /// </summary>
    private static readonly AssignedDeliverySummary[] Own =
    [
        Delivery(8, DeliveryStatus.Delivered),
        Delivery(9, DeliveryStatus.Delivered),
        Delivery(10, DeliveryStatus.InTransit),
    ];

    /// <summary>The delivery in <see cref="Own"/> that is finished and not yet reviewed.</summary>
    private const int ReviewableDeliveryId = 9;

    /// <summary>The heading row: the screen's title, and the one action a client has.</summary>
    private static readonly Regex PageHead = new(
        @"<div\b[^>]*class=""[^""]*\bdt-page-head\b[^""]*""[^>]*>(?<body>.*?)</div>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A column heading.</summary>
    private static readonly Regex HeaderCell = new(
        @"<th\b[^>]*>(?<body>.*?)</th>",
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

    /// <summary>The per-cell name span, matched by the class this screen marks it with.</summary>
    private static readonly Regex LabelSpan = new(
        @"<span\b[^>]*class=""[^""]*\bdt-review-label\b[^""]*""[^>]*>(?<body>.*?)</span>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task A_client_is_shown_what_they_wrote_and_a_way_to_write_another()
    {
        // FR-62 and FR-63. The author's own table, which carries no party at all, and the one
        // action their role has.
        var html = await RenderAsync(UserRole.Client);

        Assert.Contains("dt-table-scroll", html, StringComparison.Ordinal);
        Assert.Equal(Mine.Length, SharedMarkup.Occurrences(html, "dt-table-row"));

        Assert.Contains("dt-review-create", html, StringComparison.Ordinal);
        Assert.Contains("Написати відгук", html, StringComparison.Ordinal);

        // Their own verdicts, both of them.
        Assert.Contains("усе було чудово", html, StringComparison.Ordinal);
        Assert.Contains("запізнилися на дві години", html, StringComparison.Ordinal);

        // AD-17 at the surface: the author's table has no party column, so no counterparty's name
        // is rendered even though the moderation stub beside it carries two.
        Assert.DoesNotContain("Тарас Шевченко", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Марія Коваль", html, StringComparison.Ordinal);

        // FR-63 and NFR-22: their own delete is confirmed like any other destructive action.
        Assert.Contains("dt-review-delete", html, StringComparison.Ordinal);
        Assert.Contains("dt-confirm-accept", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dispatcher_is_shown_every_review_and_offered_no_way_to_touch_one()
    {
        // FR-64 against FR-65, rendered. The one role whose answer differs between reading and
        // writing, and the reason an action is absent rather than disabled: a dispatcher's edit
        // could only ever be refused by the guard.
        var html = await RenderAsync(UserRole.Dispatcher);

        Assert.Equal(All.Length, SharedMarkup.Occurrences(html, "dt-table-row"));

        // Both parties named, which is what the moderation shape exists for.
        Assert.Contains("Олена Петренко", html, StringComparison.Ordinal);
        Assert.Contains("Тарас Шевченко", html, StringComparison.Ordinal);

        // A review of a delivery nobody was assigned to says so rather than rendering an empty cell.
        Assert.Contains("Без водія", html, StringComparison.Ordinal);

        Assert.DoesNotContain("dt-review-create", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-review-edit", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-review-delete", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_moderates_and_is_offered_no_way_to_author()
    {
        // The PRD's retirement of FR-97, rendered: an administrator edits and deletes anything and
        // writes nothing. The absent create action is the half a reader will take for an omission,
        // and it is the product decision.
        var html = await RenderAsync(UserRole.Admin);

        Assert.Equal(All.Length, SharedMarkup.Occurrences(html, "dt-table-row"));

        Assert.Contains("dt-review-edit", html, StringComparison.Ordinal);
        Assert.Contains("dt-review-delete", html, StringComparison.Ordinal);
        Assert.Contains("dt-confirm-accept", html, StringComparison.Ordinal);

        Assert.DoesNotContain("dt-review-create", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Написати відгук", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_driver_is_told_whose_screen_this_is_rather_than_shown_an_empty_table()
    {
        // The party a review judges. An empty table would read as "nobody has written anything",
        // which is a different and false claim - and every review route refuses them anyway.
        var html = await RenderAsync(UserRole.Driver);

        Assert.Contains("dt-review-refused", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-table-scroll", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-review-create", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-review-edit", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_rating_wears_the_class_its_band_earned_and_never_a_colour()
    {
        // AD-18 and AD-28 together: the band is Domain's reading of the number, the class is what
        // that band looks like, and the colour is a token the stylesheet owns. A literal colour in
        // the markup would be caught by NFR-29's scanner; a threshold in the markup would be caught
        // by nothing, which is why the class is looked up rather than computed here.
        var client = await RenderAsync(UserRole.Client);
        var admin = await RenderAsync(UserRole.Admin);

        Assert.Contains(ReviewViews.ClassFor(RatingBand.Favourable), client, StringComparison.Ordinal);
        Assert.Contains(ReviewViews.ClassFor(RatingBand.Unfavourable), client, StringComparison.Ordinal);
        Assert.Contains(ReviewViews.ClassFor(RatingBand.Neutral), admin, StringComparison.Ordinal);

        // And no colour anywhere in the rendered page.
        Assert.DoesNotContain("#", ColourCandidates(client));
        Assert.DoesNotContain("#", ColourCandidates(admin));
    }

    [Fact]
    public void Each_band_resolves_to_the_token_the_palette_already_declared()
    {
        // The other half of the claim above, which a render cannot make: the class names have to
        // reach rules, and those rules have to resolve to the --dt-rating-* custom properties
        // _tokens.scss already publishes. A class with no rule behind it is the defect the whole
        // icon enum exists to prevent, wearing a different hat.
        var theme = File.ReadAllText(Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Styles", "_theme.scss"));

        foreach (var (band, token) in new[]
                 {
                     (RatingBand.Favourable, "--dt-rating-favourable"),
                     (RatingBand.Neutral, "--dt-rating-neutral"),
                     (RatingBand.Unfavourable, "--dt-rating-unfavourable"),
                 })
        {
            // The class the component asks for, minus the shared "dt-rating" prefix, is the
            // selector; the token is what its one declaration resolves to.
            var selector = "." + ReviewViews.ClassFor(band).Split(' ')[1];
            var rule = Rule(theme, selector);

            Assert.Contains("var(" + token + ")", rule, StringComparison.Ordinal);
        }

        // And the absence case, which must not wear any of the three: no verdict is not a verdict.
        // It has a token of its own now - `rating-unrated`, which the palette declares as the muted
        // ink and documents as "not a verdict colour" - so the claim is named band by band rather
        // than by the `--dt-rating-` prefix the fourth state now shares with them.
        var none = Rule(theme, "." + ReviewViews.NoRatingClass.Split(' ')[1]);

        Assert.Contains("var(--dt-rating-unrated)", none, StringComparison.Ordinal);

        foreach (var verdict in new[]
                 {
                     "--dt-rating-favourable",
                     "--dt-rating-neutral",
                     "--dt-rating-unfavourable",
                 })
        {
            Assert.DoesNotContain(verdict, none, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Every_action_on_the_screen_carries_an_icon_beside_its_text()
    {
        // NFR-24. Asserted by counting rather than by naming, because the failure this catches is a
        // button added later without one - which no named assertion would notice.
        var html = await RenderAsync(UserRole.Admin);

        var buttons = Regex.Matches(
                html,
                @"<button\b[^>]*>(?<body>.*?)</button>",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        Assert.NotEmpty(buttons);

        var bare = buttons
            .Where(body => !body.Contains("<svg", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(bare);
    }

    [Fact]
    public async Task Nothing_on_the_screen_is_written_in_English()
    {
        // NFR-14 as the rendered page rather than as a source scan. UserFacingTextTests reads the
        // markup and cannot see a key that resolves to an English value or to the key itself, which
        // is exactly what a missing resource entry looks like.
        foreach (var role in new[] { UserRole.Client, UserRole.Dispatcher, UserRole.Admin, UserRole.Driver })
        {
            var text = Regex.Replace(
                await RenderAsync(role),
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

    [Fact]
    public async Task The_write_dialog_offers_the_scale_and_opens_on_its_default()
    {
        // FR-67 and AD-18: the control cannot present a number the check constraint would refuse,
        // because its options are the scale's own values - and an unfilled form opens on the top of
        // it rather than on a complaint nobody made.
        var html = await RenderAsync(UserRole.Client);

        // Scoped to the rating control. Scraped from the whole page, this assertion would depend on
        // the ids the delivery picker beside it happens to carry - and "no option says 6" would
        // pass or fail on a stub delivery's row id rather than on the scale.
        var options = OptionValues(html, "review-rating");

        // Minimum to Maximum inclusive, which is a count rather than an end: passing Maximum as the
        // count is only right while Minimum happens to be 1.
        var expected = Enumerable
            .Range(RatingScale.Minimum, RatingScale.Maximum - RatingScale.Minimum + 1)
            .Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(expected, options);

        // Which one it opens on, not only which ones it offers. The list above says nothing about
        // selection, so a form that opened on 1 - the complaint nobody made - would pass a test
        // whose name says otherwise.
        Assert.Equal(
            RatingScale.Default.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SelectedOption(html, "review-rating"));

        Assert.Contains("review-delivery", html, StringComparison.Ordinal);
        Assert.Contains("review-text", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3.96)]
    [InlineData(3.94)]
    [InlineData(2.96)]
    [InlineData(2.9)]
    [InlineData(4d)]
    [InlineData(1d)]
    [InlineData(4.999)]
    public void The_number_a_reader_sees_and_the_band_it_wears_are_read_off_one_value(double average)
    {
        // The roster renders an average through a one-decimal format and classes it through
        // RatingScale.Band. Handed the raw double, 3.96 formats as "4" and bands as neutral, and
        // the reader sees a four wearing the middling colour - each half right on its own. So the
        // rounding happens once and both halves read the rounded number.
        var shown = double.Parse(
            ReviewViews.Format(average),
            System.Globalization.CultureInfo.CurrentCulture);

        Assert.Equal(ReviewViews.ClassFor(RatingScale.Band(shown)), ReviewViews.ClassFor(average));
    }

    [Fact]
    public async Task The_delivery_picker_offers_only_what_may_still_be_reviewed()
    {
        // FR-62's two halves as the client meets them: a delivery that has been carried, and one
        // they have not already written about. The stub carries a counter-example to each - an
        // InTransit parcel and a finished one that already has a review - so a filter deleted from
        // the screen shows up here as an extra option rather than as nothing at all.
        var html = await RenderAsync(UserRole.Client);

        Assert.Equal(
            [ReviewableDeliveryId.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            OptionValues(html, "review-delivery"));
    }

    [Fact]
    public async Task A_client_with_nothing_left_to_review_is_told_so_instead_of_being_offered_the_action()
    {
        // The action's form opens on the picker above. With nothing in it the form posts no
        // delivery and is answered "no such delivery" - a true sentence about a question the client
        // did not ask - so the action is absent and the rule it would have failed is stated.
        var html = await RenderAsync(UserRole.Client, deliveries: [Delivery(10, DeliveryStatus.InTransit)]);

        Assert.DoesNotContain("dt-review-create", html, StringComparison.Ordinal);
        Assert.Contains("dt-review-nothing-to-review", html, StringComparison.Ordinal);

        // Their own reviews are still there: having nothing left to write is not having written
        // nothing.
        Assert.Equal(Mine.Length, SharedMarkup.Occurrences(html, "dt-table-row"));
    }

    [Fact]
    public async Task The_screen_carries_its_heading_and_a_client_s_one_action_in_the_shared_page_head()
    {
        // <h1> rather than <h2>: `FocusOnNavigate Selector="h1"` in Routes.razor looks for one, and
        // on a page without it a keyboard user keeps the focus the previous screen had. In the
        // shared heading row because the product has one heading shape - and the write action is
        // about the collection the heading names rather than about any row in it, which is why it
        // belongs beside the title instead of loose in a Bootstrap row underneath it.
        var html = await RenderAsync(UserRole.Client);

        var head = PageHead.Match(html);

        Assert.True(head.Success, "The screen has no heading row.");

        var body = head.Groups["body"].Value;

        Assert.Contains("<h1", body, StringComparison.Ordinal);

        // NFR-24: the heading carries the glyph the navigation already uses for this destination.
        Assert.Contains("<svg", body, StringComparison.Ordinal);

        // Found by its hook and only then asserted about by its colour, because `btn btn-primary`
        // is what a create action looks like rather than what one is.
        Assert.Contains("dt-review-create", body, StringComparison.Ordinal);
        Assert.Contains("btn-primary", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Driver)]
    public async Task A_role_with_nothing_to_write_gets_the_same_heading_row_holding_only_the_title(
        UserRole role)
    {
        // The head is unconditional and its action is not. A dispatcher reads the collection, an
        // administrator moderates it and a driver is refused it outright - none of the three
        // authors a review - so the row holds the title alone, which is the shape the
        // administration group already settled for a screen with no action of its own.
        var html = await RenderAsync(role);

        var head = PageHead.Match(html);

        Assert.True(head.Success, "The screen has no heading row.");

        var body = head.Groups["body"].Value;

        Assert.Contains("<h1", body, StringComparison.Ordinal);
        Assert.Contains("<svg", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Client)]
    [InlineData(UserRole.Admin)]
    public async Task Every_row_action_sits_in_the_shared_actions_cell(UserRole role)
    {
        // DtDataTable right-aligns the trailing cell through `.dt-table-row ::deep
        // td.dt-table-actions`, so a cell that does not wear the class its own heading wears is a
        // cell the shared rule cannot reach. Both tables here marked it `dt-row-actions` and styled
        // it themselves as a flex row - which ignores `text-align` outright, so the move and the
        // deletion of that rule are one change rather than two.
        var html = await RenderAsync(role);

        var cells = ActionsCell.Matches(html)
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        // Before the count, because `0 == 0` is what a screen that rendered no rows at all would
        // answer - and the loop below would then assert nothing.
        Assert.NotEmpty(cells);

        Assert.Equal(SharedMarkup.Occurrences(html, "dt-table-row"), cells.Length);

        foreach (var cell in cells)
        {
            Assert.Contains("dt-review-edit", cell, StringComparison.Ordinal);
            Assert.Contains("dt-review-delete", cell, StringComparison.Ordinal);
        }

        // The retired class, gone from the screen rather than merely unused: a stylesheet rule and
        // a markup hook that no longer meet are how a screen ends up half-migrated.
        Assert.DoesNotContain("dt-row-actions", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dispatcher_gets_a_head_and_rows_of_the_same_width_and_no_actions_column()
    {
        // FR-64 against FR-65 as a layout claim rather than a capability one. The heading and the
        // cell are gated on the same predicate, and they have to stay paired: hide one without the
        // other and the table below the head is a column narrower than the head above it, so every
        // value in every row is announced and painted under the wrong name.
        var html = await RenderAsync(UserRole.Dispatcher);

        var columns = HeaderCell.Matches(html).Count;

        Assert.NotEqual(0, columns);

        var rows = BodyRow.Matches(html)
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        Assert.NotEmpty(rows);

        foreach (var row in rows)
        {
            Assert.Equal(columns, Cell.Matches(row).Count);
        }

        // And neither side carries an actions column at all: the class is DtDataTable's hook for
        // the trailing cell, so its absence is the absence of the column rather than of a button.
        Assert.DoesNotContain("dt-table-actions", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-row-actions", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Client)]
    [InlineData(UserRole.Admin)]
    public async Task The_actions_column_keeps_its_name_without_printing_it(UserRole role)
    {
        // The buttons under it say what they do, so a column name printed above them competes with
        // them for the same width - and a column with no name at all leaves a screen reader moving
        // by cell with one unlabelled column. Hidden, not dropped, as on every other table here.
        var html = await RenderAsync(role);

        var actions = ActionsHeader.Match(html);

        Assert.True(actions.Success, "The table has no actions heading.");
        Assert.Contains("dt-visually-hidden", actions.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("Дії", actions.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Client, "усе було чудово")]
    [InlineData(UserRole.Admin, "Тарас Шевченко")]
    public async Task The_table_this_role_is_shown_sits_inside_the_one_collapse_wrapper(
        UserRole role,
        string value)
    {
        // `dt-reviews` is the only wrapper in the product with two possible occupants, and no role
        // is shown both - so `PhoneLayoutTests` can only ever check the one its renderer draws.
        // The other would sit outside the wrapper with every collapse rule matching nothing and
        // that class still green, which is the gap this closes from the screen's own side.
        var html = await RenderAsync(role);

        var body = PhoneLayoutTests.BodyOf(html, "dt-reviews");

        Assert.Contains("dt-table-scroll", body, StringComparison.Ordinal);
        Assert.Contains("dt-table-row", body, StringComparison.Ordinal);

        // And it is this role's table rather than merely a table: the author's list renders a
        // verdict the moderation list is not asked for here, and the moderation list names a party
        // the author's type has no field for at all.
        Assert.Contains(value, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Client)]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Admin)]
    public async Task Every_cell_carries_its_own_name_for_the_phone_layout(UserRole role)
    {
        // Below the phone breakpoint the head is dropped rather than hidden: a table laid out as
        // blocks is no longer a table to a screen reader - the implicit table, row and cell roles
        // go with the `display` they came from - so a `<thead>` left in place would name columns
        // that no longer exist to be announced under. The label inside each cell is what replaces
        // it, and that only works if every cell has one.
        //
        // Every table a role is shown, which is three cases rather than two: a client gets the
        // author's list, an administrator the moderation one, and a dispatcher that same
        // moderation table minus its actions column. The dispatcher is the case most likely to
        // drift, because it is the only one whose columns and cells are both gated.
        //
        // Cell by cell rather than by counting labels against cells across the row: two labels in
        // one cell and none in the next is the same total and a card with an unnamed line in it.
        // And the label is checked against the heading of the column it is in, because a label that
        // exists and reads wrongly names the value rather than failing to name it.
        var html = await RenderAsync(role);

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

    /// <summary>The text of every declaration in one rule of a stylesheet.</summary>
    private static string Rule(string stylesheet, string selector)
    {
        var match = Regex.Match(
            stylesheet,
            Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, "_theme.scss declares no rule for " + selector + ".");

        return match.Groups["body"].Value;
    }

    /// <summary>
    /// The rendered page with its Cyrillic content removed, so a stray hex colour is visible. A
    /// weaker check than NFR-29's scanner and aimed at a different place: that one reads the
    /// stylesheets, this one reads what a component put in an attribute.
    /// </summary>
    private static string ColourCandidates(string html) =>
        Regex.Replace(html, @"[^#0-9a-fA-F]", " ", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>
    /// The screen as an administrator meets it, for <c>PhoneLayoutTests</c> to read the wrapper out
    /// of. An administrator rather than the default role of nothing: that class asks for a screen
    /// that actually draws a table, and a driver is shown a sentence instead of one.
    /// </summary>
    internal static Task<string> RenderReviewsAsync() => RenderAsync(UserRole.Admin);

    private static Task<string> RenderAsync(
        UserRole role,
        IReadOnlyList<AssignedDeliverySummary>? deliveries = null) =>
        ComponentRenderer.RenderAsync<ReviewsScreen>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ICurrentUser>(new StubCaller(role));
                services.AddSingleton<IReviewService>(new StubReviews());
                services.AddSingleton<IDeliveryService>(new StubDeliveries(deliveries ?? Own));
            });

    /// <summary>One of the caller's own deliveries, in a chosen state.</summary>
    private static AssignedDeliverySummary Delivery(int id, DeliveryStatus status) =>
        new(
            id,
            new LocationView(new MapLocation(50.4501, 30.5234), null),
            new LocationView(new MapLocation(49.8397, 24.0297), null),
            "Одна палета",
            12.5m,
            null,
            null,
            null,
            status,
            Noon,
            false);

    /// <summary>
    /// The values of the options inside one named select, and only that one.
    /// <para>
    /// Scoped rather than scraped from the whole page: the rating control and the delivery picker
    /// both render options, so a page-wide scrape makes an assertion about one of them depend on
    /// the ids the other happens to carry.
    /// </para>
    /// </summary>
    /// <summary>
    /// The value of the one option a select opens on.
    /// <para>
    /// Static server rendering marks it: the renderer knows the bound value and writes
    /// <c>selected</c> onto the matching option, which is the only way a form rendered without a
    /// circuit could show a default at all.
    /// </para>
    /// </summary>
    private static string SelectedOption(string html, string selectId)
    {
        var select = SelectBody(html, selectId);

        var selected = Regex.Matches(
                select,
                @"<option\s+value=""(?<value>[^""]*)""(?<rest>[^>]*)>",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5))
            .Where(match => match.Groups["rest"].Value.Contains("selected", StringComparison.Ordinal))
            .Select(match => match.Groups["value"].Value)
            .ToArray();

        return Assert.Single(selected);
    }

    private static string SelectBody(string html, string selectId)
    {
        var select = Regex.Match(
            html,
            @"<select[^>]*id=""" + Regex.Escape(selectId) + @"""[^>]*>(?<body>.*?)</select>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(select.Success, "The screen rendered no select with id " + selectId + ".");

        return select.Groups["body"].Value;
    }

    private static string[] OptionValues(string html, string selectId) =>
    [
        .. Regex.Matches(
                SelectBody(html, selectId),
                @"<option\s+value=""(?<value>[^""]*)""",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["value"].Value),
    ];

    /// <summary>A signed-in caller of a chosen role, and nothing else the screen reads.</summary>
    private sealed class StubCaller(UserRole role) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public UserId UserId => new(1);

        public UserRole Role => role;

        public DriverId? DriverId => null;

        public ClientId? ClientId => new(3);
    }

    /// <summary>
    /// Both review lists, answered without a database. Every write throws: a static render never
    /// reaches one, so a screen that called it during rendering would fail loudly rather than pass
    /// quietly.
    /// </summary>
    private sealed class StubReviews : IReviewService
    {
        public Task<IReadOnlyList<ReviewSummary>> ListAsync(
            ListReviewsQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReviewSummary>>(All);

        public Task<IReadOnlyList<AuthoredReviewSummary>> ListMineAsync(
            ListReviewsQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuthoredReviewSummary>>(Mine);

        public Task<AuthoredReviewSummary> CreateAsync(
            CreateReviewCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<AuthoredReviewSummary> UpdateAsync(
            int id,
            UpdateReviewCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task DeleteAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<IReadOnlyList<DriverRating>> ListDriverRatingsAsync(
            IReadOnlyCollection<DriverId> driverIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The reviews screen shows no driver ratings.");
    }

    /// <summary>
    /// The caller's own deliveries, which is the only thing this screen asks the delivery
    /// capability for — the picker in the write dialog.
    /// </summary>
    private sealed class StubDeliveries(IReadOnlyList<AssignedDeliverySummary> deliveries)
        : IDeliveryService
    {
        public Task<IReadOnlyList<AssignedDeliverySummary>> ListMineAsync(
            ListDeliveriesQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(deliveries);

        public Task<IReadOnlyList<DeliverySummary>> ListAsync(
            ListDeliveriesQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The reviews screen never reads the dispatch board.");

        public Task<DeliverySummary> GetAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The reviews screen never reads one delivery as dispatch.");

        public Task<AssignedDeliverySummary> GetMineAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The reviews screen reads the list, not one row.");

        public Task<IReadOnlyList<DeliverySummary>> ListByIdsAsync(
            IReadOnlyCollection<int> ids,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The moderation list resolves its parties in the service.");

        public Task<DeliverySummary> CreateAsync(
            CreateDeliveryCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<AssignedDeliverySummary> RequestAsync(
            RequestDeliveryCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<DeliverySummary> UpdateAsync(
            int id,
            UpdateDeliveryCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task DeleteAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<TimelineEntryView> ChangeStatusAsync(
            int id,
            ChangeDeliveryStatusCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<TimelineEntryView> AddNoteAsync(
            int id,
            AddDeliveryNoteCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A static render must not write.");

        public Task<IReadOnlyList<TimelineEntryView>> ListTimelineAsync(
            int id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The reviews screen shows no timeline.");

        public Task<IReadOnlyList<PlaceMatch>> SearchPlacesAsync(
            SearchPlacesQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The reviews screen searches no addresses.");
    }
}
