using System.Text.RegularExpressions;
using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Account;
using DriveTrack.Web.Components.Shared;
using ValidationException = DriveTrack.Application.Common.ValidationException;
using PlaceSearch = DriveTrack.Web.Components.Pages.Deliveries.PlaceSearch;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// What a refused action says on a screen, and whose refusal it is.
/// <para>
/// Two defects meet here. <c>FailureKeys</c> used to project every field error to its message key
/// and distinct the result, so a 422 naming a weight <em>and</em> a note rendered one anonymous
/// sentence naming neither - eight distinct rules on the create-delivery command collapsed into
/// one. And each delivery screen kept a single failure field that the search boxes and the
/// submit path all wrote, so a successful address lookup silently erased a refusal the client had
/// not read yet.
/// </para>
/// <para>
/// The second half is asserted against <see cref="PlaceSearch"/> directly rather than through a
/// click: <see cref="ComponentRenderer"/> renders statically and dispatches no events, so the seam
/// where the two sinks are kept apart is the only place the behaviour can be driven. The source
/// scan below is what stops them being merged back together above it.
/// </para>
/// </summary>
public class ScreenFailureTests
{
    /// <summary>
    /// The screen's own failure field being assigned from a search, in either shape the re-merge
    /// could take: <c>_failures =</c> and a <c>RunAsync</c> call in one statement, or the two split
    /// apart so that the assignment reads a box's sink afterwards.
    /// <para>
    /// Both are needed, and the second is the one a plausible refactor writes.
    /// <c>[^;]*</c> cannot cross a statement terminator, so the first pattern alone says nothing
    /// about <c>await search.RunAsync(...); _failures = search.Failures;</c> - which restores DW-41
    /// exactly, through the new API, with every other assertion here still passing.
    /// </para>
    /// </summary>
    private static readonly Regex[] SearchWritingTheSubmitSink =
    [
        new(
            @"_failures\s*=[^;]*RunAsync",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5)),
        new(
            @"_failures\s*=[^;]*\.Failures",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5)),
    ];

    private static readonly PlaceMatch[] Matches =
    [
        new("Київ, вулиця Хрещатик, 1", new MapLocation(50.4472, 30.5222)),
    ];

    [Fact]
    public void Two_fields_keyed_alike_stay_two_failures()
    {
        // The whole of DW-8: most rules share COMMON_VALIDATION_FAILED, so distincting on the key
        // alone threw away everything that told the two apart.
        var failures = FailureKeys.For(Refusal(
            ("PackageWeightKg", nameof(ErrorCode.COMMON_VALIDATION_FAILED)),
            ("DeliveryNotes", nameof(ErrorCode.COMMON_VALIDATION_FAILED))));

        Assert.Equal(
            [
                new ScreenFailure("PackageWeightKg", nameof(ErrorCode.COMMON_VALIDATION_FAILED)),
                new ScreenFailure("DeliveryNotes", nameof(ErrorCode.COMMON_VALIDATION_FAILED)),
            ],
            failures);
    }

    [Fact]
    public void One_field_refused_twice_is_still_one_sentence()
    {
        // The half of the old rule worth keeping. Two oversized assets are one thing to fix, and
        // the same sentence twice reads as a bug rather than as emphasis.
        var failures = FailureKeys.For(Refusal(
            ("Assets[0]", nameof(ErrorCode.DELIVERY_PROOF_ASSET_TOO_LARGE)),
            ("Assets[1]", nameof(ErrorCode.DELIVERY_PROOF_ASSET_TOO_LARGE))));

        Assert.Equal(
            [new ScreenFailure("Assets", nameof(ErrorCode.DELIVERY_PROOF_ASSET_TOO_LARGE))],
            failures);
    }

    [Fact]
    public void A_nested_path_is_reported_against_the_input_the_form_has()
    {
        // The wire envelope keeps `Pickup.Latitude` because a caller sent a nested object. A form
        // has one box called Pickup and none called Pickup.Latitude, so the screen normalizes.
        var failures = FailureKeys.For(Refusal(
            ("Pickup.Latitude", nameof(ErrorCode.COMMON_VALIDATION_FAILED))));

        Assert.Equal("Pickup", Assert.Single(failures).Field);
    }

    [Fact]
    public void A_failure_that_names_no_field_carries_the_code_and_no_field()
    {
        // Both shapes of it: a refusal that is not a validation failure at all, and a validation
        // failure whose field list is empty.
        var conflict = FailureKeys.For(
            new ConflictException(ErrorCode.FLEET_VEHICLE_IN_USE, "vehicle in use"));

        Assert.Equal(
            [new ScreenFailure(null, nameof(ErrorCode.FLEET_VEHICLE_IN_USE))],
            conflict);

        var empty = FailureKeys.For(Refusal());

        Assert.Equal(
            [new ScreenFailure(null, nameof(ErrorCode.COMMON_VALIDATION_FAILED))],
            empty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".Latitude")]
    [InlineData("[0]")]
    public void A_field_name_with_no_usable_root_is_reported_as_no_field(string field)
    {
        // A different branch from the empty field *list* above: the list has an entry, and the
        // entry has nothing to key a label on. Asking the catalogue for an empty key answers the
        // empty key back, which is how a blank label would reach the page instead of no label.
        var failures = FailureKeys.For(Refusal((field, nameof(ErrorCode.COMMON_VALIDATION_FAILED))));

        Assert.Null(Assert.Single(failures).Field);
    }

    [Fact]
    public async Task A_banner_names_the_field_it_is_about_in_Ukrainian()
    {
        var html = await ComponentRenderer.RenderAsync<DtFailureBanner>(
            new Dictionary<string, object?>
            {
                ["Failures"] = (IReadOnlyList<ScreenFailure>)
                [
                    new ScreenFailure("PackageWeightKg", nameof(ErrorCode.COMMON_VALIDATION_FAILED)),
                ],
            });

        var banner = SharedMarkup.ElementWithClass(html, "div", "alert-danger");
        var text = SharedMarkup.TextOf(banner);

        // The label the form uses above the same input, word for word: one input, one name.
        Assert.Contains("Вага вантажу, кг", text, StringComparison.Ordinal);
        Assert.True(SharedMarkup.IsUkrainian(text), text);

        // And the message is still there - naming the field replaces nothing. Asserted on what is
        // left once the label is taken out, because every other assertion here is satisfied by the
        // label alone: deleting the message would otherwise leave this test green.
        var withoutLabel = text.Replace("Вага вантажу, кг", string.Empty, StringComparison.Ordinal);

        Assert.True(SharedMarkup.IsUkrainian(withoutLabel), text);

        Assert.False(SharedMarkup.HasLatinWord(text), text);
        Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Password", nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK))]
    [InlineData("PackageWeightKg", nameof(ErrorCode.DELIVERY_EXCEEDS_VEHICLE_CAPACITY))]
    public async Task A_message_that_already_names_its_field_is_not_labelled_twice(string field, string key)
    {
        // Eight of the catalogue's sentences open with the name of the field they are about,
        // because the same sentence has to stand alone as `error.message` for a REST caller who
        // has no label beside it. Prefixing the label there reads as a stutter - "Пароль: Пароль
        // має містити..." - rather than as the two different claims the label exists to separate.
        //
        // The second case is the one a whole-string comparison misses: the label carries a unit
        // after a comma ("Вага вантажу, кг") that the sentence does not repeat.
        var html = await ComponentRenderer.RenderAsync<DtFailureBanner>(
            new Dictionary<string, object?>
            {
                ["Failures"] = (IReadOnlyList<ScreenFailure>)[new ScreenFailure(field, key)],
            });

        Assert.DoesNotContain("<strong>", html, StringComparison.Ordinal);

        var text = SharedMarkup.TextOf(SharedMarkup.ElementWithClass(html, "div", "alert-danger"));

        Assert.True(SharedMarkup.IsUkrainian(text), text);
        Assert.False(SharedMarkup.HasLatinWord(text), text);
    }

    [Fact]
    public async Task Two_fields_keyed_alike_render_as_two_labelled_banners()
    {
        // The payoff, through the thing that renders it. FailureKeys keeping the pair apart is only
        // half the fix: a banner that showed one line, or showed two lines with one label, would
        // leave a client with the same question about which box to correct.
        var html = await ComponentRenderer.RenderAsync<DtFailureBanner>(
            new Dictionary<string, object?>
            {
                ["Failures"] = (IReadOnlyList<ScreenFailure>)
                [
                    new ScreenFailure("PackageWeightKg", nameof(ErrorCode.COMMON_VALIDATION_FAILED)),
                    new ScreenFailure("DeliveryNotes", nameof(ErrorCode.COMMON_VALIDATION_FAILED)),
                ],
            });

        Assert.Equal(2, SharedMarkup.Occurrences(html, "alert alert-danger"));
        Assert.Equal(2, SharedMarkup.Occurrences(html, "<strong>"));

        var text = SharedMarkup.TextOf(html);

        Assert.Contains("Вага вантажу, кг", text, StringComparison.Ordinal);
        Assert.Contains("Примітки", text, StringComparison.Ordinal);
        Assert.False(SharedMarkup.HasLatinWord(text), text);
    }

    [Fact]
    public async Task A_field_with_no_label_falls_back_to_the_message_alone()
    {
        // The one path that could put a raw CLR property name in front of a user: IStringLocalizer
        // answers a missing key with the key itself (NFR-14).
        var html = await ComponentRenderer.RenderAsync<DtFailureBanner>(
            new Dictionary<string, object?>
            {
                ["Failures"] = (IReadOnlyList<ScreenFailure>)
                [
                    new ScreenFailure("Nonexistent", nameof(ErrorCode.COMMON_VALIDATION_FAILED)),
                ],
            });

        var text = SharedMarkup.TextOf(SharedMarkup.ElementWithClass(html, "div", "alert-danger"));

        Assert.False(SharedMarkup.HasLatinWord(text), text);
        Assert.True(SharedMarkup.IsUkrainian(text), text);
        Assert.DoesNotContain("<strong>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_box_that_searches_again_clears_only_its_own_refusal()
    {
        // DW-41, at the seam where it is decided. The pickup box has been refused; it searches
        // again and succeeds; the dropoff box's refusal is none of its business.
        var pickup = new PlaceSearch(_ => { });
        var dropoff = new PlaceSearch(_ => { });

        await pickup.RunAsync(Refuses, TestContext.Current.CancellationToken);
        await dropoff.RunAsync(Refuses, TestContext.Current.CancellationToken);

        Assert.NotEmpty(pickup.Failures);
        Assert.NotEmpty(dropoff.Failures);

        await pickup.RunAsync(Finds, TestContext.Current.CancellationToken);

        Assert.Empty(pickup.Failures);
        Assert.Single(pickup.Matches);
        Assert.NotEmpty(dropoff.Failures);
    }

    [Fact]
    public async Task A_refused_search_reports_itself_and_leaves_the_other_box_alone()
    {
        var pickup = new PlaceSearch(_ => { });
        var dropoff = new PlaceSearch(_ => { });

        // The dropoff box is given something to lose first. Left untouched it would answer the
        // empty list it was constructed with, and "the other box is unaffected" would be a
        // sentence about the initializer rather than about anything this test ran.
        await dropoff.RunAsync(Finds, TestContext.Current.CancellationToken);

        await pickup.RunAsync(Refuses, TestContext.Current.CancellationToken);

        Assert.Equal(
            [new ScreenFailure("Query", nameof(ErrorCode.COMMON_VALIDATION_FAILED))],
            pickup.Failures);

        // Cleared, so a stale list cannot sit under a refusal that says nothing was searched.
        Assert.Empty(pickup.Matches);

        Assert.Empty(dropoff.Failures);
        Assert.Single(dropoff.Matches);
    }

    [Fact]
    public void Neither_delivery_screen_lets_a_search_write_the_sink_the_submit_path_writes()
    {
        // The criterion a static render cannot reach. RunAsync returns Task precisely so no caller
        // can route a box's failure elsewhere; this is what notices the two being wired back
        // together - by a handler that assigns, or by RunAsync growing a return value again.
        //
        // Matched across the whole file rather than line by line: the assignment and the call sit
        // on separate lines the moment the expression is long enough to wrap, which is how the
        // shape this forbids would look if it came back.
        //
        // The screens are discovered rather than listed, so a third one adopting PlaceSearch
        // inherits the rule by using it - the same reason FieldNameTests discovers its validators.
        var screens = Directory
            .GetFiles(Path.Combine(SharedMarkup.ComponentsDirectory, "Pages"), "*.razor", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("PlaceSearch", StringComparison.Ordinal))
            .ToArray();

        // Not a count: a third screen adopting the box should widen this, not fail it. Asserted
        // non-empty only so a discovery that quietly found nothing cannot pass as two clean files.
        Assert.NotEmpty(screens);

        foreach (var screen in screens)
        {
            var source = File.ReadAllText(screen);

            Assert.All(
                SearchWritingTheSubmitSink,
                pattern => Assert.False(
                    pattern.IsMatch(source),
                    $"{Path.GetFileName(screen)} routes a box's search failure into the submit sink."));
        }
    }

    [Fact]
    public void Each_address_box_renders_its_own_failures_and_only_its_own()
    {
        // The binding itself, which nothing else can see. Pointing the dropoff box's banner at the
        // pickup box compiles, renders, and passes every other test here - and shows a dispatcher a
        // refusal about the box they did not touch.
        var board = SharedMarkup.ReadComponent("Pages", "Deliveries.razor");

        Assert.Equal(1, SharedMarkup.Occurrences(board, @"Failures=""_form.PickupSearch.Failures"""));
        Assert.Equal(1, SharedMarkup.Occurrences(board, @"Failures=""_form.DropoffSearch.Failures"""));

        var mine = SharedMarkup.ReadComponent("Pages", "MyDeliveries.razor");

        Assert.Equal(1, SharedMarkup.Occurrences(mine, @"Failures=""_request.PickupSearch.Failures"""));
        Assert.Equal(1, SharedMarkup.Occurrences(mine, @"Failures=""_request.DropoffSearch.Failures"""));

        // And the sink the boxes were separated *from* still has somewhere to render. Deleting the
        // dialog's banner would leave a refused Save or Delete saying nothing at all, which is the
        // same silence DW-41 is about arrived at from the other side.
        //
        // Counted on the parsed tag rather than on a literal substring: attribute spacing is not
        // the claim being made, and a substring count is satisfied by an occurrence sitting inside
        // a Razor comment.
        Assert.Equal(2, BannersOver(board, "_failures"));
        Assert.Equal(1, BannersOver(mine, "_failures"));

        // The bound the substring count used to carry, restored now that the field controls read
        // the same sink. Every reader of `_failures` on these two screens is either one of those
        // banners or a DtFieldError beneath a box; a third kind of consumer - a second sink wired
        // in, a screen resolving the catalogue inline again - is what this notices.
        Assert.Equal(2 + FieldControls(board), Consumers(board, "_failures"));
        Assert.Equal(1 + FieldControls(mine), Consumers(mine, "_failures"));
    }

    [Fact]
    public void One_file_under_Components_injects_the_error_catalogue_and_it_is_the_banner()
    {
        // What stops the inline block coming back. Every screen used to resolve ErrorMessages
        // itself and render `@Errors[key]` from a list of bare keys - which is precisely the shape
        // that has nowhere to put the field, and it compiles and renders perfectly well. A screen
        // cannot write that block without asking for the localizer first.
        // Code-behind swept alongside the markup: `[Inject] IStringLocalizer<ErrorMessages>` in a
        // Foo.razor.cs partial reconstitutes exactly the shape this forbids, and a *.razor glob
        // would never look at it.
        var offenders = Directory
            .GetFiles(SharedMarkup.ComponentsDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path)
                .Contains("IStringLocalizer<ErrorMessages>", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(SharedMarkup.ComponentsDirectory, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Two, and both of them shared components: the banner, and the per-field control that shows
        // what the banner subtracts. A screen still cannot resolve a message for itself.
        Assert.Equal(
            [
                Path.Combine("Shared", "DtFailureBanner.razor"),
                Path.Combine("Shared", "DtFieldError.razor"),
            ],
            offenders);
    }

    /// <summary>A component's rendered markup: Razor comments are source, not markup.</summary>
    private static string Markup(string source) =>
        Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

    /// <summary>How many banners on a screen read a given failure sink.</summary>
    private static int BannersOver(string source, string sink) =>
        CountOf(source, @"<DtFailureBanner\b[^>]*\bFailures\s*=\s*""" + Regex.Escape(sink) + @"""");

    /// <summary>How many field-level controls a screen renders.</summary>
    private static int FieldControls(string source) => CountOf(source, @"<DtFieldError\b[^>]*>");

    /// <summary>How many bindings of any kind read a given failure sink.</summary>
    private static int Consumers(string source, string sink) =>
        CountOf(source, @"Failures\s*=\s*""" + Regex.Escape(sink) + @"""");

    private static int CountOf(string source, string pattern) =>
        new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5))
            .Matches(Markup(source))
            .Count;

    private static ValidationException Refusal(params (string Field, string MessageKey)[] fields) =>
        new(
            ErrorCode.COMMON_VALIDATION_FAILED,
            "refused",
            fields.Select(field => new FieldError(field.Field, field.MessageKey)));

    private static Task<IReadOnlyList<PlaceMatch>> Finds(SearchPlacesQuery query, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<PlaceMatch>>(Matches);

    private static Task<IReadOnlyList<PlaceMatch>> Refuses(SearchPlacesQuery query, CancellationToken token) =>
        throw new ValidationException(
            ErrorCode.COMMON_VALIDATION_FAILED,
            "query too short",
            [new FieldError("Query", nameof(ErrorCode.COMMON_VALIDATION_FAILED))]);
}
