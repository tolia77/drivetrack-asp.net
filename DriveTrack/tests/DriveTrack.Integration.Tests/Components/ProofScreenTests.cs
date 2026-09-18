using System.Text.RegularExpressions;
using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// What the proof screens actually render, with the capability stubbed.
/// <para>
/// A source scan cannot make these claims. "A dispatcher is shown who captured the proof and a
/// client is not" is a property of the output, and the file contains all the same words whether it
/// holds or not — the component renders the name when it was given one, and whether it was given one
/// is the capability's decision (AD-17). Rendering it once per role is what proves the two halves
/// are wired to each other.
/// </para>
/// <para>
/// Rendering is static — <see cref="ComponentRenderer"/> never reaches <c>OnAfterRenderAsync</c> and
/// dispatches no events — so the capture screen's geolocation, its signature pad and its submit are
/// all unreachable from here, and the predicate that decides who is offered capture is lifted out of
/// the screen and asserted directly instead.
/// </para>
/// </summary>
public class ProofScreenTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_dispatcher_is_shown_who_captured_the_proof()
    {
        // The capability supplies the name for an administrator or a dispatcher, and the panel
        // renders what it was given. Both halves are needed: a panel that always rendered the field
        // would pass the test below by printing an empty cell.
        var html = await RenderProofAsync(capturedByName: "Тарас Шевченко");

        Assert.Contains("Тарас Шевченко", html, StringComparison.Ordinal);
        Assert.Contains("Зафіксував", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_is_shown_no_capturer_at_all()
    {
        // FR-122 and AD-17 reaching the markup: not merely that the name is absent, but that the
        // label introducing it is too - a row reading "Зафіксував —" would still tell a client that
        // somebody they are not allowed to know about exists.
        var html = await RenderProofAsync(capturedByName: null);

        Assert.DoesNotContain("Зафіксував", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Шевченко", html, StringComparison.Ordinal);

        // And the rest of the proof is still rendered, because "discloses nothing" must not be
        // achieved by rendering nothing.
        Assert.Contains("Олена Петренко", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_asset_is_fetched_from_the_authenticated_route()
    {
        // DR-14 at the component tier: the markup names an id on a route this application serves,
        // and never a bucket, a key or anything with a scheme in it. A presigned URL here would move
        // the FR-122 decision to whoever copied the page source.
        var html = await RenderProofAsync(capturedByName: "Тарас Шевченко");

        Assert.Contains("src=\"/proof-assets/7\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"/proof-assets/8\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("://", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_asset_route_is_built_from_the_id_and_nothing_else()
    {
        // Asserted directly as well as through the markup, because getting the prefix wrong fails
        // nothing visibly: the page is well formed and the route simply does not exist, which a
        // browser renders as a broken image.
        Assert.Equal(
            "/proof-assets/42",
            Web.Components.Pages.ProofView.Source(new ProofAssetView(42, ProofAssetKind.Photo, "image/png")));
    }

    [Theory]
    [InlineData(UserRole.Driver, false, true)]
    [InlineData(UserRole.Driver, true, false)]
    [InlineData(UserRole.Client, false, false)]
    [InlineData(UserRole.Client, true, false)]
    [InlineData(UserRole.Dispatcher, false, false)]
    [InlineData(UserRole.Admin, false, false)]
    public void Only_a_driver_with_no_proof_yet_is_offered_the_capture_action(
        UserRole role,
        bool hasProof,
        bool expected)
    {
        // The predicate the own-deliveries screen draws its capture button from. Static rendering
        // dispatches no event that could capture anything, so its effect is unreachable from a
        // render test - and inverting it would leave every suite green while a client was handed an
        // action the guard could only ever refuse (FR-12).
        Assert.Equal(
            expected,
            Web.Components.Pages.MyDeliveries.OffersProofCapture(role, hasProof));
    }

    [Theory]
    [InlineData(1024L, false)]
    [InlineData(ProofAssetRules.MaximumAssetBytes, false)]
    [InlineData(ProofAssetRules.MaximumAssetBytes + 1, true)]
    [InlineData(0L, true)]
    public void An_unacceptable_photograph_reaches_the_validator_without_being_opened(
        long size,
        bool expectedEmpty)
    {
        // The capture panel's one real decision, and the same reason the predicate above is lifted
        // out: nothing in the suite runs this component. A file the validator will refuse by length
        // is carried as an empty stream with its real declared size, so the refusal is the 422 the
        // contract names; opening it first would throw IOException out of the browser file API and
        // become a 500 for a file the contract has a 422 for.
        var upload = Web.Components.Pages.ProofCapture.Photo(new StubBrowserFile(size));

        Assert.Equal(ProofAssetKind.Photo, upload.Kind);

        // The declared length is the real one either way - that is what lets the validator name the
        // limit rather than reporting an empty file.
        Assert.Equal(size, upload.Length);
        Assert.Equal("image/jpeg", upload.ContentType);
        Assert.Equal(expectedEmpty, ReferenceEquals(Stream.Null, upload.Content));
    }

    // =====================================================================================
    // The capture panel, as a driver standing at a door meets it
    // =====================================================================================

    [Fact]
    public async Task The_capture_panel_names_the_parcel_it_is_being_filed_against()
    {
        // The summary the design leads with, and the reason it is a parameter: the own-deliveries
        // screen is already holding this row. Without it the panel said nothing at all about which
        // delivery it was about, and a capture filed against the wrong one is evidence about the
        // wrong hand-over - which nothing downstream can detect, because every field in it is valid.
        var html = await RenderCaptureAsync(Parcel);

        var text = SharedMarkup.TextOf(html);

        // The door, the parcel and the promise, in the readings both delivery screens already use:
        // a driver must not read one wording on the row they pressed and another on the panel it
        // opened.
        Assert.Contains("вул. Хрещатик, 22", text, StringComparison.Ordinal);
        Assert.Contains("Дві коробки", text, StringComparison.Ordinal);
        Assert.Contains(
            Web.Components.Pages.CellText.Window(Parcel.WindowEarliestAt, Parcel.WindowLatestAt),
            text,
            StringComparison.Ordinal);

        // And the status, which is the fourth fact the design's card carries - through the one
        // component that owns the vocabulary rather than a fifth copy of four localizer keys.
        Assert.Contains("dt-status--transit", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_panel_given_no_summary_draws_no_card_rather_than_an_empty_one()
    {
        // The card is withheld, not blanked. A summary naming nothing looks like a delivery whose
        // details failed to load, which is worse than no card - and the capture itself is unaffected
        // either way, because the command carries the id and never the row.
        var html = await RenderCaptureAsync(summary: null);

        Assert.DoesNotContain("dt-proof-summary", html, StringComparison.Ordinal);

        // The rest of the panel is still there, so "says nothing" is not achieved by rendering
        // nothing.
        Assert.Contains("dt-proof-form", html, StringComparison.Ordinal);
        Assert.Contains("dt-signature-pad", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_freshly_opened_pad_invites_a_signature_and_says_it_has_none()
    {
        // The matrix's "pad untouched" row. A 10rem rectangle with no frame, no baseline and no
        // sentence was the scaffold's whole affordance, and a driver holding a parcel has no way of
        // knowing a blank box is somewhere to draw - the prompt and the dashed frame are what say so.
        var html = await RenderCaptureAsync(Parcel);
        var text = SharedMarkup.TextOf(html);

        Assert.Contains("Розпишіться", text, StringComparison.Ordinal);
        Assert.Contains("Порожньо", text, StringComparison.Ordinal);

        // And not the signed state, which is the half a static render can rule out: the modifier
        // that turns the frame solid, and the hint that says a stroke registered.
        Assert.DoesNotContain("dt-signature-pad--signed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Підписано", text, StringComparison.Ordinal);

        // Clear is on the page from the first render rather than appearing with the first stroke: a
        // mis-drawn signature has to be as easy to undo as it was to make, and a control that
        // arrives only once it is needed is one a driver has not seen before they need it.
        Assert.Contains("dt-signature-clear", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "dt-signature-pad--signed")]
    public void The_frame_goes_solid_exactly_when_there_is_a_stroke_on_it(bool signed, string expected)
    {
        // The other half of the pad's states, asserted directly because it is unreachable from a
        // render: this harness dispatches no pointer events and executes no module, so every pad it
        // draws is the empty one. Written the other way round, the whole suite stays green while a
        // driver meets a solid frame inviting nothing and a dashed one over a signature already
        // given.
        Assert.Equal(expected, Web.Components.Shared.DtSignaturePad.Modifier(signed));
    }

    [Fact]
    public void The_pad_tells_dot_net_about_the_first_stroke_and_publishes_the_flag_it_keeps()
    {
        // The seam the design's signed state rests on, and the one no render can reach: the stroke
        // happens in a browser and the markup is rendered on the server. Read as source for the
        // reason MapAssetTests reads the module paths that way - there is no JavaScript runner in
        // this solution, and the alternative is asserting nothing at all.
        var module = SharedMarkup.ReadShared("DtSignaturePad.razor.js");
        var component = SharedMarkup.ReadShared("DtSignaturePad.razor");

        // Matched on the tokens that carry the meaning rather than on braces and spacing: a
        // reformat, or the same guard written as an early return, would break a literal without
        // anything being wrong. What must hold is that the report is conditional on the flag, and
        // that it names the method the component publishes.
        Assert.Matches(@"!\s*state\.marked", module);
        Assert.Matches(@"invokeMethodAsync\(\s*""NotifyMarkedAsync""", module);

        // The pull, which is what clearing reads back rather than assuming. Both halves are needed:
        // the export alone is a function nobody calls, and the callback alone leaves the pad saying
        // "signed" after a wipe.
        Assert.Matches(@"export\s+function\s+marked\s*\(", module);
        Assert.Matches(@"InvokeAsync<bool>\(\s*""marked""", component);

        // And the single line the read-back depends on. Without it `clear()` wipes the pixels and
        // leaves the flag set, so the pad comes back from a wipe still reading "signed" and the
        // capture still sends the drawing that was supposed to be gone.
        Assert.Matches(@"state\.marked\s*=\s*false", ClearBody(module));

        // And the component's end of both.
        Assert.Contains("[JSInvokable]", component, StringComparison.Ordinal);
        Assert.Matches(@"InvokeVoidAsync\(\s*""attach"",\s*_element,\s*_self\s*\)", component);
    }

    [Fact]
    public async Task The_photo_control_counts_what_has_been_chosen_against_what_is_allowed()
    {
        // The matrix's photo rows, at the state a freshly opened panel is in: nothing chosen, the
        // count against the ceiling, and the Add tile offered. The figure the badge shows is
        // ProofAssetRules' own, so the panel cannot advertise a limit the validator does not hold.
        var html = await RenderCaptureAsync(Parcel);
        var text = SharedMarkup.TextOf(html);

        // Read off the page rather than out of the badge element: the badge holds a nested span for
        // the hidden word, and a reader that stopped at the first closing tag would stop before the
        // figures - which is the half that matters.
        Assert.Contains("dt-proof-photo-count", html, StringComparison.Ordinal);
        Assert.Contains("Обрано фотографій", text, StringComparison.Ordinal);
        Assert.Contains($"0 / {ProofAssetRules.MaximumPhotos}", text, StringComparison.Ordinal);

        Assert.Contains("dt-proof-photo-add", html, StringComparison.Ordinal);

        // The picker keeps everything it had. `multiple` is what lets one gesture pick five, and the
        // three-type list is ProofAssetRules' allowlist as a browser spells it - `image/*` would be
        // a widening that let an HEIC through the picker to be refused afterwards.
        var picker = Regex.Match(
            html,
            @"<input[^>]*dt-proof-picker[^>]*>",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(picker.Success, html);
        Assert.Contains("multiple", picker.Value, StringComparison.Ordinal);
        Assert.Contains("accept=\"image/png,image/jpeg,image/webp\"", picker.Value, StringComparison.Ordinal);

        // And it carries no `capture`. A present `capture` makes a phone open the camera and hand
        // back a single frame, overriding `multiple` on exactly the device this panel is drawn for -
        // so the attribute would quietly cost both the gallery route and the one-gesture multi-pick
        // the assertion above is about.
        Assert.DoesNotContain("capture=", picker.Value, StringComparison.Ordinal);

        // One picker on a fresh panel, and the one that is live rather than disabled: the spent ones
        // accumulate only as the driver picks.
        Assert.Equal(1, SharedMarkup.Occurrences(html, "dt-proof-picker"));
        Assert.DoesNotContain("disabled", picker.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(ProofAssetRules.MaximumPhotos, false)]
    [InlineData(ProofAssetRules.MaximumPhotos + 1, false)]
    public void The_add_tile_is_withdrawn_at_the_maximum_and_never_refuses_a_file(int chosen, bool offered)
    {
        // Lifted out for the reason the capture predicate on the own-deliveries screen is: static
        // rendering dispatches no event that could choose a file, so the rule's effect is
        // unreachable from a render - and the last row is a count only a caller going round this
        // control can produce.
        //
        // What this is NOT is a limit. The tile going is a drawing decision; the refusal is
        // CaptureProofCommandValidator's, and it names the ceiling in the same sentence the REST
        // surface answers with (AD-9). A panel that threw the sixth file away here would be a
        // second, stricter definition of a valid proof.
        Assert.Equal(offered, Web.Components.Pages.ProofCapture.OffersAnotherPhoto(chosen));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(ProofAssetRules.MaximumPhotos, false)]
    [InlineData(ProofAssetRules.MaximumPhotos + 1, true)]
    public void A_selection_over_the_ceiling_is_named_rather_than_counted(int chosen, bool over)
    {
        // Reachable because picking adds rather than replaces: four and then three is seven. The
        // badge must not print "7 / 5" - a count above the ceiling in the shape a valid one uses
        // reads as a quota that has simply grown - so it says the selection is over the limit
        // instead. Lifted out for the reason the Add-tile rule is: no render can choose a file.
        //
        // It decides what the pill says and nothing else. The files are still sent, and
        // CaptureProofCommandValidator is still what turns them down (AD-9).
        Assert.Equal(over, Web.Components.Pages.ProofCapture.IsOverTheLimit(chosen));
    }

    [Fact]
    public async Task A_dropoff_with_no_resolved_address_still_names_where_the_parcel_is_going()
    {
        // FR-94's fallback, on the summary card. Nothing in this milestone resolves an address, so
        // this is the branch a driver actually meets - and a card that rendered a null address would
        // be a blank line where the door should be. The coordinates are what the record holds
        // (DR-11), so they are what is shown, through the reading both delivery screens share.
        var unresolved = Parcel with
        {
            Dropoff = new LocationView(new MapLocation(50.4470, 30.5220), null),
        };

        var text = SharedMarkup.TextOf(await RenderCaptureAsync(unresolved));

        Assert.Contains("50.447; 30.522", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_capture_with_nowhere_to_record_it_cannot_be_sent()
    {
        // The matrix's "location pending" row, which is the state every panel opens in: the device
        // has not answered, so there is no capture point and the action is disabled. FR-119 records
        // where the hand-over happened and this panel draws no coordinate box, so a submit without
        // one can only ever return a 422 naming a field nobody could fill in - after every
        // photograph has been streamed up the circuit.
        //
        // The design says Save stays enabled and the misses are shown afterwards. That holds for
        // every box on this panel and not for this one, because this one has no box.
        var html = await RenderCaptureAsync(Parcel);

        var submit = Regex.Match(
            html,
            @"<button[^>]*dt-proof-submit[^>]*>",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(submit.Success, html);
        Assert.Contains("disabled", submit.Value, StringComparison.Ordinal);

        // What the button says, and not only what its opening tag carries. The label is chosen by
        // the same flag the `disabled` binding reads, so a panel that has sent nothing must offer
        // the action rather than report a save - and swapping the two arms is a change nothing else
        // in the suite would see.
        var label = SharedMarkup.TextOf(SharedMarkup.ElementWithClass(html, "button", "dt-proof-submit"));

        Assert.Contains("Зберегти підтвердження", label, StringComparison.Ordinal);
        Assert.DoesNotContain("Зберігаємо", label, StringComparison.Ordinal);

        // And the sentence a driver acts on instead, rather than a silent dead button.
        Assert.Contains("Визначаємо місце", SharedMarkup.TextOf(html), StringComparison.Ordinal);
    }

    /// <summary>
    /// The body of the module's <c>clear</c> export, so an assertion about what clearing does cannot
    /// be satisfied by the same line sitting in a different function.
    /// </summary>
    /// <param name="module">The module's source.</param>
    private static string ClearBody(string module)
    {
        var start = module.IndexOf("export function clear", StringComparison.Ordinal);

        Assert.True(start >= 0, "The pad's module exports no clear().");

        // To the closing brace in the first column, which is where a top-level function ends.
        var end = module.IndexOf("\n}", start, StringComparison.Ordinal);

        Assert.True(end > start, "clear() is never closed.");

        return module[start..end];
    }

    /// <summary>
    /// The row a capture is filed against: a resolved dropoff, a described parcel and a window with
    /// both bounds, so the summary card has all three of the facts it draws.
    /// </summary>
    private static readonly AssignedDeliverySummary Parcel = new(
        1,
        new LocationView(new MapLocation(50.4501, 30.5234), "вул. Січових Стрільців, 5"),
        new LocationView(new MapLocation(50.4470, 30.5220), "вул. Хрещатик, 22"),
        "Дві коробки",
        4.5m,
        null,
        Noon,
        Noon.AddHours(2),
        DeliveryStatus.InTransit,
        Noon.AddDays(-1),
        false);

    /// <summary>
    /// The capture panel, rendered as a driver's dialog builds it. The capability is stubbed and
    /// never called: nothing on a first render captures anything, and static rendering never reaches
    /// <c>OnAfterRenderAsync</c> - which is exactly why the panel opens with no coordinates and is
    /// the state the matrix's "location pending" row describes.
    /// </summary>
    /// <param name="summary">The row the capture is about, or null for a panel given none.</param>
    private static Task<string> RenderCaptureAsync(AssignedDeliverySummary? summary) =>
        ComponentRenderer.RenderAsync<Web.Components.Pages.ProofCapture>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["DeliveryId"] = 1,
                ["Summary"] = summary,
            },
            services => services.AddSingleton<IProofOfDeliveryService>(new StubProofService(null)));

    private static Task<string> RenderProofAsync(string? capturedByName) =>
        ComponentRenderer.RenderAsync<Web.Components.Pages.ProofView>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["DeliveryId"] = 1,
            },
            services => services.AddSingleton<IProofOfDeliveryService>(
                new StubProofService(capturedByName)));

    /// <summary>
    /// One captured proof, with the capturer's name already decided. The decision itself is the
    /// capability's and is asserted over HTTP; what this stands in for is "the panel was given a
    /// name" and "the panel was not".
    /// </summary>
    private sealed class StubProofService(string? capturedByName) : IProofOfDeliveryService
    {
        public Task<ProofOfDeliveryView> CaptureAsync(
            int deliveryId,
            CaptureProofCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(View());

        public Task<ProofOfDeliveryView> GetAsync(int deliveryId, CancellationToken cancellationToken) =>
            Task.FromResult(View());

        public Task<ProofAssetContent> OpenAssetAsync(int assetId, CancellationToken cancellationToken) =>
            Task.FromResult(new ProofAssetContent("image/png", Stream.Null));

        private ProofOfDeliveryView View() =>
            new(
                1,
                "Олена Петренко",
                new LocationView(new MapLocation(50.4501, 30.5234), null),
                Noon,
                capturedByName,
                [
                    new ProofAssetView(7, ProofAssetKind.Signature, "image/png"),
                    new ProofAssetView(8, ProofAssetKind.Photo, "image/jpeg"),
                ]);
    }

    /// <summary>
    /// A picked file, with nothing behind it. <c>OpenReadStream</c> answers a real stream, which is
    /// the whole point: the assertion is which branch of the panel's ternary ran, and the two
    /// branches are distinguishable only by what came back.
    /// </summary>
    private sealed class StubBrowserFile(long size) : IBrowserFile
    {
        public string Name => "фото.jpg";

        public DateTimeOffset LastModified => Noon;

        public long Size => size;

        public string ContentType => "image/jpeg";

        public Stream OpenReadStream(
            long maxAllowedSize = 512000,
            CancellationToken cancellationToken = default) =>
            new MemoryStream([1, 2, 3], writable: false);
    }
}
