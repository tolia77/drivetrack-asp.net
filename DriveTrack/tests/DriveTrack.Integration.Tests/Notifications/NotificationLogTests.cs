using System.Net;
using DriveTrack.Application.Common;
using DriveTrack.Application.Notifications;
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
    /// <summary>The page size every assertion here is derived from, so none of them can drift.</summary>
    private const int Maximum = ListNotificationAttemptsQueryValidator.MaximumLimit;

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
    public async Task The_log_answers_one_page_and_the_offset_reaches_the_second()
    {
        // NFR-27 over the table that most needed it: it grows by a row per status change per
        // delivery and nothing prunes it, so an unbounded read is the whole history materialised
        // every time the screen is opened. A hundred and fifty rows is one full page and half of
        // another, which is what makes both halves of the claim assertable.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(new FakeGeocoder(), new FakeEmailSender()));

        using var client = factory.CreateClient();

        // Seeded through the context rather than over HTTP: a page and a half of status changes,
        // each drained through the real worker, would make this a test of the runner's throughput.
        // What is under test is where the OFFSET and the LIMIT go.
        var written = await SeedAttemptsAsync(factory, Maximum + (Maximum / 2), cancellationToken);

        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        // No query string at all: the omitted parameters take the controller's defaults rather than
        // binding to zero, so this is the first full page and not an empty one.
        var first = await IdsAsync(client, admin, query: string.Empty, cancellationToken);

        Assert.Equal(Enumerable.Reverse(written).Take(Maximum), first);

        var second = await IdsAsync(
            client, admin, $"?offset={Maximum}&limit={Maximum}", cancellationToken);

        Assert.Equal(Enumerable.Reverse(written).Skip(Maximum), second);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(null, Maximum + 1)]
    [InlineData(-1, null)]
    public async Task A_page_outside_the_bounds_is_refused_before_any_query(int? offset, int? limit)
    {
        // NFR-27: validated rather than passed through. A limit of zero is a refusal, not "give me
        // nothing", which is also why the controller's parameters are nullable - and why each case
        // sends only the parameter it is about, leaving the other one omitted.
        //
        // The over-limit case is Maximum + 1 rather than a literal: a hard-coded "101" would quietly
        // become a valid page the day the maximum grew, and this theory would go on passing while
        // asserting nothing.
        var cancellationToken = TestContext.Current.CancellationToken;

        var query = string.Concat(
            offset is { } skip ? $"offset={skip}" : string.Empty,
            offset is not null && limit is not null ? "&" : string.Empty,
            limit is { } take ? $"limit={take}" : string.Empty);

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(new FakeGeocoder(), new FakeEmailSender()));

        using var client = factory.CreateClient();

        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        using var refused = await ReadLogAsync(client, admin, cancellationToken, "?" + query);

        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.UnprocessableEntity,
            ErrorCode.COMMON_VALIDATION_FAILED,
            cancellationToken);
    }

    [Fact]
    public async Task A_dispatcher_is_refused_whatever_page_they_ask_for()
    {
        // The guard runs before the validator, so a caller who may not read the log is told that
        // rather than told their paging is wrong - including when their paging *is* wrong, which is
        // the case that would otherwise leak "this route exists and your role got past it".
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(new FakeGeocoder(), new FakeEmailSender()));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        foreach (var query in new[] { string.Empty, $"?offset=0&limit={Maximum}", "?limit=0" })
        {
            using var response = await ReadLogAsync(client, dispatcher, cancellationToken, query);

            await FleetApi.AssertFailureAsync(
                response,
                HttpStatusCode.Forbidden,
                ErrorCode.AUTH_FORBIDDEN,
                cancellationToken);
        }
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
            client,
            token,
            deliveryId,
            status,
            cancellationToken,

            // FR-120: Delivered needs a proof or a note, and this suite is about what the notice
            // records rather than about how a delivery is closed. The note is the arm that needs no
            // object store.
            note: status == DeliveryStatus.Delivered ? "Передано отримувачу" : null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> ReadLogAsync(
        HttpClient client,
        string? token,
        CancellationToken cancellationToken,
        string query = "") =>
        FleetApi.SendAsync(
            client,
            HttpMethod.Get,
            "/api/notifications" + query,
            token,
            body: null,
            cancellationToken);

    /// <summary>The ids on one page of the log, in the order the route answered them.</summary>
    private static async Task<int[]> IdsAsync(
        HttpClient client,
        string token,
        string query,
        CancellationToken cancellationToken)
    {
        using var response = await ReadLogAsync(client, token, cancellationToken, query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return
        [
            .. (await FleetApi.DataAsync(response, cancellationToken))
                .EnumerateArray()
                .Select(row => row.GetProperty("id").GetInt32()),
        ];
    }

    /// <summary>
    /// Saves <paramref name="count"/> attempts against one delivery and answers their row ids in
    /// the order they were written — which is the reverse of the order the log reads them back in.
    /// </summary>
    private static async Task<IReadOnlyList<int>> SeedAttemptsAsync(
        ApiFactory factory,
        int count,
        CancellationToken cancellationToken)
    {
        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        var delivery = Seed.NewDelivery();

        context.Deliveries.Add(delivery);

        // One round trip: the id the attempts need is assigned by this save.
        await context.SaveChangesAsync(cancellationToken);

        // A minute apart, so the timestamp and the id agree on the order and neither one alone is
        // carrying the assertion. The tie-break has its own test above, on rows sharing an instant.
        var start = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

        var attempts = Enumerable
            .Range(0, count)
            .Select(index => new NotificationAttempt
            {
                DeliveryId = delivery.Id,
                Kind = NotificationKind.StatusChange,
                Recipient = $"client{index}@drivetrack.test",
                AttemptedAt = start.AddMinutes(index),
                Outcome = NotificationOutcome.Sent,
            })
            .ToArray();

        context.NotificationAttempts.AddRange(attempts);
        await context.SaveChangesAsync(cancellationToken);

        return [.. attempts.Select(attempt => attempt.Id)];
    }
}
