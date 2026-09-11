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
