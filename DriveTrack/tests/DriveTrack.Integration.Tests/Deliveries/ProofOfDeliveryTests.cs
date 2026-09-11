using System.Net;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace DriveTrack.Integration.Tests.Deliveries;

/// <summary>
/// FR-119 to FR-123 over HTTP, against the real <c>Program.cs</c> pipeline and a real PostgreSQL,
/// with the object store answered inside the process.
/// <para>
/// Three halves of this story can only be asserted here. The first is AD-26's ordering: that a
/// capture the guard refuses leaves the store untouched, and that a store which throws leaves no
/// proof row — claims about what happens on either side of the port, which is what the fake exists
/// to make visible. The second is FR-120's precondition, which has to hold identically for an
/// administrator, a dispatcher and the assigned driver, because the whole point of putting it at one
/// site was that it could not be role-branched. The third is FR-122's disclosure matrix: who is told
/// the capturer's name is decided in the capability's mapping step against the caller, so the claim
/// is about what a particular signed-in caller receives on the wire.
/// </para>
/// </summary>
public class ProofOfDeliveryTests(PostgresFixture postgres)
{
    // =====================================================================================
    // Capturing
    // =====================================================================================

    [Fact]
    public async Task A_capture_stores_every_asset_and_answers_the_proof()
    {
        // The matrix's first row, end to end: the assets reach the store, one proof row and its
        // assets commit together, and the view that comes back describes both.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId),
            cancellationToken);

        using (var captured = await ProofApi.CaptureAsync(
                   client, driver.Token, deliveryId, cancellationToken, signature: ProofApi.Png))
        {
            Assert.Equal(HttpStatusCode.OK, captured.StatusCode);

            var view = await FleetApi.DataAsync(captured, cancellationToken);

            Assert.Equal(deliveryId, view.GetProperty("deliveryId").GetInt32());
            Assert.Equal("Олена Петренко", view.GetProperty("recipientName").GetString());
            Assert.Equal(50.4501, view.GetProperty("captureLocation")
                .GetProperty("point").GetProperty("latitude").GetDouble());

            var assets = view.GetProperty("assets").EnumerateArray().ToArray();

            // Exactly two, and the signature first: one signature and one photograph went up, and a
            // collection binder that swept every file into the photograph list would produce three.
            Assert.Equal(2, assets.Length);
            Assert.Equal(nameof(ProofAssetKind.Signature), assets[0].GetProperty("kind").GetString());
            Assert.Equal("image/png", assets[0].GetProperty("contentType").GetString());
            Assert.Equal(nameof(ProofAssetKind.Photo), assets[1].GetProperty("kind").GetString());
        }

        // The bytes really arrived, and they are the bytes that were sent - not a truncated,
        // re-encoded or empty stand-in for them.
        Assert.Equal(2, store.Saved.Count);
        Assert.Equal(ProofApi.Png, store.Saved[0].Content);
        Assert.Equal("image/png", store.Saved[0].ContentType);
    }

    [Fact]
    public async Task Every_committed_storage_key_resolves_in_the_store()
    {
        // AD-26's guarantee, read off both sides at once: the keys the database holds are exactly
        // the keys the store was asked to mint, so a committed proof can always be shown. The
        // inverse - an object nothing references - is garbage and deliberately not an error.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId),
            cancellationToken);

        await ProofApi.CaptureOkAsync(client, driver.Token, deliveryId, cancellationToken);

        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        var stored = await context.ProofOfDeliveries
            .AsNoTracking()
            .Include(proof => proof.Assets)
            .SingleAsync(proof => proof.DeliveryId == deliveryId, cancellationToken);

        var keys = stored.Assets.Select(asset => asset.StorageKey).Order(StringComparer.Ordinal);

        Assert.Equal(
            store.Saved.Select(saved => saved.Key).Order(StringComparer.Ordinal),
            keys);

        // And the column holds a key rather than anything a browser could follow (DR-14).
        Assert.All(stored.Assets, asset =>
        {
            Assert.DoesNotContain("://", asset.StorageKey, StringComparison.Ordinal);
            Assert.DoesNotContain("/proof-assets", asset.StorageKey, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_dispatcher_may_capture_and_is_told_who_did()
    {
        // The permissive half of RequireAssignedDriver, which nothing else here reaches: every other
        // capture in this suite is the assigned driver's, so "or dispatch" was asserted only by its
        // refusals. FR-119 does not reserve the capture to a driver - it reserves it to whoever the
        // status change is reserved to (FR-34) - and dispatch closing a delivery from the board with
        // the evidence in hand is the case that says so.
        //
        // It is also the only path in the product that both writes a proof and answers a non-null
        // capturedByName, because AD-17 discloses the capturer to dispatch alone: a driver capturing
        // their own proof is told nothing about themselves. So the mapping step's populated arm runs
        // here and nowhere else.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using var captured = await ProofApi.CaptureAsync(
            client, dispatcher, deliveryId, cancellationToken, signature: ProofApi.Png);

        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);

        var view = await FleetApi.DataAsync(captured, cancellationToken);

        // FleetApi.TokenAsync creates a dispatcher called Диспетчер Тестовий, and the capture's own
        // response is what carries the name - not a second read afterwards, which would prove only
        // that GetAsync resolves one.
        Assert.Equal("Диспетчер Тестовий", view.GetProperty("capturedByName").GetString());
        Assert.Equal(2, store.Saved.Count);
    }

    [Fact]
    public async Task A_capture_on_a_delivered_parcel_is_allowed()
    {
        // FR-121. The parcel arrived before anyone had a chance to photograph it, and refusing the
        // evidence afterwards would leave the gap this capability exists to close.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId),
            cancellationToken);

        await MoveAsync(client, driver.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken);

        // Closed on the note arm of FR-120, which is exactly the case FR-121 is about: there was no
        // proof at the time.
        await MoveAsync(
            client,
            driver.Token,
            deliveryId,
            DeliveryStatus.Delivered,
            cancellationToken,
            note: "Передано, знімок додам пізніше");

        await ProofApi.CaptureOkAsync(client, driver.Token, deliveryId, cancellationToken);

        Assert.Equal(2, store.Saved.Count);
    }

    [Fact]
    public async Task A_second_capture_is_refused_and_the_first_is_untouched()
    {
        // FR-123: a proof is evidence, and evidence that can be replaced is not evidence. The claim
        // is both halves - refused, and the existing row unchanged - because a path that deleted
        // before it inserted would satisfy the first alone.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId),
            cancellationToken);

        await ProofApi.CaptureOkAsync(client, driver.Token, deliveryId, cancellationToken);

        var first = await ProofAsync(client, dispatcher, deliveryId, cancellationToken);

        using (var refused = await ProofApi.CaptureAsync(
                   client,
                   driver.Token,
                   deliveryId,
                   cancellationToken,
                   recipientName: "Хтось інший",
                   signature: ProofApi.Png))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Conflict,
                ErrorCode.DELIVERY_PROOF_ALREADY_CAPTURED,
                cancellationToken);
        }

        var second = await ProofAsync(client, dispatcher, deliveryId, cancellationToken);

        Assert.Equal("Олена Петренко", second.GetProperty("recipientName").GetString());
        Assert.Equal(
            first.GetProperty("capturedAt").GetString(),
            second.GetProperty("capturedAt").GetString());
    }

    // =====================================================================================
    // What is refused, and what the store saw
    // =====================================================================================

    [Fact]
    public async Task A_client_may_not_capture_and_nothing_reaches_the_store()
    {
        // The ordering claim AD-26 exists for, stated as an absence. Store first and guard second
        // would be one method shorter and would let any signed-in caller push bytes into the bucket
        // before being refused - and the refusal would look identical from the outside.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var customer = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(clientId: customer.ClientId),
            cancellationToken);

        using (var refused = await ProofApi.CaptureAsync(
                   client, customer.Token, deliveryId, cancellationToken, signature: ProofApi.Png))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Forbidden,
                ErrorCode.AUTH_FORBIDDEN,
                cancellationToken);
        }

        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task A_driver_who_is_not_carrying_the_parcel_is_refused_before_the_store()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var mine = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var theirs = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: mine.DriverId),
            cancellationToken);

        using (var refused = await ProofApi.CaptureAsync(
                   client, theirs.Token, deliveryId, cancellationToken, signature: ProofApi.Png))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Forbidden,
                ErrorCode.AUTH_FORBIDDEN,
                cancellationToken);
        }

        Assert.Empty(store.Saved);

        // And the assigned driver is allowed, so the refusal above is about who asked rather than
        // about the request being malformed.
        await ProofApi.CaptureOkAsync(client, mine.Token, deliveryId, cancellationToken);
    }

    [Fact]
    public async Task An_asset_of_a_type_a_proof_may_not_carry_is_refused_before_the_store()
    {
        // NFR-28, and "before" is the load-bearing half: the store is written before the
        // transaction opens, so an upload refused afterwards would be an object in the bucket that
        // no row will ever name.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var (driverToken, deliveryId) = await AssignedAsync(factory, client, cancellationToken);

        using (var refused = await ProofApi.CaptureAsync(
                   client,
                   driverToken,
                   deliveryId,
                   cancellationToken,
                   signature: ProofApi.Png,
                   photoContentType: "application/pdf"))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.UnprocessableContent,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);

            // The per-field explanation, which is where NFR-4 puts it: a driver is told the file was
            // the wrong sort rather than that something about the request was. Asserted as the
            // localized sentence, because that is what the envelope carries - the key is resolved
            // through the same catalogue error.code is (NFR-3).
            await AssertFieldMessageAsync(
                factory,
                refused,
                ErrorCode.DELIVERY_PROOF_ASSET_TYPE_NOT_ALLOWED,
                cancellationToken);
        }

        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task An_oversized_asset_is_refused_before_the_store()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var (driverToken, deliveryId) = await AssignedAsync(factory, client, cancellationToken);

        // One byte over the per-asset cap and comfortably under the request cap, so the refusal
        // comes from the validator with a contract code rather than from the framework with a 413
        // no localized catalogue has a message for.
        var oversized = new byte[DriveTrack.Application.Deliveries.ProofAssetRules.MaximumAssetBytes + 1];

        using (var refused = await ProofApi.CaptureAsync(
                   client,
                   driverToken,
                   deliveryId,
                   cancellationToken,
                   signature: ProofApi.Png,
                   photos: [oversized]))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.UnprocessableContent,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);

            await AssertFieldMessageAsync(
                factory,
                refused,
                ErrorCode.DELIVERY_PROOF_ASSET_TOO_LARGE,
                cancellationToken);
        }

        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task A_capture_missing_its_signature_or_its_photograph_is_refused()
    {
        // FR-119 reads as one sentence and is two refusals, because the two are different things to
        // fix for whoever is standing at a door.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var (driverToken, deliveryId) = await AssignedAsync(factory, client, cancellationToken);

        using (var noSignature = await ProofApi.CaptureAsync(
                   client, driverToken, deliveryId, cancellationToken, signature: null))
        {
            await FleetApi.AssertFailureAsync(
                noSignature,
                HttpStatusCode.UnprocessableContent,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);
        }

        using (var noPhoto = await ProofApi.CaptureAsync(
                   client,
                   driverToken,
                   deliveryId,
                   cancellationToken,
                   signature: ProofApi.Png,
                   photos: []))
        {
            await FleetApi.AssertFailureAsync(
                noPhoto,
                HttpStatusCode.UnprocessableContent,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);
        }

        Assert.Empty(store.Saved);
    }

    [Theory]
    [InlineData(null, 30.5234)]
    [InlineData(50.4501, null)]
    public async Task A_capture_missing_half_its_coordinates_is_refused_rather_than_placed_at_zero(
        double? latitude,
        double? longitude)
    {
        // FR-119 records where the hand-over happened, and 0 is a real point rather than an absent
        // one - so a form value that is missing must reach the validator as null and not as zero.
        // Asserted over HTTP because the decision lives in ProofCaptureForm.Coordinate, which only
        // the REST adapter runs: a capture that answered 200 here would have committed a proof
        // placed in the Atlantic or the Gulf of Guinea, which is worse than no proof at all.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var (driverToken, deliveryId) = await AssignedAsync(factory, client, cancellationToken);

        using var refused = await ProofApi.CaptureAsync(
            client,
            driverToken,
            deliveryId,
            cancellationToken,
            latitude: latitude,
            longitude: longitude,
            signature: ProofApi.Png);

        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.UnprocessableContent,
            ErrorCode.COMMON_VALIDATION_FAILED,
            cancellationToken);

        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task A_store_that_will_not_write_leaves_no_proof_behind()
    {
        // The last row of the matrix. The port's contract is that a failed save throws, and AD-26's
        // ordering is what makes that safe: the transaction that would have referenced the key was
        // never opened, so there is nothing to roll back and nothing half-written to find.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore
        {
            Failure = new InvalidOperationException("The object store is unreachable."),
        };

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId),
            cancellationToken);

        using (var failed = await ProofApi.CaptureAsync(
                   client, driver.Token, deliveryId, cancellationToken, signature: ProofApi.Png))
        {
            await FleetApi.AssertFailureAsync(
                failed,
                HttpStatusCode.InternalServerError,
                ErrorCode.COMMON_UNEXPECTED_ERROR,
                cancellationToken);
        }

        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        Assert.False(await context.ProofOfDeliveries
            .AsNoTracking()
            .AnyAsync(proof => proof.DeliveryId == deliveryId, cancellationToken));
    }

    // =====================================================================================
    // FR-120: the precondition on Delivered
    // =====================================================================================

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Driver)]
    public async Task Delivered_without_a_proof_or_a_note_is_refused_for_every_caller_alike(
        UserRole role)
    {
        // FR-120 at one site, and the reason it is one site: an administrator, a dispatcher and the
        // assigned driver reach ChangeStatusAsync through the same method, so a role branch in the
        // condition would be a second, unwritten lifecycle. Asserted for all three because that is
        // the only thing that can notice one appearing.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId),
            cancellationToken);

        await MoveAsync(client, dispatcher, deliveryId, DeliveryStatus.InTransit, cancellationToken);

        var before = await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken);

        var token = role switch
        {
            UserRole.Driver => driver.Token,
            UserRole.Dispatcher => dispatcher,
            _ => await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken),
        };

        // Blank rather than absent as well: a note of one space explains nothing, and it is what an
        // empty form field sends.
        foreach (var note in new string?[] { null, "   " })
        {
            using var refused = await DeliveryApi.ChangeStatusAsync(
                client, token, deliveryId, DeliveryStatus.Delivered, cancellationToken, note);

            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Conflict,
                ErrorCode.DELIVERY_PROOF_REQUIRED,
                cancellationToken);
        }

        // Refused, and refused before anything was written: the entry is staged ahead of the commit,
        // so a precondition raised after the move would leave a history saying the parcel arrived.
        Assert.Equal(
            nameof(DeliveryStatus.InTransit),
            await DeliveryApi.StatusAsync(client, dispatcher, deliveryId, cancellationToken));

        var after = await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken);

        Assert.Equal(before.Length, after.Length);
    }

    [Fact]
    public async Task Delivered_with_a_note_and_no_proof_succeeds_and_the_note_lands_on_the_entry()
    {
        // The second arm of FR-120, which is what dispatch has when there is nothing to photograph.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        await MoveAsync(client, dispatcher, deliveryId, DeliveryStatus.InTransit, cancellationToken);

        using var closed = await DeliveryApi.ChangeStatusAsync(
            client,
            dispatcher,
            deliveryId,
            DeliveryStatus.Delivered,
            cancellationToken,
            note: "Вручено особисто, фото немає");

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var entry = await FleetApi.DataAsync(closed, cancellationToken);

        Assert.Equal(nameof(DeliveryStatus.Delivered), entry.GetProperty("newStatus").GetString());
        Assert.Equal("Вручено особисто, фото немає", entry.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Delivered_with_a_proof_and_no_note_succeeds()
    {
        // The first arm, and the one the capability was built for: the evidence is the explanation.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId),
            cancellationToken);

        await MoveAsync(client, driver.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken);
        await ProofApi.CaptureOkAsync(client, driver.Token, deliveryId, cancellationToken);

        using var closed = await DeliveryApi.ChangeStatusAsync(
            client, driver.Token, deliveryId, DeliveryStatus.Delivered, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
    }

    // =====================================================================================
    // FR-122: who is told what
    // =====================================================================================

    [Fact]
    public async Task A_client_reads_the_proof_without_learning_who_captured_it()
    {
        // AD-17 reaching the evidence. A client learning the assigned driver's name through a proof
        // would be the disclosure FR-96 closes on the delivery itself, arriving by another door - so
        // the claim is not merely that the field is null, but that the driver's name appears nowhere
        // in the response at all.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var customer = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId, clientId: customer.ClientId),
            cancellationToken);

        await ProofApi.CaptureOkAsync(client, driver.Token, deliveryId, cancellationToken);

        using (var read = await ProofApi.ReadAsync(client, customer.Token, deliveryId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

            var body = await read.Content.ReadAsStringAsync(cancellationToken);
            var view = JsonDocument.Parse(body).RootElement.GetProperty("data");

            Assert.Equal(JsonValueKind.Null, view.GetProperty("capturedByName").ValueKind);

            // DeliveryApi.DriverAsync takes the driver on as Тарас Шевченко; neither half of that
            // name may be anywhere in what a client is handed.
            Assert.DoesNotContain("Шевченко", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Тарас", body, StringComparison.Ordinal);

            // And the rest of FR-122 is present, because "discloses nothing" must not be achieved by
            // answering nothing.
            Assert.Equal("Олена Петренко", view.GetProperty("recipientName").GetString());
            Assert.Equal(2, view.GetProperty("assets").GetArrayLength());
        }

        // The same proof, read by dispatch: the name is there. Asserting only the withholding would
        // pass for a capability that never resolved a name at all.
        var seen = await ProofAsync(client, dispatcher, deliveryId, cancellationToken);

        Assert.Equal("Тарас Шевченко", seen.GetProperty("capturedByName").GetString());
    }

    [Fact]
    public async Task The_assigned_driver_reads_the_proof_and_is_told_no_capturer()
    {
        // The fourth of FR-122's audiences, and the only one nothing else here reads the proof as.
        // Two claims, and the second is the one worth the test: a driver's scope admits the row, so
        // the read succeeds - and AD-17 still withholds the capturer, even though the driver is the
        // capturer. The disclosure rule is about the caller's role and not about whether they would
        // be learning anything new, which is what keeps it a rule rather than a judgement.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var other = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId),
            cancellationToken);

        await ProofApi.CaptureOkAsync(client, driver.Token, deliveryId, cancellationToken);

        using (var read = await ProofApi.ReadAsync(client, driver.Token, deliveryId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

            var view = await FleetApi.DataAsync(read, cancellationToken);

            Assert.Equal(JsonValueKind.Null, view.GetProperty("capturedByName").ValueKind);
            Assert.Equal("Олена Петренко", view.GetProperty("recipientName").GetString());
            Assert.Equal(2, view.GetProperty("assets").GetArrayLength());
        }

        // And another driver's scope does not admit it, so the read above is this driver's
        // assignment doing the work rather than the role.
        using var refused = await ProofApi.ReadAsync(client, other.Token, deliveryId, cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.NotFound,
            ErrorCode.COMMON_NOT_FOUND,
            cancellationToken);
    }

    [Fact]
    public async Task Another_clients_proof_is_not_found_rather_than_refused()
    {
        // The scope narrows in the query, so "not yours" and "no such delivery" are one answer - a
        // 403 here would confirm that somebody else's delivery exists.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new FakeAssetStore();

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(store));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var owner = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);
        var stranger = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId, clientId: owner.ClientId),
            cancellationToken);

        await ProofApi.CaptureOkAsync(client, driver.Token, deliveryId, cancellationToken);

        using var refused = await ProofApi.ReadAsync(
            client, stranger.Token, deliveryId, cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.NotFound,
            ErrorCode.COMMON_NOT_FOUND,
            cancellationToken);
    }

    [Fact]
    public async Task A_delivery_with_no_proof_yet_answers_the_same_not_found()
    {
        // One answer for "nothing captured" and for "not yours", which is what stops the read path
        // being a way to enumerate deliveries.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString, cancellationToken, OutboundPorts.Replace(new FakeAssetStore()));
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using var read = await ProofApi.ReadAsync(client, dispatcher, deliveryId, cancellationToken);

        await FleetApi.AssertFailureAsync(
            read,
            HttpStatusCode.NotFound,
            ErrorCode.COMMON_NOT_FOUND,
            cancellationToken);
    }

    /// <summary>
    /// Asserts that some field of a refused request carries the message <paramref name="code"/>
    /// resolves to, and that it is not the generic one.
    /// <para>
    /// The envelope carries the localized sentence rather than the key — the adapter resolves a
    /// field's message through the same catalogue <c>error.code</c> is resolved through (NFR-3) — so
    /// the claim is made in the same terms. The second half matters more than the first: a rule that
    /// forgot its own message key would still produce a 422 with a field name on it, and only the
    /// sentence shows which rule refused.
    /// </para>
    /// </summary>
    private static async Task AssertFieldMessageAsync(
        ApiFactory factory,
        HttpResponseMessage response,
        ErrorCode code,
        CancellationToken cancellationToken)
    {
        using var scope = factory.Services.CreateScope();

        var localizer = scope.ServiceProvider
            .GetRequiredService<IStringLocalizer<DriveTrack.Web.Resources.ErrorMessages>>();

        var envelope = await FleetApi.ReadAsync(response, cancellationToken);

        // Read as values rather than as the JSON text: the encoder escapes every Cyrillic character
        // to a \u sequence, so a substring assertion against the document would be comparing a
        // Ukrainian sentence with its escaped spelling and could only ever fail.
        var messages = envelope.GetProperty("error")
            .GetProperty("fields")
            .EnumerateObject()
            .SelectMany(field => field.Value.EnumerateArray())
            .Select(message => message.GetString())
            .ToArray();

        Assert.Contains(localizer[code.ToString()].Value, messages);

        Assert.DoesNotContain(localizer[nameof(ErrorCode.COMMON_VALIDATION_FAILED)].Value, messages);
    }

    /// <summary>A dispatcher, a driver with a delivery of their own, and that driver's token.</summary>
    private static async Task<(string Token, int DeliveryId)> AssignedAsync(
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

        return (driver.Token, deliveryId);
    }

    /// <summary>The proof of a delivery that must have one, as the caller reads it.</summary>
    private static async Task<JsonElement> ProofAsync(
        HttpClient client,
        string token,
        int deliveryId,
        CancellationToken cancellationToken)
    {
        using var response = await ProofApi.ReadAsync(client, token, deliveryId, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await FleetApi.DataAsync(response, cancellationToken);
    }

    private static async Task MoveAsync(
        HttpClient client,
        string token,
        int deliveryId,
        DeliveryStatus status,
        CancellationToken cancellationToken,
        string? note = null)
    {
        using var response = await DeliveryApi.ChangeStatusAsync(
            client, token, deliveryId, status, cancellationToken, note);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
