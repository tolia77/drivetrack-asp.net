using System.Net;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Deliveries;

/// <summary>
/// FR-25, FR-27, FR-96 and AD-3's anti-post-filtering rule: what a driver and a client are shown,
/// and what they are not told.
/// <para>
/// Two separate claims, and the second is the one that is easy to lose. "Only their own rows" is
/// about which rows come back; "no counterparty identity" is about what is in them. A mapping that
/// nulled the parties instead of using a type without them would satisfy the first and fail the
/// second the day somebody reused the dispatch summary here, so the payload is read as text and
/// searched for names that must not be in it.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DeliveryDisclosureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_driver_is_shown_their_own_deliveries_and_no_others()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var other = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var mine = new[]
        {
            await DeliveryApi.PostAsync(
                client, dispatcher, DeliveryApi.NewDelivery(driverId: driver.DriverId), cancellationToken),
            await DeliveryApi.PostAsync(
                client, dispatcher, DeliveryApi.NewDelivery(driverId: driver.DriverId), cancellationToken),
        };

        await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(driverId: other.DriverId), cancellationToken);
        await DeliveryApi.PostAsync(client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);
        await DeliveryApi.PostAsync(client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        var rows = await MineAsync(client, driver.Token, query: string.Empty, cancellationToken);

        Assert.Equal(mine, rows.Select(row => row.GetProperty("id").GetInt32()).ToArray());
    }

    [Fact]
    public async Task A_client_is_shown_their_own_deliveries_and_no_others()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);
        var other = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var mine = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(clientId: requester.ClientId), cancellationToken);

        await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(clientId: other.ClientId), cancellationToken);
        await DeliveryApi.PostAsync(client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);
        await DeliveryApi.PostAsync(client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);
        await DeliveryApi.PostAsync(client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        var rows = await MineAsync(client, requester.Token, query: string.Empty, cancellationToken);

        Assert.Equal(mine, Assert.Single(rows).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Neither_party_learns_who_the_other_is()
    {
        // FR-27 and FR-96, asserted against the serialized body rather than against a deserialized
        // shape: the claim is that the name is not on the wire at all, and a typed read could only
        // ever look at the fields somebody remembered to check.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId, clientId: requester.ClientId),
            cancellationToken);

        foreach (var token in new[] { driver.Token, requester.Token })
        {
            using var response = await FleetApi.SendAsync(
                client, HttpMethod.Get, "/api/deliveries/mine", token, body: null, cancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // The seeded names, which are the only two people this delivery has.
            Assert.DoesNotContain("Шевченко", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Петренко", body, StringComparison.Ordinal);

            // And no field for one, which is what makes the claim structural: the type this route
            // answers has no driver and no client property at all (AD-17).
            foreach (var row in JsonDocument.Parse(body).RootElement.GetProperty("data").EnumerateArray())
            {
                Assert.False(row.TryGetProperty("driver", out _));
                Assert.False(row.TryGetProperty("client", out _));
                Assert.False(row.TryGetProperty("driverId", out _));
                Assert.False(row.TryGetProperty("clientId", out _));
            }
        }
    }

    [Fact]
    public async Task A_page_is_scoped_in_the_query_rather_than_filtered_after_it()
    {
        // AD-3's rule, and the defect it exists to make unrepresentable. Ninety deliveries belong
        // to nobody and the last ten to this driver; a page fetched unscoped and filtered afterwards
        // answers an empty first page, and no amount of paging further finds them.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        // Seeded through the context rather than over HTTP: a hundred round trips would make this
        // a test of the endpoint's throughput, and what is under test is where the WHERE goes.
        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            for (var index = 0; index < 90; index++)
            {
                context.Deliveries.Add(Seed.NewDelivery());
            }

            await context.SaveChangesAsync(cancellationToken);

            for (var index = 0; index < 10; index++)
            {
                context.Deliveries.Add(Seed.NewDelivery(driverId: new DriverId(driver.DriverId)));
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        var rows = await MineAsync(client, driver.Token, "?offset=0&limit=10", cancellationToken);

        Assert.Equal(10, rows.Length);

        // The second page of the driver's own rows is empty, which is the other half of the same
        // claim: the offset counts the caller's rows and not everybody's.
        var second = await MineAsync(client, driver.Token, "?offset=10&limit=10", cancellationToken);

        Assert.Empty(second);
    }

    [Fact]
    public async Task The_dispatch_list_is_refused_to_a_driver_and_a_client_rather_than_narrowed()
    {
        // The role check and the scope are different decisions, and both are on the dispatch route.
        // Narrowing a driver's view of it instead of refusing would be a second, quieter answer to
        // "who may run dispatch" - and would put a counterparty's name in front of them.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId, clientId: requester.ClientId),
            cancellationToken);

        foreach (var token in new[] { driver.Token, requester.Token })
        {
            using var refused = await FleetApi.SendAsync(
                client, HttpMethod.Get, "/api/deliveries", token, body: null, cancellationToken);

            await FleetApi.AssertFailureAsync(
                refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }
    }

    [Fact]
    public async Task A_caller_who_runs_dispatch_gets_every_row_from_the_own_deliveries_route()
    {
        // The consequence of ListMineAsync taking no role check, pinned rather than left implied.
        // A dispatcher's scope is unrestricted, so "mine" means every delivery for them - which is
        // exactly why /my-deliveries is fenced to Driver and Client and why the dispatch board is a
        // separate route rather than this one with a different projection.
        //
        // Worth a test of its own because the alternative reading is plausible and wrong: a future
        // edit that "fixed" this by narrowing a dispatcher to nothing would silently give the two
        // roles that run dispatch an empty screen, and no other assertion here would notice.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(driverId: driver.DriverId), cancellationToken);
        await DeliveryApi.PostAsync(client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);
        await DeliveryApi.PostAsync(client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        var rows = await MineAsync(client, dispatcher, query: string.Empty, cancellationToken);

        Assert.Equal(3, rows.Length);

        // And still the withheld shape: the route answers one type whoever calls it, so a dispatcher
        // reading it gets no party names either. The board is where those live.
        foreach (var row in rows)
        {
            Assert.False(row.TryGetProperty("driver", out _));
            Assert.False(row.TryGetProperty("client", out _));
        }
    }

    [Fact]
    public async Task An_anonymous_caller_is_unauthenticated_on_both_routes()
    {
        // 401 rather than 403: the caller presented no credentials, and FR-13 branches on the
        // difference.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/api/deliveries", "/api/deliveries/mine" })
        {
            using var refused = await FleetApi.SendAsync(
                client, HttpMethod.Get, path, token: null, body: null, cancellationToken);

            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Unauthorized,
                ErrorCode.AUTH_UNAUTHENTICATED,
                cancellationToken);
        }
    }

    private static async Task<JsonElement[]> MineAsync(
        HttpClient client,
        string token,
        string query,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client,
            HttpMethod.Get,
            "/api/deliveries/mine" + query,
            token,
            body: null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await FleetApi.DataAsync(response, cancellationToken)).EnumerateArray()];
    }
}
