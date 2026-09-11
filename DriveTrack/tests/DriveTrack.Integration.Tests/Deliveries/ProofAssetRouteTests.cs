using System.Net;
using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Deliveries;

/// <summary>
/// <c>GET /proof-assets/{id}</c>: the one route a stored image's bytes leave by.
/// <para>
/// It is the only endpoint in the product that accepts either credential, and the only one outside
/// <c>/api</c> that answers an envelope when it refuses. Both are decisions nothing else can check.
/// AD-22 makes <c>/api/*</c> bearer-only and a Blazor circuit holds no token, so an
/// <c>&lt;img&gt;</c> could never fetch an asset from there; and a cookie handler left on its
/// default behaviour answers an unauthenticated image request with the HTML sign-in page at status
/// 200, which a browser renders as a broken image and no log anywhere explains.
/// </para>
/// <para>
/// The last test is the DR-14 claim, and it is deliberately about the whole response rather than
/// about one field: there is no presigned URL, no bucket name and no storage key anywhere a client
/// can see, because the only thing that decides who may read an asset is this route asking the
/// capability again.
/// </para>
/// </summary>
public class ProofAssetRouteTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_bearer_caller_gets_the_bytes_and_the_stored_content_type()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var captured = await CaptureAsync(factory, client, cancellationToken);

        using var response = await ProofApi.AssetAsync(
            client, captured.SignatureAssetId, cancellationToken, token: captured.DriverToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        // These are bytes a driver uploaded under a type that driver declared, and nothing looked
        // inside the file. Without nosniff a browser may act on what it finds rather than on what
        // was declared, so a "PNG" that is really markup would render as markup on this
        // application's own origin.
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));

        // The bytes that were uploaded, not a re-encoding of them and not an envelope wrapped round
        // them: the route is a minimal-API endpoint, so no MVC filter ever sees the result.
        Assert.Equal(ProofApi.Png, await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task A_cookie_caller_gets_the_bytes_from_the_same_route()
    {
        // The half AD-22 cannot serve. The scheme is chosen by the credential the caller actually
        // presented rather than by the path, which is what "accepts either scheme" has to mean for a
        // URL a browser loads with a cookie and an API client loads with a token.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

        var captured = await CaptureAsync(factory, client, cancellationToken);

        var cookie = await ProofApi.CookieAsync(
            factory, captured.DriverEmail, FleetApi.Password, cancellationToken);

        using var response = await ProofApi.AssetAsync(
            client, captured.SignatureAssetId, cancellationToken, cookie: cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ProofApi.Png, await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task An_anonymous_request_is_an_enveloped_401_and_never_a_redirect()
    {
        // The failure the widened cookie events exist to prevent. Left on its default the handler
        // answers 302 to /sign-in, the test client follows it, and the image request ends as 200
        // text/html - a broken image with a successful status, which is the worst shape a failure
        // can take.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

        var captured = await CaptureAsync(factory, client, cancellationToken);

        using var response = await ProofApi.AssetAsync(
            client, captured.SignatureAssetId, cancellationToken);

        await FleetApi.AssertFailureAsync(
            response,
            HttpStatusCode.Unauthorized,
            ErrorCode.AUTH_UNAUTHENTICATED,
            cancellationToken);

        Assert.Null(response.Headers.Location);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Another_clients_asset_is_a_non_disclosing_404()
    {
        // The scope narrows in the query, so an asset belonging to somebody else's delivery is not
        // found rather than refused - and an id that never existed answers identically, which is
        // what stops the route being a way to count what the system holds.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var captured = await CaptureAsync(factory, client, cancellationToken);
        var stranger = await DeliveryApi.ClientAsync(
            client, captured.DispatcherToken, cancellationToken);

        using (var refused = await ProofApi.AssetAsync(
                   client, captured.SignatureAssetId, cancellationToken, token: stranger.Token))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.NotFound,
                ErrorCode.COMMON_NOT_FOUND,
                cancellationToken);

            Assert.Equal("application/json", refused.Content.Headers.ContentType?.MediaType);
        }

        using var missing = await ProofApi.AssetAsync(
            client, captured.SignatureAssetId + 10_000, cancellationToken, token: stranger.Token);

        await FleetApi.AssertFailureAsync(
            missing,
            HttpStatusCode.NotFound,
            ErrorCode.COMMON_NOT_FOUND,
            cancellationToken);
    }

    [Fact]
    public async Task A_row_whose_object_the_store_no_longer_holds_is_a_404()
    {
        // The "key missing in the store" row of the matrix. A bucket emptied out of band leaves a
        // row naming nothing, and the honest answer to "show me this image" is that there is none -
        // a 500 would report a defect where there is only an absence.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var captured = await CaptureAsync(factory, client, cancellationToken);

        store.Empty = true;

        using var response = await ProofApi.AssetAsync(
            client, captured.SignatureAssetId, cancellationToken, token: captured.DriverToken);

        await FleetApi.AssertFailureAsync(
            response,
            HttpStatusCode.NotFound,
            ErrorCode.COMMON_NOT_FOUND,
            cancellationToken);
    }

    [Fact]
    public async Task No_response_anywhere_carries_a_storage_key_or_an_absolute_store_url()
    {
        // DR-14 as a property of the wire rather than of one DTO. A URL would move the FR-122
        // decision from the capability to whoever holds the link, and a storage key would let a
        // caller address the bucket directly the moment it was ever reachable.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var captured = await CaptureAsync(factory, client, cancellationToken);

        using var read = await ProofApi.ReadAsync(
            client, captured.DispatcherToken, captured.DeliveryId, cancellationToken);

        var body = await read.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain("://", body, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Amz", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Signature=", body, StringComparison.OrdinalIgnoreCase);

        // And the keys the store actually minted appear nowhere in what a caller is handed.
        Assert.All(store.Saved, saved =>
            Assert.DoesNotContain(saved.Key, body, StringComparison.Ordinal));
    }

    /// <summary>What one arranged capture left behind.</summary>
    private sealed record Captured(
        string DispatcherToken,
        string DriverToken,
        string DriverEmail,
        int DeliveryId,
        int SignatureAssetId);

    /// <summary>
    /// A driver, a delivery of theirs and a captured proof, with the signature's asset id read back
    /// off the view — which is the only place it is published, and deliberately the only thing about
    /// an asset that a caller ever learns.
    /// </summary>
    private static async Task<Captured> CaptureAsync(
        ApiFactory factory,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId),
            cancellationToken);

        using var captured = await ProofApi.CaptureAsync(
            client, driver.Token, deliveryId, cancellationToken, signature: ProofApi.Png);

        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);

        var view = await FleetApi.DataAsync(captured, cancellationToken);

        var signature = view.GetProperty("assets")
            .EnumerateArray()
            .First(asset => asset.GetProperty("kind").GetString() == "Signature");

        return new Captured(
            dispatcher,
            driver.Token,
            driver.Email,
            deliveryId,
            signature.GetProperty("id").GetInt32());
    }
}
