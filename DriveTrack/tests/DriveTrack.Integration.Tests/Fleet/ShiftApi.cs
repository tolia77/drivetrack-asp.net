using System.Globalization;
using System.Net;
using System.Text.Json;

namespace DriveTrack.Integration.Tests.Fleet;

/// <summary>
/// The eight routes a shift is reached by, collected the way <c>ReviewApi</c> collects its five.
/// <para>
/// Every helper hands the response back unasserted, because half of story 4.2's matrix is about
/// <em>which</em> refusal a caller gets: a helper that insisted on success could only be used by the
/// rows that succeed. The two that do assert say so in their names.
/// </para>
/// <para>
/// The HTTP plumbing is <see cref="FleetApi"/>'s, unchanged. What lives here is only the shape of
/// the eight requests.
/// </para>
/// </summary>
internal static class ShiftApi
{
    /// <summary>Puts a driver on duty (FR-109). No instant: the server stamps it.</summary>
    public static Task<HttpResponseMessage> StartAsync(
        HttpClient client,
        string? token,
        int driverId,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client, HttpMethod.Post, "/api/shifts/start", token, new { driverId }, cancellationToken);

    /// <summary>Takes a driver off duty (FR-109).</summary>
    public static Task<HttpResponseMessage> EndAsync(
        HttpClient client,
        string? token,
        int driverId,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client, HttpMethod.Post, "/api/shifts/end", token, new { driverId }, cancellationToken);

    /// <summary>Records a shift that already happened (FR-113).</summary>
    /// <param name="client">The API client.</param>
    /// <param name="token">The caller, or null for an anonymous attempt.</param>
    /// <param name="driverId">The driver the shift belongs to.</param>
    /// <param name="startedAt">When it started.</param>
    /// <param name="endedAt">When it ended, or null to record it open.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public static Task<HttpResponseMessage> CreateAsync(
        HttpClient client,
        string? token,
        int driverId,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/shifts",
            token,
            new { driverId, startedAt, endedAt },
            cancellationToken);

    /// <summary>
    /// Corrects a shift (FR-114), sending exactly the fields the caller names.
    /// </summary>
    /// <remarks>
    /// The body is assembled from a dictionary rather than from an anonymous type with nullable
    /// members, because AD-23's distinction is between a property that is <em>absent</em> and one
    /// that is present: an anonymous type always writes both, which is the one thing the edit rows
    /// of this matrix need to be able to avoid.
    /// </remarks>
    public static Task<HttpResponseMessage> EditAsync(
        HttpClient client,
        string? token,
        int shiftId,
        CancellationToken cancellationToken,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null,
        bool sendNullEnd = false)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (startedAt is { } start)
        {
            body["startedAt"] = start;
        }

        if (endedAt is { } end)
        {
            body["endedAt"] = end;
        }

        if (sendNullEnd)
        {
            // The reopen attempt: the field is present and carries nothing. Optional<DateTimeOffset>
            // has no case for it, so this is the payload FR-117 is actually enforced against.
            body["endedAt"] = null;
        }

        return FleetApi.SendAsync(client, HttpMethod.Put, Path(shiftId), token, body, cancellationToken);
    }

    /// <summary>Deletes a shift (FR-114).</summary>
    public static Task<HttpResponseMessage> DeleteAsync(
        HttpClient client,
        string? token,
        int shiftId,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client, HttpMethod.Delete, Path(shiftId), token, body: null, cancellationToken);

    /// <summary>Reads one shift.</summary>
    public static Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string? token,
        int shiftId,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client, HttpMethod.Get, Path(shiftId), token, body: null, cancellationToken);

    /// <summary>Reads a page of shifts (FR-112, FR-113).</summary>
    /// <param name="client">The API client.</param>
    /// <param name="token">The caller, or null for an anonymous read.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <param name="query">
    /// A query string, <c>?offset=&amp;limit=&amp;driverId=</c>, for the rows that are about where
    /// the narrowing goes. Empty is the route's own default page.
    /// </param>
    public static Task<HttpResponseMessage> ListAsync(
        HttpClient client,
        string? token,
        CancellationToken cancellationToken,
        string query = "") =>
        FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/shifts" + query, token, body: null, cancellationToken);

    /// <summary>Reads which drivers are on duty (FR-116).</summary>
    public static Task<HttpResponseMessage> ListOnDutyAsync(
        HttpClient client,
        string? token,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/shifts/on-duty", token, body: null, cancellationToken);

    /// <summary>Starts a shift that must have succeeded, and answers its row id.</summary>
    public static async Task<int> StartedAsync(
        HttpClient client,
        string token,
        int driverId,
        CancellationToken cancellationToken)
    {
        using var response = await StartAsync(client, token, driverId, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await FleetApi.DataAsync(response, cancellationToken)).GetProperty("id").GetInt32();
    }

    /// <summary>Records a shift that must have succeeded, and answers its row id.</summary>
    public static async Task<int> RecordedAsync(
        HttpClient client,
        string token,
        int driverId,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        CancellationToken cancellationToken)
    {
        using var response = await CreateAsync(
            client, token, driverId, startedAt, endedAt, cancellationToken);

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

    private static string Path(int shiftId) =>
        "/api/shifts/" + shiftId.ToString(CultureInfo.InvariantCulture);
}
