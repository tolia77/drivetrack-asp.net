using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Vehicles;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Components.Shared;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// The shared component library, asserted where it is actually decided: in the class a component
/// renders and in the hook a screen names it by.
/// <para>
/// Every assertion here guards a failure that ships looking fine. A status drawn as an unclassed
/// span is a word on a table with no fill and no shape, and reads as a rendering glitch rather than
/// as a missing arm. A driver nobody has reviewed shown as a zero is the worst rating the scale
/// has, stated about somebody nobody has judged. A save button that is teal in both modes is a
/// dialog that has stopped saying which of the two it is in.
/// </para>
/// <para>
/// Classes, never variants. A variant is what an action <em>means</em> - create is teal, edit is
/// slate - so a test that found a control by <c>.btn-primary</c> would be a test that passes the
/// day somebody paints a delete button teal. Controls are found by their <c>dt-*</c> hook, and the
/// variant is only ever the thing being asserted about them.
/// </para>
/// </summary>
public class ComponentLibraryTests
{
    /// <summary>
    /// Bootstrap's contextual badge classes. Retired rather than merely unused: they are the
    /// vocabulary the delivery statuses, the shift states and the duty markers all wore before the
    /// design system had one of its own, and the whole point of the StatusLabel/Badge distinction
    /// is lost the first time one of them comes back.
    /// </summary>
    private static readonly Regex ContextualBadge = new(
        @"\btext-bg-[a-z]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The screens whose one dialog serves both modes, and the expression each decides by. Kept as
    /// a table rather than as six near-identical facts, so a seventh dual-mode dialog is a row.
    /// </summary>
    public static TheoryData<string, string, string> DualModeDialogs() => new()
    {
        { "Pages/Vehicles.razor", "dt-vehicle-save", "_form.Id is null" },
        { "Pages/Drivers.razor", "dt-driver-save", "_form.Id is null" },
        { "Pages/Deliveries.razor", "dt-delivery-save", "_form.Id is null" },
        { "Pages/Shifts.razor", "dt-shift-save", "_form.Id is null" },
        { "Pages/Reviews.razor", "dt-review-save", "_editingId is null" },
    };

    /// <summary>The four delivery statuses and the class each one's face declares.</summary>
    public static TheoryData<DeliveryStatus, string> DeliveryFaces() => new()
    {
        { DeliveryStatus.Pending, "dt-status--pending" },
        { DeliveryStatus.InTransit, "dt-status--transit" },
        { DeliveryStatus.Delivered, "dt-status--delivered" },
        { DeliveryStatus.Failed, "dt-status--failed" },
    };

    /// <summary>A rating, the band it falls in and how many stars that many is drawn with.</summary>
    /// <remarks>
    /// One filled star per whole point, and 4.6 is the row that says so: a partial rating drawn as
    /// a full row of five is indistinguishable at a glance from a driver every review gave full
    /// marks, and only the figure beside the stars would have said otherwise. 5.0 is here to hold
    /// the other end - a full row is still reachable, by the one score that earns it.
    /// </remarks>
    public static TheoryData<double, RatingBand, int> RatedDrivers() => new()
    {
        { 5.0, RatingBand.Favourable, 5 },
        { 4.6, RatingBand.Favourable, 4 },
        { 4.0, RatingBand.Favourable, 4 },
        { 3.2, RatingBand.Neutral, 3 },
        { 2.1, RatingBand.Unfavourable, 2 },
    };

    // =====================================================================================
    // The library exists, and the retired vocabulary stays retired
    // =====================================================================================

    [Fact]
    public void Every_piece_of_the_vocabulary_has_a_component_of_its_own()
    {
        // Vacuity guard for everything below, and the story's own checklist. A screen that needs a
        // badge and finds no DtBadge writes a span with a class on it, and the second screen that
        // needs one writes a slightly different span.
        foreach (var expected in new[]
                 {
                     "DtStatusLabel.razor", "DtRatingDisplay.razor", "DtBadge.razor",
                     "DtCard.razor", "DtSelect.razor", "DtTimelineEntry.razor",
                     "DtConnectionDock.razor",
                 })
        {
            Assert.True(
                File.Exists(Path.Combine(SharedMarkup.SharedDirectory, expected)),
                $"Components/Shared/{expected} is missing.");
        }
    }

    [Fact]
    public void No_component_renders_a_bootstrap_contextual_badge()
    {
        // The rule that stops the old vocabulary coming back. Every `text-bg-*` in the component
        // tree was a status, a shift state or a duty marker wearing a colour and nothing else - no
        // shape, no border, nothing telling a reader which of the three vocabularies they were
        // looking at. StatusLabel and Badge replaced all of them, and a paste from an older screen
        // is how one returns.
        var offenders = Directory
            .GetFiles(SharedMarkup.ComponentsDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => ContextualBadge.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(SharedMarkup.ComponentsDirectory, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    // =====================================================================================
    // StatusLabel: the two closed vocabularies
    // =====================================================================================

    [Theory]
    [MemberData(nameof(DeliveryFaces))]
    public async Task A_delivery_status_wears_the_status_class_its_face_declares(
        DeliveryStatus status,
        string modifier)
    {
        // The matrix's first row. The shape as well as the fill hangs off this class - the design
        // says the green and the red "differ by shape and word; they are not colour-blind safe on
        // hue alone" - so a label that lost the class would lose the distinction and keep the word.
        var html = await ComponentRenderer.RenderAsync<Web.Components.Shared.DeliveryStatusLabel>(
            new Dictionary<string, object?> { ["Status"] = status });

        Assert.Contains($"dt-status {modifier}", html, StringComparison.Ordinal);

        // And the word is still there, in the reader's language. Colour and shape repeat the word;
        // they never replace it.
        Assert.True(
            SharedMarkup.IsUkrainian(SharedMarkup.TextOf(html)),
            $"The {status} label renders no word: it rendered '{SharedMarkup.TextOf(html)}'.");
    }

    [Theory]
    [InlineData(true, "dt-status--running")]
    [InlineData(false, "dt-status--finished")]
    public async Task A_shift_state_wears_the_status_shape_rather_than_a_badge(bool isOpen, string modifier)
    {
        // The matrix's second row. The same shape a delivery status wears, deliberately - a reader
        // learns one vocabulary - and never Bootstrap's badge, which carried the state in its hue
        // alone and looked exactly like the duty marker beside it on the same screen.
        var html = await ComponentRenderer.RenderAsync<Web.Components.Pages.ShiftStateLabel>(
            new Dictionary<string, object?> { ["IsOpen"] = isOpen });

        Assert.Contains($"dt-status {modifier}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("text-bg-", html, StringComparison.Ordinal);
        Assert.True(SharedMarkup.IsUkrainian(SharedMarkup.TextOf(html)));
    }

    [Fact]
    public async Task A_delivery_status_with_no_face_fails_loudly()
    {
        // The matrix's first row read the other way, and the defect it names: the label used to be
        // four independent arms, so a fifth status would have rendered an empty unclassed span onto
        // a delivery table and every timeline line - a blank cell that looks like missing data
        // rather than like a missing decision. AD-10's own Delivery.NextStatuses fails this way for
        // the same reason.
        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() =>
            ComponentRenderer.RenderAsync<Web.Components.Shared.DeliveryStatusLabel>(
                new Dictionary<string, object?> { ["Status"] = (DeliveryStatus)99 }));
    }

    [Fact]
    public async Task A_status_face_with_no_class_fails_loudly()
    {
        // The same claim one level down, so the shared label cannot be handed a face nobody has
        // drawn and answer with an unshaped, uncoloured span.
        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() =>
            ComponentRenderer.RenderAsync<DtStatusLabel>(
                new Dictionary<string, object?> { ["Face"] = (DtStatusFace)99 }));
    }

    // =====================================================================================
    // RatingDisplay
    // =====================================================================================

    [Fact]
    public async Task An_unrated_driver_shows_words_and_neither_stars_nor_a_zero()
    {
        // The matrix's third row. Null and zero are different claims - nobody has said anything,
        // versus everybody said the worst thing - and the second is what a screen says the moment
        // it renders an absent average through the same path as a present one.
        var html = await ComponentRenderer.RenderAsync<DtRatingDisplay>(
            new Dictionary<string, object?> { ["Rating"] = null });

        Assert.Contains("dt-rating--unrated", html, StringComparison.Ordinal);

        var text = SharedMarkup.TextOf(html);

        Assert.True(SharedMarkup.IsUkrainian(text), $"The unrated state reads '{text}'.");
        Assert.DoesNotContain('0', text);
        Assert.DoesNotContain('★', text);
        Assert.DoesNotContain('☆', text);

        // And it wears no band colour. The absence of a verdict is not a verdict, and any of the
        // three would make it one.
        foreach (var band in Enum.GetValues<RatingBand>())
        {
            Assert.DoesNotContain(
                ReviewViewsClass(band),
                html,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(RatedDrivers))]
    public async Task A_rated_driver_shows_its_band_its_stars_and_its_number(
        double rating,
        RatingBand band,
        int filled)
    {
        // The number is always beside the stars: five glyphs alone are a rating nobody can read
        // back precisely, and the figure is what the reader actually compares one driver by.
        var html = await ComponentRenderer.RenderAsync<DtRatingDisplay>(
            new Dictionary<string, object?> { ["Rating"] = rating });

        Assert.Contains(ReviewViewsClass(band), html, StringComparison.Ordinal);

        var text = SharedMarkup.TextOf(html);

        Assert.Equal(filled, text.Count(character => character == '★'));
        Assert.Equal(5 - filled, text.Count(character => character == '☆'));

        // The glyph row says nothing to a screen reader, so the figure has to be in the text.
        Assert.Contains(rating.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("uk-UA")), text, StringComparison.Ordinal);
    }

    // =====================================================================================
    // Badge
    // =====================================================================================

    [Theory]
    [InlineData(DtBadgeVariant.Neutral, "dt-badge")]
    [InlineData(DtBadgeVariant.Overdue, "dt-badge--overdue")]
    [InlineData(DtBadgeVariant.Role, "dt-badge--role")]
    public async Task A_badge_is_a_pill_that_never_looks_like_a_status_label(
        DtBadgeVariant variant,
        string expected)
    {
        var html = await ComponentRenderer.RenderAsync<DtBadge>(
            new Dictionary<string, object?> { ["Variant"] = variant });

        Assert.Contains(expected, html, StringComparison.Ordinal);

        // The one rule the two vocabularies are kept apart by: a badge is never a status label, so
        // it never borrows the class that carries the status shape.
        Assert.DoesNotContain("dt-status", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_badge_variant_with_no_class_fails_loudly()
    {
        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() =>
            ComponentRenderer.RenderAsync<DtBadge>(
                new Dictionary<string, object?> { ["Variant"] = (DtBadgeVariant)99 }));
    }

    [Fact]
    public void The_overdue_marker_is_the_badge_on_both_screens_that_draw_one()
    {
        // The matrix's sixth row, asserted at the two places the marker is written: a delivery past
        // its window is marked beside the window on the dispatch board and on a driver's or
        // client's own list, and both used to write Bootstrap's red badge - which is the same
        // chrome the Failed status wore two columns away.
        foreach (var file in new[] { "Pages/Deliveries.razor", "Pages/MyDeliveries.razor" })
        {
            var markup = SharedMarkup.ReadComponent(file.Split('/'));

            Assert.Contains(
                "DtBadgeVariant.Overdue",
                markup,
                StringComparison.Ordinal);
        }
    }

    // =====================================================================================
    // The dual-mode save action
    // =====================================================================================

    [Theory]
    [MemberData(nameof(DualModeDialogs))]
    public void The_save_action_follows_the_mode_its_dialog_is_in(string file, string hook, string creating)
    {
        // The matrix's fourth and fifth rows. Six screens open one dialog for both modes, so the
        // save button is the only thing on screen that can say which one the reader is in - and
        // AD-28 gives create and edit different colours precisely so that it does. A button that is
        // slate in both is a dialog that has stopped answering the question.
        //
        // Read from the source, and for the edit arm above all: a statically rendered screen holds
        // an empty form, so the mode with an id set is genuinely unreachable here. The create arm is
        // rendered instead, one test below - a source match is character-for-character true of a
        // screen whose freshly opened create dialog shows the edit colour, because the expression is
        // right and the state it reads is not.
        var markup = SharedMarkup.ReadComponent(file.Split('/'));

        // The hook stays written out on the button itself, so the control is still findable in the
        // source by the name every other test names it by.
        Assert.Contains($@"class=""{hook} @SaveVariant""", markup, StringComparison.Ordinal);

        var variant = Regex.Match(
            markup,
            @"private string SaveVariant =>\s*(?<condition>[^\r\n]+)\s*\?\s*""(?<create>[^""]+)""\s*:\s*""(?<edit>[^""]+)"";",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(variant.Success, $"{file} does not resolve its save variant from the dialog's mode.");
        Assert.Equal(creating, variant.Groups["condition"].Value.Trim());
        Assert.Equal("btn btn-primary", variant.Groups["create"].Value);
        Assert.Equal("btn btn-secondary", variant.Groups["edit"].Value);
    }

    [Fact]
    public async Task A_freshly_opened_create_dialog_renders_the_create_action()
    {
        // The half the source scan cannot make: the expression above can be perfectly correct while
        // the dialog is opened over a form the previous edit left an id on, and every character the
        // regex matches would still be there. A statically rendered screen holds a new form, which
        // is exactly the state OpenCreateAsync is supposed to produce - so the button on it is the
        // create button, or the screen has stopped saying which mode it is in.
        var html = await ComponentRenderer.RenderAsync<Web.Components.Pages.Vehicles>(
            parameters: null,
            configureServices: services =>
            {
                services.AddSingleton<ICurrentUser>(new StubDispatcher());
                services.AddSingleton<IVehicleService>(new StubFleet());
            });

        var save = Regex.Match(
            html,
            @"<button[^>]*\bclass=""(?<classes>[^""]*\bdt-vehicle-save\b[^""]*)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(
            save.Success,
            $"No save button carrying 'dt-vehicle-save' in:{Environment.NewLine}{html}");

        // The variant is the thing asserted about the control, never the way the control is found.
        var classes = save.Groups["classes"].Value;

        Assert.Contains("btn-primary", classes, StringComparison.Ordinal);
        Assert.DoesNotContain("btn-secondary", classes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pages/Admin/Clients.razor", "dt-client-save")]
    [InlineData("Pages/MyShifts.razor", "dt-shift-save")]
    public void An_edit_only_dialog_saves_with_the_edit_action_and_nothing_else(string file, string hook)
    {
        // The other half of the same rule, and the reason it is a separate case: these two dialogs
        // have no create mode at all - a client is registered by registering and a driver's own
        // shift is started by going on duty - so a variant that switched on a mode would be
        // switching on a state that cannot occur. The edit colour, stated outright.
        var markup = SharedMarkup.ReadComponent(file.Split('/'));

        Assert.Contains($@"btn btn-secondary {hook}", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveVariant", markup, StringComparison.Ordinal);
    }

    // =====================================================================================
    // DataTable: the truncation note
    // =====================================================================================

    [Fact]
    public async Task A_cut_off_page_says_so_under_the_table_and_a_full_one_says_nothing()
    {
        // The matrix's eighth row. The note is a fact about what is on screen, so it sits under the
        // rows rather than above them - a reader who has not looked at the rows yet has nothing to
        // apply it to.
        var truncated = await RenderTableAsync(isTruncated: true);
        var whole = await RenderTableAsync(isTruncated: false);

        Assert.Contains("dt-table-note", truncated, StringComparison.Ordinal);
        Assert.DoesNotContain("dt-table-note", whole, StringComparison.Ordinal);

        // In its own words: the note is the screen's, because a fleet, a list of deliveries and a
        // log of attempts are three different things to be missing some of.
        Assert.Contains("Показано лише першу сторінку", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public void No_truncation_note_claims_a_total()
    {
        // The rule the design states outright: a list is a page of rows, never a total. The query
        // answers no total at all - the PRD excludes one - so "100 of 842" would be a number the
        // product does not have, and a page count would be a page nobody can navigate to.
        var catalogue = File.ReadAllText(Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Web"),
            "Resources",
            "UiText.resx"));

        var notes = Regex.Matches(
                catalogue,
                @"<data name=""[A-Za-z]*Truncated""[^>]*>\s*<value>(?<value>[^<]*)</value>",
                RegexOptions.None,
                TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["value"].Value)
            .ToArray();

        Assert.NotEmpty(notes);

        foreach (var note in notes)
        {
            Assert.DoesNotContain(note, character => char.IsAsciiDigit(character));
        }
    }

    // =====================================================================================
    // DataTable: the truncation note's two halves
    // =====================================================================================

    [Fact]
    public void A_screen_that_admits_a_cut_off_page_also_says_what_was_cut_off()
    {
        // The note needs two coordinated parameters where it needed one, and the coordination is
        // what nothing held: `IsTruncated` alone renders nothing at all, so dropping the sentence
        // from a screen silently stops it admitting it shows a partial list, and dropping the flag
        // leaves a sentence that can never appear. Asserted across every table in the tree rather
        // than one screen at a time, because the failure is per screen and six of them pass the
        // pair.
        var tables = Directory
            .GetFiles(SharedMarkup.ComponentsDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.StartsWith(SharedMarkup.SharedDirectory, StringComparison.Ordinal))
            .SelectMany(path => Regex
                .Matches(
                    File.ReadAllText(path),
                    @"<DtDataTable\b(?<attributes>[^>]*)>",
                    RegexOptions.Singleline,
                    TimeSpan.FromSeconds(5))
                .Select(match => (
                    Screen: Path.GetRelativePath(SharedMarkup.ComponentsDirectory, path),
                    Attributes: match.Groups["attributes"].Value)))
            .ToArray();

        Assert.NotEmpty(tables);

        var halves = tables
            .Where(table => table.Attributes.Contains("IsTruncated", StringComparison.Ordinal)
                != table.Attributes.Contains("Note=", StringComparison.Ordinal))
            .Select(table => table.Screen)
            .ToArray();

        Assert.Empty(halves);

        // Vacuity guard: every list in this product is one page of a bounded query, so a tree in
        // which no table passes the pair is a tree where the rule above holds by saying nothing.
        Assert.Equal(
            6,
            tables.Count(table => table.Attributes.Contains("IsTruncated", StringComparison.Ordinal)));
    }

    // =====================================================================================
    // Select: what the browser's answer is allowed to write
    // =====================================================================================

    [Fact]
    public void An_empty_option_clears_a_value_the_model_can_leave_unset()
    {
        // The arm whose removal costs the product two features with every test green: the dispatch
        // board's status filter can no longer be cleared and a driver can no longer be unassigned,
        // because BindConverter refuses "" for an int? rather than reading it as absence.
        Assert.True(DtSelectBinding.TryRead<int?>(string.Empty, out var cleared));
        Assert.Null(cleared);

        Assert.True(DtSelectBinding.TryRead<DeliveryStatus?>(null, out var anyStatus));
        Assert.Null(anyStatus);
    }

    [Fact]
    public void An_empty_option_never_writes_a_zero_as_though_it_meant_absence()
    {
        // A non-nullable TValue has no way to say "nothing", and its default is a real answer: 0 is
        // an id and an enum's zero member is a status. Writing one would be recording a choice
        // nobody made, so the model keeps what it had instead.
        Assert.False(DtSelectBinding.TryRead<int>(string.Empty, out _));
        Assert.False(DtSelectBinding.TryRead<DeliveryStatus>(string.Empty, out _));
    }

    [Fact]
    public void A_real_option_is_read_as_the_value_the_model_holds()
    {
        Assert.True(DtSelectBinding.TryRead<int?>("7", out var id));
        Assert.Equal(7, id);

        Assert.True(DtSelectBinding.TryRead<DeliveryStatus?>("Delivered", out var status));
        Assert.Equal(DeliveryStatus.Delivered, status);
    }

    [Fact]
    public void An_answer_this_type_cannot_hold_leaves_the_value_where_it_was()
    {
        // No option produces one, so the honest answer is to leave the bound value alone rather
        // than clear a picker the reader did not clear.
        Assert.False(DtSelectBinding.TryRead<int?>("сім", out _));
        Assert.False(DtSelectBinding.TryRead<DeliveryStatus?>("Отримано", out _));
    }

    // =====================================================================================
    // Dialog and ConnectionDock
    // =====================================================================================

    [Fact]
    public async Task A_dialog_always_carries_a_close_in_its_head_whatever_actions_it_was_given()
    {
        // Escape and the backdrop dismiss a modal `<dialog>` already, and neither is visible: on a
        // phone there is no backdrop to reach and no Escape key to press. The head's Close is the
        // one way out that is always on screen, which is why it is not the same hook as the foot's
        // default close - a test that could not tell the two apart would pass on a dialog whose
        // only exit had been replaced by the screen's own actions.
        var withActions = await ComponentRenderer.RenderAsync<DtDialog>(
            new Dictionary<string, object?>
            {
                ["Title"] = "Заголовок",
                ["Actions"] = (RenderFragment)(builder => builder.AddContent(0, "Дія")),
            });

        var plain = await ComponentRenderer.RenderAsync<DtDialog>(
            new Dictionary<string, object?> { ["Title"] = "Заголовок" });

        Assert.Contains("dt-dialog-dismiss", withActions, StringComparison.Ordinal);
        Assert.Contains("dt-dialog-dismiss", plain, StringComparison.Ordinal);

        // The foot's own close is still the one a dialog with no actions of its own grows.
        Assert.DoesNotContain("dt-dialog-close", withActions, StringComparison.Ordinal);
        Assert.Contains("dt-dialog-close", plain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_connection_dock_keeps_the_hook_the_framework_finds_it_by()
    {
        // `blazor.web.js` reveals this element by its id and by nothing else: renaming it leaves a
        // circuit failure with no notice at all, and nothing else in the product would change.
        var html = await ComponentRenderer.RenderAsync<DtConnectionDock>();

        Assert.Contains(@"id=""blazor-error-ui""", html, StringComparison.Ordinal);
        Assert.Contains("dt-dock", html, StringComparison.Ordinal);
        Assert.Contains(@"class=""reload", html, StringComparison.Ordinal);

        var text = SharedMarkup.TextOf(html);

        Assert.True(SharedMarkup.IsUkrainian(text), $"The dock reads '{text}'.");
        Assert.False(SharedMarkup.HasLatinWord(text), $"The dock reads '{text}'.");
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    /// <summary>The class a band wears, read back the way the components ask for it.</summary>
    private static string ReviewViewsClass(RatingBand band) => band switch
    {
        RatingBand.Favourable => "dt-rating--favourable",
        RatingBand.Neutral => "dt-rating--neutral",
        RatingBand.Unfavourable => "dt-rating--unfavourable",
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, null),
    };

    private static Task<string> RenderTableAsync(bool isTruncated) =>
        ComponentRenderer.RenderAsync<DtDataTable<string>>(new Dictionary<string, object?>
        {
            ["Items"] = (IReadOnlyCollection<string>)["перший"],
            ["IsLoading"] = false,
            ["IsTruncated"] = isTruncated,
            ["Note"] = "Показано лише першу сторінку: доставок може бути більше, ніж вміщує цей список.",
            ["HeaderTemplate"] = (RenderFragment)(builder =>
            {
                builder.OpenElement(0, "th");
                builder.AddContent(1, "Назва");
                builder.CloseElement();
            }),
            ["RowTemplate"] = (RenderFragment<string>)(item => builder =>
            {
                builder.OpenElement(0, "td");
                builder.AddContent(1, item);
                builder.CloseElement();
            }),
        });

    /// <summary>A signed-in dispatcher, which is the caller the fleet screen opens for.</summary>
    private sealed class StubDispatcher : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public UserId UserId => new(1);

        public UserRole Role => UserRole.Dispatcher;

        public DriverId? DriverId => null;

        public ClientId? ClientId => null;
    }

    /// <summary>
    /// One vehicle, so the screen renders a table with a row in it and the dialog beneath. Writes
    /// are never exercised — static rendering dispatches no events — so they answer rather than
    /// record.
    /// </summary>
    private sealed class StubFleet : IVehicleService
    {
        private static readonly VehicleSummary One =
            new(5, "Рено Мастер", "АА1234ВВ", 1200m, 42_000, new DateOnly(2026, 12, 1));

        public Task<IReadOnlyList<VehicleSummary>> ListAsync(
            ListVehiclesQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VehicleSummary>>([One]);

        public Task<IReadOnlyList<VehicleSummary>> ListUnassignedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VehicleSummary>>([One]);

        public Task<VehicleSummary> GetAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(One);

        public Task<VehicleSummary> CreateAsync(
            CreateVehicleCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(One);

        public Task<VehicleSummary> UpdateAsync(
            int id,
            UpdateVehicleCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(One);

        public Task DeleteAsync(int id, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
