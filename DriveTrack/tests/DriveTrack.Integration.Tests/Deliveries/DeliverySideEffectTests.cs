using System.Net;
using System.Text.Json;
using DriveTrack.Application.Deliveries;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Deliveries;

/// <summary>
/// AD-12, FR-28, FR-94 and FR-95 end to end: what a delivery operation leaves behind, and what it
/// does not make its caller wait for.
/// <para>
/// Only here. The claim is not "the runner writes an address" — that would be a claim about a
/// class — it is that a dispatcher's create returns with the address still absent, that the address
/// is there afterwards, and that neither a dead geocoder nor a dead mail relay can reach back and
/// change what the caller was told. That sentence spans an HTTP response, a background worker, a
/// second unit of work and a real transaction, and there is nowhere below this to assert it.
/// </para>
/// <para>
/// The two ports are fakes, registered last (see <see cref="OutboundPorts"/>). Everything between
/// them — the queue, the worker, the runner, the second unit of work, the real schema — is the
/// production wiring the container runs.
/// </para>
/// </summary>
public class DeliverySideEffectTests(PostgresFixture postgres)
{
    /// <summary>The pickup <c>DeliveryApi.NewDelivery</c> sends, so a test can name what was asked.</summary>
    private static readonly (double Latitude, double Longitude) Pickup = (50.4501, 30.5234);

    /// <inheritdoc cref="Pickup" />
    private static readonly (double Latitude, double Longitude) Dropoff = (49.8397, 24.0297);

    // =====================================================================================
    // FR-94 / FR-95: the address is filled in later, or left absent
    // =====================================================================================

    [Fact]
    public async Task A_created_delivery_answers_without_an_address_and_carries_one_afterwards()
    {
        // The first row of the matrix, and the half of it that is easy to lose: the response the
        // dispatcher received names no address at all. An implementation that awaited the geocoder
        // before returning would satisfy every later assertion here and break FR-95's promise.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Address = "Київ, Хрещатик, 1" };
        var sender = new FakeEmailSender();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(geocoder, sender));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        int deliveryId;

        using (var response = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/deliveries",
                   dispatcher,
                   DeliveryApi.NewDelivery(),
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var created = await FleetApi.DataAsync(response, cancellationToken);

            deliveryId = created.GetProperty("id").GetInt32();

            Assert.Equal(JsonValueKind.Null, created.GetProperty("pickup").GetProperty("address").ValueKind);
            Assert.Equal(JsonValueKind.Null, created.GetProperty("dropoff").GetProperty("address").ValueKind);
        }

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        var delivery = await ReadAsync(client, dispatcher, deliveryId, cancellationToken);

        Assert.Equal("Київ, Хрещатик, 1", Address(delivery, "pickup"));
        Assert.Equal("Київ, Хрещатик, 1", Address(delivery, "dropoff"));

        // The instant the matrix names beside the address, and the half of that row nothing else
        // here would notice the loss of. Read from storage rather than from the response: LocationView
        // carries the address alone, so the column is invisible over HTTP and a runner that stopped
        // stamping it would look identical from outside. AD-13: the injected clock, at offset zero.
        var stored = await StoredAsync(factory, deliveryId, cancellationToken);

        Assert.NotNull(stored.PickupLocation.AddressResolvedAt);
        Assert.NotNull(stored.DropoffLocation.AddressResolvedAt);

        Assert.Equal(TimeSpan.Zero, stored.PickupLocation.AddressResolvedAt.Value.Offset);
        Assert.Equal(TimeSpan.Zero, stored.DropoffLocation.AddressResolvedAt.Value.Offset);

        // Both points, and only both: a create has two unresolved locations and nothing else.
        Assert.Equal(new[] { Pickup, Dropoff }, geocoder.Described);
    }

    [Fact]
    public async Task A_geocoder_that_throws_leaves_the_delivery_exactly_as_it_was()
    {
        // The second row. "Swallowed inside the runner" has to mean the delivery survives the
        // failure untouched - not that it is written with a half-filled location, and not that the
        // create the dispatcher already got a 200 for is somehow undone.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder
        {
            Failure = new HttpRequestException("The geocoding service could not be reached."),
        };

        var sender = new FakeEmailSender();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(geocoder, sender));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        var delivery = await ReadAsync(client, dispatcher, deliveryId, cancellationToken);

        Assert.Null(Address(delivery, "pickup"));
        Assert.Null(Address(delivery, "dropoff"));

        // The coordinates are untouched, which is DR-11's actual rule: they are the record, and the
        // address is a cache that may simply be missing.
        Assert.Equal(
            Pickup.Latitude,
            delivery.GetProperty("pickup").GetProperty("point").GetProperty("latitude").GetDouble());

        // FR-28's other half, stated as an absence: a failed geocode is not a notification, so no
        // attempt row exists to explain it. The address being absent is the whole record of it.
        Assert.Empty(await AttemptsAsync(factory, cancellationToken));
    }

    [Fact]
    public async Task A_geocoder_that_finds_nothing_leaves_the_address_null()
    {
        // The third row, and a different thing from the failure above: the provider answered, and
        // its answer was that it has no address for that point. Both end with a null column, and it
        // matters that neither ends with a row saying something went wrong, because nothing did.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Address = null };
        var sender = new FakeEmailSender();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(geocoder, sender));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        var delivery = await ReadAsync(client, dispatcher, deliveryId, cancellationToken);

        Assert.Null(Address(delivery, "pickup"));
        Assert.Null(Address(delivery, "dropoff"));

        // It was asked, which is what separates this from a delivery nothing ever looked at.
        Assert.Equal(new[] { Pickup, Dropoff }, geocoder.Described);
    }

    [Fact]
    public async Task An_edit_re_requests_the_point_that_moved_and_only_that_one()
    {
        // The fourth row, and the one that pays for DR-11's "compare rather than always reset".
        // Clearing both addresses on every edit would pass every other assertion in this file while
        // doubling the traffic a public geocoder sees for an edit to the weight.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Address = "Київ, Хрещатик, 1" };
        var sender = new FakeEmailSender();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(geocoder, sender));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        Assert.Equal(2, geocoder.Described.Count);

        geocoder.Address = "Львів, площа Ринок, 1";

        var moved = (Latitude: 50.5000, Longitude: 30.6000);

        using (var response = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/deliveries/{deliveryId}",
                   dispatcher,
                   new
                   {
                       pickup = new { latitude = moved.Latitude, longitude = moved.Longitude },
                       dropoff = new { latitude = Dropoff.Latitude, longitude = Dropoff.Longitude },
                       packageDetails = "Одна палета",
                       packageWeightKg = 12.5m,
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        var delivery = await ReadAsync(client, dispatcher, deliveryId, cancellationToken);

        Assert.Equal("Львів, площа Ринок, 1", Address(delivery, "pickup"));

        // The dropoff kept the address it already had, which is the half a blanket reset would lose.
        Assert.Equal("Київ, Хрещатик, 1", Address(delivery, "dropoff"));

        // One more lookup, for the moved point. Three in total, and the third is the new pickup.
        Assert.Equal(3, geocoder.Described.Count);
        Assert.Equal(moved, geocoder.Described[2]);
    }

    [Fact]
    public async Task One_point_the_geocoder_cannot_place_does_not_discard_the_other()
    {
        // The runner looks the two points up separately and contains each on its own. With a fake
        // that fails everything, a runner that abandoned both points on the first exception would
        // be indistinguishable from one that did not - so this fails exactly one of them.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder
        {
            Address = "Київ, Хрещатик, 1",
            FailsFor = (latitude, longitude) =>
                (latitude, longitude) == (Dropoff.Latitude, Dropoff.Longitude),
        };

        var sender = new FakeEmailSender();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(geocoder, sender));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        var delivery = await ReadAsync(client, dispatcher, deliveryId, cancellationToken);

        // The pickup's answer survived the dropoff's failure, and the dropoff is absent rather than
        // wrong - DR-11's two legitimate states, one per point.
        Assert.Equal("Київ, Хрещатик, 1", Address(delivery, "pickup"));
        Assert.Null(Address(delivery, "dropoff"));
    }

    [Fact]
    public async Task A_point_moved_while_its_lookup_was_in_flight_keeps_no_address()
    {
        // The guard that only a race can reach: the write scope reloads, and refuses an address
        // whose coordinates have changed since it was asked for. Without the reload the old point's
        // street would be stored beside the new coordinates, which is the one pairing DR-11 forbids
        // and the one the screen presents as authoritative.
        var cancellationToken = TestContext.Current.CancellationToken;

        var gate = new TaskCompletionSource();

        var geocoder = new FakeGeocoder { Address = "Київ, Хрещатик, 1", Gate = gate };
        var sender = new FakeEmailSender();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(geocoder, sender));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        // Held inside the geocoder with the original point's answer already in hand. This wait is
        // what makes the race happen rather than be hoped for.
        await geocoder.Arrived.Task.WaitAsync(cancellationToken);

        // What the geocoder will say about wherever the point ends up. Different from the answer the
        // held job is carrying, so the two are told apart in the assertion below.
        geocoder.Address = "Львів, площа Ринок, 1";

        var moved = (Latitude: 50.5000, Longitude: 30.6000);

        using (var response = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/deliveries/{deliveryId}",
                   dispatcher,
                   new
                   {
                       pickup = new { latitude = moved.Latitude, longitude = moved.Longitude },
                       dropoff = new { latitude = Dropoff.Latitude, longitude = Dropoff.Longitude },
                       packageDetails = "Одна палета",
                       packageWeightKg = 12.5m,
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        gate.SetResult();

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        var delivery = await ReadAsync(client, dispatcher, deliveryId, cancellationToken);

        // The coordinates the dispatcher chose, described by the lookup that asked about *them*.
        // Drop the runner's coordinate comparison and this is "Київ, Хрещатик, 1" instead: the held
        // job writes the old point's street against the new coordinates, and the edit's own job then
        // finds an address already there and leaves it - which is exactly the pairing DR-11 forbids.
        var point = delivery.GetProperty("pickup").GetProperty("point");

        Assert.Equal(moved.Latitude, point.GetProperty("latitude").GetDouble(), 6);
        Assert.Equal(moved.Longitude, point.GetProperty("longitude").GetDouble(), 6);

        Assert.Equal("Львів, площа Ринок, 1", Address(delivery, "pickup"));

        Assert.Contains(moved, geocoder.Described);
    }

    [Fact]
    public async Task A_job_that_fails_outright_does_not_stop_the_ones_behind_it()
    {
        // The worker's own promise, and the one nothing else here can reach: the runner contains the
        // two failures it exists for, so no fake port can produce a job that throws out of it. What
        // does is a storage failure - a scope that would not open, a commit that failed - and if that
        // ended the loop, every later delivery in the process would silently stop being geocoded and
        // notified while every write its callers waited for still succeeded. One lost job, not all.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Address = "Київ, Хрещатик, 1" };
        var sender = new FakeEmailSender();
        var runner = new FailFirstRunner();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            services =>
            {
                OutboundPorts.Replace(geocoder, sender)(services);

                // Last, so it wins the resolve over the real runner it wraps.
                services.AddSingleton<IDeliverySideEffectRunner>(provider =>
                    runner.Wrapping(ActivatorUtilities.CreateInstance<DeliverySideEffectRunner>(provider)));
            });

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        // The first delivery's job is the one that throws.
        await DeliveryApi.PostAsync(client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        Assert.Equal(1, runner.Failed);

        // The second is queued after the loop has already met an exception it did not expect.
        var second = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        var delivery = await ReadAsync(client, dispatcher, second, cancellationToken);

        // Still running. Remove the worker's catch and this is null: the reader died with the first
        // job and the second one is sitting in a channel nothing reads.
        Assert.Equal("Київ, Хрещатик, 1", Address(delivery, "pickup"));
    }

    /// <summary>
    /// A runner that throws the first job it is handed and delegates every one after it.
    /// </summary>
    /// <remarks>
    /// Stands in for the storage failures the real runner deliberately does not absorb. Nothing a
    /// fake port can do reaches the worker's catch, because the runner catches a dead geocoder and a
    /// dead relay itself — those are the failures it exists to answer for.
    /// </remarks>
    private sealed class FailFirstRunner : IDeliverySideEffectRunner
    {
        private IDeliverySideEffectRunner? _inner;
        private int _failed;

        /// <summary>How many jobs were thrown out of, which should be exactly one.</summary>
        public int Failed => Volatile.Read(ref _failed);

        /// <summary>Takes the real runner to delegate to, and answers itself.</summary>
        /// <param name="inner">The production runner.</param>
        public IDeliverySideEffectRunner Wrapping(IDeliverySideEffectRunner inner)
        {
            _inner = inner;

            return this;
        }

        /// <inheritdoc />
        public Task RunAsync(DeliverySideEffectJob job, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
            {
                throw new InvalidOperationException("This job could not be performed.");
            }

            return _inner!.RunAsync(job, cancellationToken);
        }
    }

    // =====================================================================================
    // FR-28: the client is told, and the attempt is recorded either way
    // =====================================================================================

    [Fact]
    public async Task A_status_change_emails_the_client_and_records_the_attempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder();
        var sender = new FakeEmailSender();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(geocoder, sender));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var customer = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(clientId: customer.ClientId),
            cancellationToken);

        using (var response = await DeliveryApi.ChangeStatusAsync(
                   client, dispatcher, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        var message = Assert.Single(sender.Sent);

        // The address on the Identity account, which is the only place a client's address lives.
        Assert.Equal(customer.Email, message.Recipient);

        // NFR-14 reaches the mail too: the subject and the body are Ukrainian, and the two status
        // labels in the body are the ones the screens use.
        Assert.Contains("У дорозі", message.Body, StringComparison.Ordinal);
        Assert.Contains("Очікує", message.Body, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(message.Subject));

        var attempt = Assert.Single(await AttemptsAsync(factory, cancellationToken));

        Assert.Equal(deliveryId, attempt.DeliveryId);
        Assert.Equal(NotificationKind.StatusChange, attempt.Kind);
        Assert.Equal(customer.Email, attempt.Recipient);
        Assert.Equal(NotificationOutcome.Sent, attempt.Outcome);
        Assert.Null(attempt.Error);

        // AD-13: stored at offset zero, from the injected clock.
        Assert.Equal(TimeSpan.Zero, attempt.AttemptedAt.Offset);
    }

    [Fact]
    public async Task A_send_that_throws_is_recorded_and_the_status_change_still_stands()
    {
        // The row this story exists for. The original swallowed a failed notification silently; the
        // remediation is that the write survives untouched *and* that the failure leaves a record.
        var cancellationToken = TestContext.Current.CancellationToken;

        // Comfortably past the column's thousand characters, so the truncation is exercised rather
        // than assumed - an untruncated message would fail the insert and lose the very row FR-28
        // exists to keep.
        var reason = new string('я', 1500);

        var geocoder = new FakeGeocoder();
        var sender = new FakeEmailSender { Failure = new InvalidOperationException(reason) };

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(geocoder, sender));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var customer = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(clientId: customer.ClientId),
            cancellationToken);

        using (var response = await DeliveryApi.ChangeStatusAsync(
                   client, dispatcher, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            // The caller's answer is unchanged: the send had not been attempted when this was
            // written, and could not have changed it if it had.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        // The status change is committed and readable.
        Assert.Equal(
            nameof(DeliveryStatus.InTransit),
            await DeliveryApi.StatusAsync(client, dispatcher, deliveryId, cancellationToken));

        Assert.Empty(sender.Sent);

        var attempt = Assert.Single(await AttemptsAsync(factory, cancellationToken));

        Assert.Equal(NotificationOutcome.Failed, attempt.Outcome);
        Assert.Equal(customer.Email, attempt.Recipient);
        Assert.NotNull(attempt.Error);
        Assert.Equal(1000, attempt.Error!.Length);
        Assert.StartsWith("яяя", attempt.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_delivery_with_no_client_sends_nothing_and_records_nothing()
    {
        // FR-16 makes a delivery with no client legal, and FR-28 has nobody to write to. "No row"
        // has to mean "nothing was owed" here, which is why the sent case above writes one.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder();
        var sender = new FakeEmailSender();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(geocoder, sender));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using (var response = await DeliveryApi.ChangeStatusAsync(
                   client, dispatcher, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        Assert.Empty(sender.Sent);
        Assert.Empty(await AttemptsAsync(factory, cancellationToken));
    }

    [Fact]
    public async Task A_driver_advancing_their_own_delivery_notifies_exactly_as_dispatch_does()
    {
        // FR-34 and FR-28 meeting: the lifecycle has one path and every role walks it, so the
        // notification cannot depend on who moved the parcel. A role branch anywhere in the
        // dispatch seam would show up here and nowhere else.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder();
        var sender = new FakeEmailSender();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(geocoder, sender));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var customer = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId, clientId: customer.ClientId),
            cancellationToken);

        using (var response = await DeliveryApi.ChangeStatusAsync(
                   client, driver.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        var message = Assert.Single(sender.Sent);

        Assert.Equal(customer.Email, message.Recipient);

        var attempt = Assert.Single(await AttemptsAsync(factory, cancellationToken));

        Assert.Equal(NotificationOutcome.Sent, attempt.Outcome);
        Assert.Equal(customer.Email, attempt.Recipient);
    }

    /// <summary>The attempt rows, read through the port the admin screen reads them through.</summary>
    private static async Task<IReadOnlyList<NotificationAttempt>> AttemptsAsync(
        ApiFactory factory,
        CancellationToken cancellationToken)
    {
        await using var unitOfWork = await factory.Database.UnitOfWorkFactory
            .CreateAsync(cancellationToken);

        return await unitOfWork.NotificationAttempts.ListAsync(cancellationToken);
    }

    /// <summary>One delivery, as dispatch reads it.</summary>
    private static async Task<JsonElement> ReadAsync(
        HttpClient client,
        string dispatcherToken,
        int deliveryId,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client,
            HttpMethod.Get,
            $"/api/deliveries/{deliveryId}",
            dispatcherToken,
            body: null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await FleetApi.DataAsync(response, cancellationToken);
    }

    private static string? Address(JsonElement delivery, string point) =>
        delivery.GetProperty(point).GetProperty("address").GetString();

    /// <summary>One delivery as it was stored, for the columns the response does not carry.</summary>
    private static async Task<Delivery> StoredAsync(
        ApiFactory factory,
        int deliveryId,
        CancellationToken cancellationToken)
    {
        await using var unitOfWork = await factory.Database.UnitOfWorkFactory
            .CreateAsync(cancellationToken);

        var delivery = await unitOfWork.Deliveries.GetByIdAsync(deliveryId, cancellationToken);

        return Assert.IsType<Delivery>(delivery);
    }
}
