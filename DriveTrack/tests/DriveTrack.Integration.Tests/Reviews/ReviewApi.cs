using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Integration.Tests.Deliveries;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Reviews;

/// <summary>
/// The arrangement the review suites share: a delivery that has actually been carried, and the five
/// routes a review is reached by.
/// <para>
/// The HTTP plumbing is <see cref="FleetApi"/>'s and the parties are <see cref="DeliveryApi"/>'s,
/// both reused unchanged. What lives here is the one thing neither has: a delivery in a state a
/// review may be written about, which is not something a test can arrange by writing a row — it is
/// a parcel a driver picked up and handed over, so it is arranged by doing that through the
/// endpoints that ship.
/// </para>
/// </summary>
internal static class ReviewApi
{
    /// <summary>A client, a driver and the finished delivery between them.</summary>
    /// <param name="DeliveryId">The delivery, at <see cref="DeliveryStatus.Delivered"/>.</param>
    /// <param name="Client">The client who requested it, signed in.</param>
    /// <param name="Driver">The driver who carried it, signed in.</param>
    internal sealed record Carried(
        int DeliveryId,
        DeliveryApi.ClientCaller Client,
        DeliveryApi.DriverCaller Driver);

    /// <summary>
    /// A delivery carried from <see cref="DeliveryStatus.Pending"/> to its end state, with the
    /// client and the driver who were party to it.
    /// </summary>
    /// <remarks>
    /// Driven through the real lifecycle rather than seeded at the end state, and that is not
    /// ceremony: FR-120 refuses <c>Delivered</c> without a proof or a note, and the transition table
    /// refuses Pending → Delivered outright — so a helper that "just set the status" would arrange a
    /// row the product cannot produce and prove nothing about the rule under test.
    /// </remarks>
    /// <param name="client">The API client.</param>
    /// <param name="dispatcherToken">A dispatch token, which is what opens a delivery.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <param name="status">
    /// Where to leave the delivery. <see cref="DeliveryStatus.Delivered"/> and
    /// <see cref="DeliveryStatus.Failed"/> are the two a review may be written about;
    /// <see cref="DeliveryStatus.Pending"/> and <see cref="DeliveryStatus.InTransit"/> are what the
    /// refusal rows of the matrix need.
    /// </param>
    /// <param name="existing">
    /// A client to attach the delivery to instead of registering another. Supplied by the tests that
    /// need two deliveries for one client — a second registration would answer the wrong question.
    /// </param>
    public static async Task<Carried> DeliveryAsync(
        HttpClient client,
        string dispatcherToken,
        CancellationToken cancellationToken,
        DeliveryStatus status = DeliveryStatus.Delivered,
        DeliveryApi.ClientCaller? existing = null)
    {
        var vehicleId = await DeliveryApi.VehicleAsync(client, dispatcherToken, 1_200m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcherToken, vehicleId, cancellationToken);
        var requester = existing
            ?? await DeliveryApi.ClientAsync(client, dispatcherToken, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcherToken,
            DeliveryApi.NewDelivery(driverId: driver.DriverId, clientId: requester.ClientId),
            cancellationToken);

        if (status != DeliveryStatus.Pending)
        {
            await AdvanceAsync(client, driver.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken);
        }

        if (status is DeliveryStatus.Delivered or DeliveryStatus.Failed)
        {
            // FR-120's note rather than a proof: the precondition is "a proof or an explanation",
            // and a sentence is the cheaper half of it in a suite that is not about proofs.
            await AdvanceAsync(client, driver.Token, deliveryId, status, cancellationToken, "вручено");
        }

        return new Carried(deliveryId, requester, driver);
    }

    /// <summary>Writes a review, and hands the response back rather than asserting it.</summary>
    /// <remarks>
    /// Unasserted because half this story's matrix is about which refusal a caller gets: a helper
    /// that insisted on success could only be used by the rows that succeed.
    /// </remarks>
    public static Task<HttpResponseMessage> WriteAsync(
        HttpClient client,
        string? token,
        int deliveryId,
        int rating,
        string? text,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/reviews",
            token,
            new { deliveryId, rating, text },
            cancellationToken);

    /// <summary>Reads the whole collection (FR-64).</summary>
    /// <param name="client">The API client.</param>
    /// <param name="token">The caller, or null for an anonymous read.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <param name="query">
    /// A paging query string, <c>?offset=&amp;limit=</c>, for the tests that are about where the
    /// narrowing goes. Empty is the route's own default page.
    /// </param>
    public static Task<HttpResponseMessage> ListAsync(
        HttpClient client,
        string? token,
        CancellationToken cancellationToken,
        string query = "") =>
        FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/reviews" + query, token, body: null, cancellationToken);

    /// <summary>Reads the caller's own reviews (FR-63).</summary>
    /// <param name="client">The API client.</param>
    /// <param name="token">The caller, or null for an anonymous read.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <param name="query">A paging query string; see <see cref="ListAsync"/>.</param>
    public static Task<HttpResponseMessage> ListMineAsync(
        HttpClient client,
        string? token,
        CancellationToken cancellationToken,
        string query = "") =>
        FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/reviews/mine" + query, token, body: null, cancellationToken);

    /// <summary>Edits a review (FR-65). Both fields are sent, because a form sends both.</summary>
    public static Task<HttpResponseMessage> EditAsync(
        HttpClient client,
        string? token,
        int reviewId,
        int rating,
        string text,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            "/api/reviews/" + reviewId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            token,
            new { rating, text },
            cancellationToken);

    /// <summary>Deletes a review (FR-66).</summary>
    public static Task<HttpResponseMessage> DeleteAsync(
        HttpClient client,
        string? token,
        int reviewId,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client,
            HttpMethod.Delete,
            "/api/reviews/" + reviewId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            token,
            body: null,
            cancellationToken);

    /// <summary>Writes a review that must have succeeded, and answers its row id.</summary>
    public static async Task<int> WrittenAsync(
        HttpClient client,
        string token,
        int deliveryId,
        int rating,
        CancellationToken cancellationToken,
        string text = "усе добре")
    {
        using var response = await WriteAsync(
            client, token, deliveryId, rating, text, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await FleetApi.DataAsync(response, cancellationToken)).GetProperty("id").GetInt32();
    }

    /// <summary>The rows of a list read that must have succeeded.</summary>
    public static async Task<JsonElement[]> RowsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await FleetApi.DataAsync(response, cancellationToken)).EnumerateArray()];
    }

    /// <summary>The driver roster as dispatch reads it, which is where FR-98's aggregate lands.</summary>
    public static async Task<JsonElement[]> DriverRosterAsync(
        HttpClient client,
        string dispatcherToken,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/drivers", dispatcherToken, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await FleetApi.DataAsync(response, cancellationToken)).EnumerateArray()];
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
