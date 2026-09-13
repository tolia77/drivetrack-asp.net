using System.Net;
using DriveTrack.Integration.Tests.Deliveries;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;

namespace DriveTrack.Integration.Tests.Authorization;

/// <summary>
/// The other half of a boundary: not what a caller may <em>do</em>, but what they are told while
/// doing something they are allowed to do.
/// <para>
/// AD-17 asks for a direct assertion on the payload rather than for an argument about the shape of a
/// DTO. A type with no field for a counterparty proves the field cannot be populated; it proves
/// nothing about the seven other routes that answer a different type, and nothing at all about a
/// field added later. So the seeded driver's family name and the seeded client's telephone number
/// are distinctive strings, and these tests look for them in the raw bytes of the response.
/// </para>
/// <para>
/// Each one is paired with a dispatcher reading the same route. Without that pair, a test that the
/// string is absent would pass just as happily against a route that answered nothing at all.
/// </para>
/// </summary>
public class DisclosureBoundaryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_client_is_never_told_the_name_of_the_driver_carrying_their_parcel()
    {
        // FR-27 and FR-96 across all three routes a client can reach the delivery by. The timeline
        // is the one that needs saying out loud: every entry carries an actor, the actor of the
        // status changes was the driver, and the disclosure decision is a null in the mapping step
        // rather than a row that was not written.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        foreach (var path in PartyRoutes(world))
        {
            var body = await BodyAsync(world, path, world.ClientA.Token, cancellationToken);

            Assert.DoesNotContain(
                AuthorizationWorld.DriverALastName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(AuthorizationWorld.DriverAName, body, StringComparison.Ordinal);

            // The given name on its own as well, because the two assertions above are satisfied by
            // a payload that discloses only it - and a first name is an identity too.
            Assert.DoesNotContain(
                AuthorizationWorld.DriverAFirstName, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_same_routes_hand_dispatch_the_driver_by_name()
    {
        // The pair. Dispatch reads the identical routes and is told who acted, which is what makes
        // the absences above a decision rather than an empty payload - and FR-106's "who did this"
        // is what a dispatch desk is for.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var timeline = await BodyAsync(
            world,
            $"/api/deliveries/{world.DeliveryId}/timeline",
            world.DispatcherToken,
            cancellationToken);

        var proof = await BodyAsync(
            world,
            $"/api/deliveries/{world.DeliveryId}/proof",
            world.DispatcherToken,
            cancellationToken);

        var board = await BodyAsync(
            world, $"/api/deliveries/{world.DeliveryId}", world.DispatcherToken, cancellationToken);

        Assert.Contains(AuthorizationWorld.DriverALastName, timeline, StringComparison.Ordinal);
        Assert.Contains(AuthorizationWorld.DriverALastName, proof, StringComparison.Ordinal);
        Assert.Contains(AuthorizationWorld.DriverALastName, board, StringComparison.Ordinal);

        // Every string the client-facing test asserts is absent, asserted present here. Without
        // these two, "the given name never appears" and "the full name never appears" would go on
        // passing against a product that had stopped composing either one anywhere.
        Assert.Contains(AuthorizationWorld.DriverAFirstName, timeline, StringComparison.Ordinal);
        Assert.Contains(AuthorizationWorld.DriverAName, timeline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clients_timeline_still_says_which_role_moved_the_parcel()
    {
        // The disclosure rule is about identity, not about the history. A client who is told
        // nothing at all cannot see that their parcel was picked up, so the role stays and only the
        // name goes - which is also what distinguishes "withheld" from "there was no entry".
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        using var response = await DeliveryApi.TimelineAsync(
            world.Client, world.ClientA.Token, world.DeliveryId, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entries = (await FleetApi.DataAsync(response, cancellationToken)).EnumerateArray().ToArray();

        Assert.NotEmpty(entries);

        foreach (var entry in entries)
        {
            Assert.Equal(
                System.Text.Json.JsonValueKind.Null,
                entry.GetProperty("actorName").ValueKind);

            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("actorRole").GetString()));
        }

        Assert.Contains(entries, entry => entry.GetProperty("actorRole").GetString() == "Driver");
    }

    [Fact]
    public async Task A_driver_is_never_told_their_clients_name_telephone_number_or_email()
    {
        // FR-27 from the other side. A driver needs the parcel, the two points and the window; the
        // person is the dispatch desk's to hold, and a delivery screen that showed a number would
        // be a disclosure no route intends and no type declares.
        //
        // Name, telephone number and email address, which is every identifying field a client
        // account has: there is no postal address anywhere in this product for a client, so there is
        // none to assert about.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        foreach (var path in PartyRoutes(world))
        {
            var body = await BodyAsync(world, path, world.DriverA.Token, cancellationToken);

            Assert.DoesNotContain(AuthorizationWorld.ClientAPhone, body, StringComparison.Ordinal);
            Assert.DoesNotContain(world.ClientA.Email, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                AuthorizationWorld.ClientALastName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(
                AuthorizationWorld.ClientAFirstName, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_client_roster_does_carry_the_number_the_driver_never_sees()
    {
        // The pair again, and the reason the absences above are not a property of an empty
        // database: FR-48 gives dispatch the roster with the telephone number on it.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var roster = await BodyAsync(world, "/api/clients", world.DispatcherToken, cancellationToken);

        Assert.Contains(AuthorizationWorld.ClientAPhone, roster, StringComparison.Ordinal);
        Assert.Contains(world.ClientA.Email, roster, StringComparison.OrdinalIgnoreCase);

        // The names too, so that all four of the driver-side absences are paired. A roster that
        // stopped carrying the client's name would otherwise turn two of those assertions into
        // searches for a string this product no longer writes anywhere.
        Assert.Contains(AuthorizationWorld.ClientALastName, roster, StringComparison.Ordinal);
        Assert.Contains(AuthorizationWorld.ClientAFirstName, roster, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three routes the two parties share. One list rather than two, because it is the same
    /// three routes from either side and the claim is symmetric: neither party learns the other.
    /// </summary>
    private static string[] PartyRoutes(AuthorizationWorld world) =>
    [
        "/api/deliveries/mine",
        $"/api/deliveries/{world.DeliveryId}/timeline",
        $"/api/deliveries/{world.DeliveryId}/proof",
    ];

    /// <summary>The raw response body of a read that must have succeeded.</summary>
    private static async Task<string> BodyAsync(
        AuthorizationWorld world,
        string path,
        string token,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            world.Client, HttpMethod.Get, path, token, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(body), path + " answered nothing at all.");

        return body;
    }
}
