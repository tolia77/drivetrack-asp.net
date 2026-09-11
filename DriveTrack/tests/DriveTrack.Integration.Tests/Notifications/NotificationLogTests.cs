using System.Net;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Deliveries;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Notifications;

/// <summary>
/// FR-28's read side over HTTP: the log an administrator opens, and the three roles that cannot.
/// <para>
/// The rows are written the way the product writes them — two status changes through the real
/// endpoint, drained through the real worker — rather than inserted by hand. A hand-written row
/// would prove the query orders correctly and nothing about whether the system ever produces one.
/// </para>
/// </summary>
public class NotificationLogTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_administrator_reads_every_attempt_newest_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var sender = new FakeEmailSender();

        // Stopped, so both attempts are stamped with the same instant and "newest first" can only
        // come from the id. On the real clock the two rows are microseconds apart and the ordering
        // this test is named for would hold with the tie-break deleted.
        //
        // Stopped at now rather than at a written-out date: the host issues its bearer tokens
        // against this clock and the handler validates them against the real one, so a fixed instant
        // far from the present would expire every token before it was used.
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(new FakeGeocoder(), sender),
            clock);

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var customer = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(clientId: customer.ClientId),
            cancellationToken);

        // Two moves, so "newest first" is a claim with something to order. Drained between them,
        // because the point is the order the rows were written in and not the order two racing
        // workers happened to finish.
        await AdvanceAsync(client, dispatcher, deliveryId, DeliveryStatus.InTransit, cancellationToken);
        await OutboundPorts.DrainAsync(factory, cancellationToken);

        await AdvanceAsync(client, dispatcher, deliveryId, DeliveryStatus.Delivered, cancellationToken);
        await OutboundPorts.DrainAsync(factory, cancellationToken);

        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        using var response = await ReadLogAsync(client, admin, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = (await FleetApi.DataAsync(response, cancellationToken)).EnumerateArray().ToArray();

        Assert.Equal(2, rows.Length);

        // Newest first. Both rows carry the stopped clock's instant, so the timestamp orders
        // nothing here and the id tie-break is the only thing that can produce this order - which
        // is the point: delete it from the repository and this assertion fails.
        Assert.Equal(
            rows[0].GetProperty("attemptedAt").GetString(),
            rows[1].GetProperty("attemptedAt").GetString());

        Assert.True(rows[0].GetProperty("id").GetInt32() > rows[1].GetProperty("id").GetInt32());

        foreach (var row in rows)
        {
            Assert.Equal(deliveryId, row.GetProperty("deliveryId").GetInt32());
            Assert.Equal(customer.Email, row.GetProperty("recipient").GetString());

            // AD-21: both enums cross the wire as their member names.
            Assert.Equal(
                nameof(NotificationKind.StatusChange),
                row.GetProperty("kind").GetString());
            Assert.Equal(
                nameof(NotificationOutcome.Sent),
                row.GetProperty("outcome").GetString());
        }

        // Two notices actually left, which is what the two rows are a record of.
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task An_empty_log_is_an_empty_list_rather_than_a_refusal()
    {
        // A system that has notified nobody is the ordinary state of a fresh deployment, and the
        // screen has to be able to say so.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(new FakeGeocoder(), new FakeEmailSender()));

        using var client = factory.CreateClient();

        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        using var response = await ReadLogAsync(client, admin, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await FleetApi.DataAsync(response, cancellationToken)).EnumerateArray());
    }

    [Fact]
    public async Task Nobody_but_an_administrator_can_reach_the_log()
    {
        // FR-12: the navigation hides the link from three roles, and this is the decision the
        // hiding is a convenience for. A dispatcher reaches it by typing the address, and the guard
        // inside the service is what answers - not the missing menu entry.
        //
        // A dispatcher is the case worth naming: AD-4 makes an admin satisfy every check, so
        // RequireRole(Dispatcher) would have admitted them, and the service says
        // RequireRole(Admin) precisely so it does not.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(new FakeGeocoder(), new FakeEmailSender()));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var customer = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        foreach (var token in new[] { dispatcher, driver.Token, customer.Token })
        {
            using var response = await ReadLogAsync(client, token, cancellationToken);

            await FleetApi.AssertFailureAsync(
                response,
                HttpStatusCode.Forbidden,
                ErrorCode.AUTH_FORBIDDEN,
                cancellationToken);
        }
    }

    [Fact]
    public async Task An_anonymous_caller_is_told_to_sign_in_rather_than_refused()
    {
        // FR-13: no session and the wrong session are different failures, and the caller's next move
        // differs with them.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(new FakeGeocoder(), new FakeEmailSender()));

        using var client = factory.CreateClient();

        using var response = await ReadLogAsync(client, token: null, cancellationToken);

        await FleetApi.AssertFailureAsync(
            response,
            HttpStatusCode.Unauthorized,
            ErrorCode.AUTH_UNAUTHENTICATED,
            cancellationToken);
    }

    private static async Task AdvanceAsync(
        HttpClient client,
        string token,
        int deliveryId,
        DeliveryStatus status,
        CancellationToken cancellationToken)
    {
        using var response = await DeliveryApi.ChangeStatusAsync(
            client, token, deliveryId, status, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> ReadLogAsync(
        HttpClient client,
        string? token,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client,
            HttpMethod.Get,
            "/api/notifications",
            token,
            body: null,
            cancellationToken);
}
