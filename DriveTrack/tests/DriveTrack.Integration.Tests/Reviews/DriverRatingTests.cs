using System.Text.Json;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Deliveries;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;

namespace DriveTrack.Integration.Tests.Reviews;

/// <summary>
/// FR-98 and DR-18: the per-driver rating, derived on demand from the reviews and stored nowhere.
/// <para>
/// This is the original system's named defect. Ratings existed per review and were aggregated
/// nowhere, so there was no such thing as a driver's standing — and the fix that first suggests
/// itself, a column on <c>drivers</c>, is a second answer that goes stale the moment a review is
/// written, edited or deleted. So the assertions here are as much about the <em>absence</em> of a
/// stored figure as about the arithmetic: the aggregate moves with every write to the reviews, and
/// nobody had to remember to update anything.
/// </para>
/// <para>
/// Asserted over the driver roster because that is where a dispatcher reads it, and because the
/// roster is the boundary AD-24 draws: the Drivers capability asks the Reviews capability, which is
/// the only reader of <c>reviews</c>.
/// </para>
/// </summary>
public class DriverRatingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_drivers_rating_is_the_mean_of_every_review_of_their_deliveries()
    {
        // The matrix's arithmetic row: reviews of 5, 4 and 3 average to 4, which reads as
        // favourable - and the count travels with it, because an average of one review and an
        // average of fifty are different claims about the same number.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driverId = await ReviewedAsync(client, dispatcher, cancellationToken, 5, 4, 3);

        var row = await DriverAsync(client, dispatcher, driverId, cancellationToken);

        Assert.Equal(4d, row.GetProperty("rating").GetDouble());
        Assert.Equal(3, row.GetProperty("reviewCount").GetInt32());
    }

    [Fact]
    public async Task A_mean_that_is_not_a_whole_number_reaches_the_roster_unrounded()
    {
        // Every other arithmetic case here averages to a whole number, so rounding or truncating
        // the mean in the grouped query would ship green and the story's headline figure would be
        // pinned by nothing. Five and four is 4.5, and 4.5 is what /api/drivers has to say: the
        // aggregate is a number, and deciding how many of its digits a reader sees is the screen's
        // job rather than the query's.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driverId = await ReviewedAsync(client, dispatcher, cancellationToken, 5, 4);

        var row = await DriverAsync(client, dispatcher, driverId, cancellationToken);

        Assert.Equal(4.5d, row.GetProperty("rating").GetDouble());
        Assert.Equal(2, row.GetProperty("reviewCount").GetInt32());
    }

    [Fact]
    public async Task Every_way_of_reading_one_driver_carries_the_same_standing()
    {
        // The roster is not the only payload a DriverSummary leaves by. A single-driver read and
        // the answer to an edit are built by different methods, and either could have been left
        // filling the two fields with null and zero - the whole suite would stay green while a
        // dispatcher who opened one driver saw a rated one as unrated.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driverId = await ReviewedAsync(client, dispatcher, cancellationToken, 5, 4, 3);

        var roster = await DriverAsync(client, dispatcher, driverId, cancellationToken);

        using (var one = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/drivers/" + driverId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                   dispatcher,
                   body: null,
                   cancellationToken))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, one.StatusCode);

            var payload = await FleetApi.DataAsync(one, cancellationToken);

            Assert.Equal(roster.GetProperty("rating").GetDouble(), payload.GetProperty("rating").GetDouble());
            Assert.Equal(
                roster.GetProperty("reviewCount").GetInt32(),
                payload.GetProperty("reviewCount").GetInt32());
        }

        // A licence-only edit touches no review, so the payload it answers has to carry the
        // standing the driver already held - a null here would read as "nobody has reviewed them".
        using (var edited = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   "/api/drivers/" + driverId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                   dispatcher,
                   new { licenseNumber = "ВІ999888" },
                   cancellationToken))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, edited.StatusCode);

            var payload = await FleetApi.DataAsync(edited, cancellationToken);

            Assert.Equal("ВІ999888", payload.GetProperty("licenseNumber").GetString());
            Assert.Equal(4d, payload.GetProperty("rating").GetDouble());
            Assert.Equal(3, payload.GetProperty("reviewCount").GetInt32());
        }
    }

    [Fact]
    public async Task A_driver_nobody_has_reviewed_has_no_rating_rather_than_a_zero()
    {
        // The row the whole nullable field exists for. A zero is a rating - the worst one the scale
        // has - so mapping "nobody has said anything" onto it would sort an unrated driver below
        // every rated one and put a complaint nobody made in front of a dispatcher.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var row = await DriverAsync(client, dispatcher, driver.DriverId, cancellationToken);

        Assert.Equal(JsonValueKind.Null, row.GetProperty("rating").ValueKind);
        Assert.Equal(0, row.GetProperty("reviewCount").GetInt32());
    }

    [Fact]
    public async Task A_driver_who_carried_an_unreviewed_delivery_still_has_no_rating()
    {
        // The case a join written the other way round would get wrong: the driver has deliveries,
        // and a LEFT JOIN answering a row of nulls would become an average of nothing. A driver is
        // rated by the reviews that exist, not by the parcels they carried.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        var row = await DriverAsync(client, dispatcher, carried.Driver.DriverId, cancellationToken);

        Assert.Equal(JsonValueKind.Null, row.GetProperty("rating").ValueKind);
        Assert.Equal(0, row.GetProperty("reviewCount").GetInt32());
    }

    [Fact]
    public async Task One_drivers_reviews_do_not_reach_another_drivers_rating()
    {
        // The aggregate is grouped, and a grouping keyed wrongly - or dropped - would hand every
        // driver the whole table's average. Two drivers with opposite reviews is what notices.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var praised = await ReviewedAsync(client, dispatcher, cancellationToken, 5, 5);
        var criticized = await ReviewedAsync(client, dispatcher, cancellationToken, 1, 1);

        var first = await DriverAsync(client, dispatcher, praised, cancellationToken);
        var second = await DriverAsync(client, dispatcher, criticized, cancellationToken);

        Assert.Equal(5d, first.GetProperty("rating").GetDouble());
        Assert.Equal(2, first.GetProperty("reviewCount").GetInt32());

        Assert.Equal(1d, second.GetProperty("rating").GetDouble());
        Assert.Equal(2, second.GetProperty("reviewCount").GetInt32());
    }

    [Fact]
    public async Task The_rating_follows_the_reviews_rather_than_being_stored_beside_them()
    {
        // DR-18 as a behaviour rather than as a schema claim. The rating moves when a review is
        // edited and again when it is deleted, and nothing in the product updates a driver row -
        // which is the difference between "derived on demand" and "cached and hopefully refreshed".
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var admin = await FleetApi.TokenAsync(
            factory, client, UserRole.Admin, cancellationToken);

        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);
        var reviewId = await ReviewApi.WrittenAsync(
            client, carried.Client.Token, carried.DeliveryId, 1, cancellationToken);

        var afterWrite = await DriverAsync(client, dispatcher, carried.Driver.DriverId, cancellationToken);

        Assert.Equal(1d, afterWrite.GetProperty("rating").GetDouble());

        using (var edited = await ReviewApi.EditAsync(
                   client, carried.Client.Token, reviewId, 5, "передумав", cancellationToken))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, edited.StatusCode);
        }

        var afterEdit = await DriverAsync(client, dispatcher, carried.Driver.DriverId, cancellationToken);

        Assert.Equal(5d, afterEdit.GetProperty("rating").GetDouble());

        using (var deleted = await ReviewApi.DeleteAsync(client, admin, reviewId, cancellationToken))
        {
            Assert.Equal(System.Net.HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var afterDelete = await DriverAsync(client, dispatcher, carried.Driver.DriverId, cancellationToken);

        // Back to nothing, rather than back to a stale 5 or forward to a 0.
        Assert.Equal(JsonValueKind.Null, afterDelete.GetProperty("rating").ValueKind);
        Assert.Equal(0, afterDelete.GetProperty("reviewCount").GetInt32());
    }

    [Fact]
    public async Task Deleting_a_reviewed_delivery_takes_its_review_out_of_the_drivers_standing()
    {
        // The one path where the aggregate moves without anybody touching a review: FR-24's delete
        // cascades from Delivery to Review (DR-9), so the row simply stops existing. A stored
        // average would have gone stale here and nothing in the product would have been asked to
        // refresh it - which is the whole of DR-18's argument, and the case the two edit-and-delete
        // assertions above cannot make.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        // FR-24 reserves deletion to an administrator, so the delete goes through one.
        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        // One driver carrying two reviewed parcels, so deleting one leaves a standing to compare.
        var vehicleId = await DeliveryApi.VehicleAsync(client, dispatcher, 1_200m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId, cancellationToken);

        var kept = await ReviewedDeliveryAsync(client, dispatcher, driver, 5, cancellationToken);
        var removed = await ReviewedDeliveryAsync(client, dispatcher, driver, 1, cancellationToken);

        var before = await DriverAsync(client, dispatcher, driver.DriverId, cancellationToken);

        Assert.Equal(3d, before.GetProperty("rating").GetDouble());
        Assert.Equal(2, before.GetProperty("reviewCount").GetInt32());

        using (var deleted = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Delete,
                   "/api/deliveries/" + removed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                   admin,
                   body: null,
                   cancellationToken))
        {
            // 200 rather than 204: the delivery route answers the envelope with a null payload,
            // which is that controller's shape and not this test's business to change.
            Assert.Equal(System.Net.HttpStatusCode.OK, deleted.StatusCode);
        }

        var after = await DriverAsync(client, dispatcher, driver.DriverId, cancellationToken);

        Assert.Equal(5d, after.GetProperty("rating").GetDouble());
        Assert.Equal(1, after.GetProperty("reviewCount").GetInt32());

        // And the review really is gone rather than merely uncounted: the cascade removed the row.
        var remaining = await ReviewApi.RowsAsync(
            await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken);

        Assert.Equal(kept, Assert.Single(remaining).GetProperty("deliveryId").GetInt32());
    }

    [Fact]
    public async Task Deleting_a_driver_takes_their_reviews_out_of_every_standing()
    {
        // The remaining way the aggregate moves without anybody touching a review, and the one the
        // delivery-delete twin above cannot make: FR-39 leaves a deleted driver's deliveries
        // unassigned rather than deleting them, so the review survives its driver. Delivery's
        // SetNull cascade is therefore the only path where a review that still exists stops
        // counting towards anybody - and a stored average would have been left on a row that no
        // longer exists.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var vehicleId = await DeliveryApi.VehicleAsync(client, dispatcher, 1_200m, cancellationToken);
        var leaving = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId, cancellationToken);
        var orphaned = await ReviewedDeliveryAsync(client, dispatcher, leaving, 1, cancellationToken);

        // A second driver, so "the standing is gone" is a claim about this driver's reviews rather
        // than about the aggregate having stopped working.
        var stays = await ReviewedAsync(client, dispatcher, cancellationToken, 5);

        Assert.Equal(
            1d,
            (await DriverAsync(client, dispatcher, leaving.DriverId, cancellationToken))
                .GetProperty("rating")
                .GetDouble());

        using (var deleted = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Delete,
                   "/api/drivers/" + leaving.DriverId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                   dispatcher,
                   body: null,
                   cancellationToken))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, deleted.StatusCode);
        }

        var roster = await ReviewApi.DriverRosterAsync(client, dispatcher, cancellationToken);

        // Gone from the roster, and their one-star verdict went with them rather than landing on
        // whoever is left.
        Assert.DoesNotContain(roster, row => row.GetProperty("id").GetInt32() == leaving.DriverId);

        var remaining = Assert.Single(roster, row => row.GetProperty("id").GetInt32() == stays);

        Assert.Equal(5d, remaining.GetProperty("rating").GetDouble());
        Assert.Equal(1, remaining.GetProperty("reviewCount").GetInt32());

        // And the review itself is still there, still readable by a moderator, now naming no
        // driver: the row outlived the person it was about, which is exactly what SetNull means.
        var reviews = await ReviewApi.RowsAsync(
            await ReviewApi.ListAsync(client, dispatcher, cancellationToken),
            cancellationToken);

        Assert.Equal(2, reviews.Length);

        var widowed = Assert.Single(
            reviews,
            row => row.GetProperty("deliveryId").GetInt32() == orphaned);

        Assert.Equal(JsonValueKind.Null, widowed.GetProperty("driver").ValueKind);
    }

    /// <summary>
    /// A driver who has carried one delivery per rating given, each reviewed by the client who
    /// requested it.
    /// </summary>
    /// <remarks>
    /// One delivery per review, because DR-6 allows exactly one review per delivery — which is what
    /// makes "a driver with three reviews" three finished deliveries rather than three rows against
    /// one. Every delivery goes to the same driver and a different client, since a client may
    /// request as many deliveries as they like but may review each of them once.
    /// </remarks>
    private static async Task<int> ReviewedAsync(
        HttpClient client,
        string dispatcherToken,
        CancellationToken cancellationToken,
        params int[] ratings)
    {
        var vehicleId = await DeliveryApi.VehicleAsync(client, dispatcherToken, 1_200m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcherToken, vehicleId, cancellationToken);

        foreach (var rating in ratings)
        {
            await ReviewedDeliveryAsync(client, dispatcherToken, driver, rating, cancellationToken);
        }

        return driver.DriverId;
    }

    /// <summary>
    /// One delivery carried by <paramref name="driver"/> from end to end and reviewed by the client
    /// who requested it, answering the delivery's own id.
    /// </summary>
    /// <remarks>
    /// A fresh client each time, because DR-6 allows one review per delivery and a client may
    /// review only their own: one review therefore means one delivery and one requester.
    /// </remarks>
    private static async Task<int> ReviewedDeliveryAsync(
        HttpClient client,
        string dispatcherToken,
        DeliveryApi.DriverCaller driver,
        int rating,
        CancellationToken cancellationToken)
    {
        var requester = await DeliveryApi.ClientAsync(client, dispatcherToken, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcherToken,
            DeliveryApi.NewDelivery(driverId: driver.DriverId, clientId: requester.ClientId),
            cancellationToken);

        await AdvanceAsync(client, driver.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken);
        await AdvanceAsync(
            client, driver.Token, deliveryId, DeliveryStatus.Delivered, cancellationToken, "вручено");

        await ReviewApi.WrittenAsync(client, requester.Token, deliveryId, rating, cancellationToken);

        return deliveryId;
    }

    private static async Task AdvanceAsync(
        HttpClient client,
        string driverToken,
        int deliveryId,
        DeliveryStatus status,
        CancellationToken cancellationToken,
        string? note = null)
    {
        using var response = await DeliveryApi.ChangeStatusAsync(
            client, driverToken, deliveryId, status, cancellationToken, note);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> DriverAsync(
        HttpClient client,
        string dispatcherToken,
        int driverId,
        CancellationToken cancellationToken)
    {
        var roster = await ReviewApi.DriverRosterAsync(client, dispatcherToken, cancellationToken);

        return roster.Single(row => row.GetProperty("id").GetInt32() == driverId);
    }
}
