using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Deliveries;

namespace DriveTrack.Integration.Tests.Authorization;

/// <summary>
/// One row per caller-facing REST endpoint: what it is, who may use it, and a request that reaches
/// it without changing anything.
/// <para>
/// Data rather than assertions, because two different tests read it. <c>EndpointBoundaryTests</c>
/// drives every row against every role; <c>EndpointInventoryTests</c> compares this table against
/// the host's own <c>EndpointDataSource</c> in both directions, so an endpoint that lands without a
/// row here fails the build and a row here that names no endpoint fails it too. A matrix that only
/// the first test read would be a list of the endpoints somebody remembered.
/// </para>
/// <para>
/// <b>Why most rows address a row id that does not exist.</b> Every single-row method in this system
/// loads, then guards, then answers 404 (<c>DriverService.UpdateAsync</c>,
/// <c>VehicleService.DeleteAsync</c>, <c>DeliveryService.UpdateAsync</c> and the rest all read that
/// way), and the members that take a nullable owner id — <c>RequireAssignedDriver</c>,
/// <c>RequireShiftOwner</c>, <c>RequireReviewOwner</c> — compare a null against the caller's row id
/// and refuse. So a refused role is refused at <see cref="AuthorizationWorld.MissingId"/> exactly as
/// it would be at a real id, and the one allowed caller each row probes with reaches a 404 instead
/// of a write. The rows that guard before they load instead send a body the validator rejects,
/// which is the same trick from the other end.
/// </para>
/// <para>
/// <b><see cref="EndpointRow.Allowed"/> is about this request, not about the route template.</b>
/// <c>PATCH /api/users/{id}</c> admits its own user, so against a missing id the set is
/// <c>{Admin}</c> — the administrator passes by AD-4's override. Ownership, which the role matrix
/// cannot reach by construction, is <c>OwnershipBoundaryTests</c>'s subject.
/// </para>
/// </summary>
internal static class EndpointMatrix
{
    /// <summary>The four roles a caller can hold, in the order the sweep reports them.</summary>
    public static readonly UserRole[] Roles =
        [UserRole.Admin, UserRole.Dispatcher, UserRole.Driver, UserRole.Client];

    private static readonly string Missing =
        AuthorizationWorld.MissingId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Every caller-facing REST endpoint the host publishes.</summary>
    public static readonly IReadOnlyList<EndpointRow> Rows =
    [
        // ---- FR-1, FR-4: the two anonymous entry points, and the only two. -------------------
        Public("POST", "api/auth/register", _ => Json(HttpMethod.Post, "/api/auth/register", new { })),
        Public("POST", "api/auth/sign-in", _ => Json(HttpMethod.Post, "/api/auth/sign-in", new { })),

        // ---- FR-5, FR-87, FR-88, FR-92: a caller's own account (RequireSelf). ----------------
        Row("GET", "api/users/{id:int}", [UserRole.Admin],
            _ => Json(HttpMethod.Get, "/api/users/" + Missing, null)),
        Row("PATCH", "api/users/{id:int}", [UserRole.Admin],
            _ => Json(HttpMethod.Patch, "/api/users/" + Missing, new { })),
        Row("POST", "api/users/{id:int}/password", [UserRole.Admin],
            _ => Json(HttpMethod.Post, "/api/users/" + Missing + "/password", new { })),
        Row("POST", "api/users/{id:int}/email", [UserRole.Admin],
            _ => Json(HttpMethod.Post, "/api/users/" + Missing + "/email", new { })),

        // ---- FR-46 to FR-48: the client roster. Reads are dispatch's, writes are an admin's. --
        Row("GET", "api/clients", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/clients", null)),
        Row("GET", "api/clients/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/clients/" + Missing, null)),
        Row("PATCH", "api/clients/{id:int}", [UserRole.Admin],
            _ => Json(HttpMethod.Patch, "/api/clients/" + Missing, new { })),
        Row("DELETE", "api/clients/{id:int}", [UserRole.Admin],
            _ => Json(HttpMethod.Delete, "/api/clients/" + Missing, null)),

        // ---- FR-49, FR-50: dispatcher accounts, admin only throughout. -----------------------
        Row("POST", "api/dispatchers", [UserRole.Admin],
            _ => Json(HttpMethod.Post, "/api/dispatchers", new { })),
        Row("GET", "api/dispatchers", [UserRole.Admin],
            _ => Json(HttpMethod.Get, "/api/dispatchers", null)),
        Row("GET", "api/dispatchers/{id:int}", [UserRole.Admin],
            _ => Json(HttpMethod.Get, "/api/dispatchers/" + Missing, null)),
        Row("PATCH", "api/dispatchers/{id:int}", [UserRole.Admin],
            _ => Json(HttpMethod.Patch, "/api/dispatchers/" + Missing, new { })),
        Row("DELETE", "api/dispatchers/{id:int}", [UserRole.Admin],
            _ => Json(HttpMethod.Delete, "/api/dispatchers/" + Missing, null)),

        // ---- FR-35 to FR-39: the driver roster. ----------------------------------------------
        Row("GET", "api/drivers", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/drivers", null)),
        Row("GET", "api/drivers/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/drivers/" + Missing, null)),
        Row("POST", "api/drivers", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Post, "/api/drivers", new { })),
        Row("PUT", "api/drivers/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Put, "/api/drivers/" + Missing, new { })),
        Row("DELETE", "api/drivers/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Delete, "/api/drivers/" + Missing, null)),

        // ---- FR-40 to FR-45: the fleet. ------------------------------------------------------
        Row("GET", "api/vehicles", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/vehicles", null)),
        Row("GET", "api/vehicles/unassigned", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/vehicles/unassigned", null)),
        Row("GET", "api/vehicles/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/vehicles/" + Missing, null)),
        Row("POST", "api/vehicles", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Post, "/api/vehicles", new { })),
        Row("PUT", "api/vehicles/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Put, "/api/vehicles/" + Missing, new { })),
        Row("DELETE", "api/vehicles/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Delete, "/api/vehicles/" + Missing, null)),

        // ---- FR-28: the notification log. ----------------------------------------------------
        Row("GET", "api/notifications", [UserRole.Admin],
            _ => Json(HttpMethod.Get, "/api/notifications", null)),

        // ---- FR-14 to FR-27, FR-89 to FR-91, FR-104 to FR-108, FR-119, FR-122: deliveries. ---
        Row("GET", "api/deliveries", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/deliveries", null)),

        // RequireScope admits all four: a driver and a client are narrowed rather than refused.
        Row("GET", "api/deliveries/mine", [.. Roles],
            _ => Json(HttpMethod.Get, "/api/deliveries/mine", null)),

        // RequireDeliveryComposer: a driver composes nothing, which is what keeps the geocoder out
        // of reach of the one role that only ever reads deliveries. No query string, so the
        // validator refuses before the port is ever asked.
        Row("GET", "api/deliveries/places", [UserRole.Admin, UserRole.Dispatcher, UserRole.Client],
            _ => Json(HttpMethod.Get, "/api/deliveries/places", null)),
        Row("GET", "api/deliveries/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/deliveries/" + Missing, null)),
        Row("POST", "api/deliveries", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Post, "/api/deliveries", new { })),

        // FR-89: a client's own request, and nobody else's. Dispatch and an administrator compose
        // for somebody else, so the guard answers null and DeliveryService refuses it outright.
        Row("POST", "api/deliveries/requests", [UserRole.Client],
            _ => Json(HttpMethod.Post, "/api/deliveries/requests", new { })),
        Row("PUT", "api/deliveries/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Put, "/api/deliveries/" + Missing, new { })),
        Row("DELETE", "api/deliveries/{id:int}", [UserRole.Admin],
            _ => Json(HttpMethod.Delete, "/api/deliveries/" + Missing, null)),

        // FR-34: the assigned driver, or dispatch. A null assignment never equals a driver's row
        // id, so at a missing delivery the set is dispatch's alone.
        Row("POST", "api/deliveries/{id:int}/status", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(
                HttpMethod.Post,
                "/api/deliveries/" + Missing + "/status",
                new { status = "InTransit" })),
        Row("POST", "api/deliveries/{id:int}/timeline", [.. Roles],
            _ => Json(
                HttpMethod.Post,
                "/api/deliveries/" + Missing + "/timeline",
                new { note = "нотатка" })),
        Row("GET", "api/deliveries/{id:int}/timeline", [.. Roles],
            _ => Json(HttpMethod.Get, "/api/deliveries/" + Missing + "/timeline", null)),

        // Multipart, and it has to be: [FromForm] binding runs before the service, so a JSON body
        // here would be answered 415 by the framework and the guard's refusal would never be the
        // thing under test.
        Row("POST", "api/deliveries/{id:int}/proof", [UserRole.Admin, UserRole.Dispatcher],
            _ => Capture("/api/deliveries/" + Missing + "/proof")),
        Row("GET", "api/deliveries/{id:int}/proof", [.. Roles],
            _ => Json(HttpMethod.Get, "/api/deliveries/" + Missing + "/proof", null)),

        // ---- FR-62 to FR-67: reviews. Three different sets of callers across five routes. -----
        Row("GET", "api/reviews", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/reviews", null)),
        Row("GET", "api/reviews/mine", [UserRole.Client],
            _ => Json(HttpMethod.Get, "/api/reviews/mine", null)),
        Row("POST", "api/reviews", [UserRole.Client],
            _ => Json(
                HttpMethod.Post,
                "/api/reviews",
                new { deliveryId = AuthorizationWorld.MissingId, rating = 5, text = "текст" })),
        Row("PUT", "api/reviews/{id:int}", [UserRole.Admin],
            _ => Json(HttpMethod.Put, "/api/reviews/" + Missing, new { rating = 5, text = "текст" })),
        Row("DELETE", "api/reviews/{id:int}", [UserRole.Admin],
            _ => Json(HttpMethod.Delete, "/api/reviews/" + Missing, null)),

        // ---- FR-109 to FR-117: shifts. A client has no part in any of them (FR-115). ----------
        Row("GET", "api/shifts", [UserRole.Admin, UserRole.Dispatcher, UserRole.Driver],
            _ => Json(HttpMethod.Get, "/api/shifts", null)),
        Row("GET", "api/shifts/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/shifts/" + Missing, null)),
        Row("GET", "api/shifts/on-duty", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Get, "/api/shifts/on-duty", null)),

        // The three command routes guard on the driver the body names, so a driver who is not that
        // driver is refused before the row is looked for.
        Row("POST", "api/shifts", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(
                HttpMethod.Post,
                "/api/shifts",
                new
                {
                    driverId = AuthorizationWorld.MissingId,
                    startedAt = "2026-09-01T08:00:00+00:00",
                    endedAt = (string?)null,
                })),
        Row("POST", "api/shifts/start", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(
                HttpMethod.Post, "/api/shifts/start", new { driverId = AuthorizationWorld.MissingId })),
        Row("POST", "api/shifts/end", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(
                HttpMethod.Post, "/api/shifts/end", new { driverId = AuthorizationWorld.MissingId })),
        Row("PUT", "api/shifts/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(
                HttpMethod.Put,
                "/api/shifts/" + Missing,
                new { startedAt = "2026-09-01T08:00:00+00:00" })),
        Row("DELETE", "api/shifts/{id:int}", [UserRole.Admin, UserRole.Dispatcher],
            _ => Json(HttpMethod.Delete, "/api/shifts/" + Missing, null)),

        // ---- FR-122 / AD-26: the one route stored bytes leave by. Not a controller, and easy to
        // miss for exactly that reason - it is a minimal API mounted outside /api.
        Row("GET", "proof-assets/{assetId:int}", [.. Roles],
            _ => Json(HttpMethod.Get, "/proof-assets/" + Missing, null)),
    ];

    /// <summary>Every row, keyed the way the theories name them.</summary>
    public static IReadOnlyDictionary<string, EndpointRow> ByKey { get; } =
        Rows.ToDictionary(row => row.Key, StringComparer.Ordinal);

    /// <summary>The row with that key, or a failure naming it.</summary>
    public static EndpointRow Resolve(string key) =>
        ByKey.TryGetValue(key, out var row)
            ? row
            : throw new ArgumentOutOfRangeException(nameof(key), key, "No endpoint row carries that key.");

    private static EndpointRow Row(
        string verb,
        string template,
        IReadOnlyList<UserRole> allowed,
        Func<AuthorizationWorld, HttpRequestMessage> request) =>
        new(verb, template, IsPublic: false, allowed.ToHashSet(), request);

    private static EndpointRow Public(
        string verb,
        string template,
        Func<AuthorizationWorld, HttpRequestMessage> request) =>
        new(verb, template, IsPublic: true, EndpointMatrix.Roles.ToHashSet(), request);

    /// <summary>A JSON request, built fresh: an <see cref="HttpContent"/> cannot be sent twice.</summary>
    private static HttpRequestMessage Json(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    /// <summary>
    /// The smallest legal-looking proof capture: a recipient, a point, a signature and a photograph.
    /// </summary>
    /// <remarks>
    /// It never reaches the store. The delivery id is a missing one, so an allowed caller is
    /// answered 404 inside the first, uncommitted scope — before a key is even minted (AD-26) — and
    /// a refused caller never gets that far.
    /// </remarks>
    private static HttpRequestMessage Capture(string path)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(AuthorizationWorld.ProofRecipientName, Encoding.UTF8), "RecipientName" },

            // Invariant, and that is load-bearing: the host runs under uk-UA, which writes 50,4501,
            // and a comma in a form field is a value the binder reads as something else entirely.
            { new StringContent("50.4501", Encoding.UTF8), "Latitude" },
            { new StringContent("30.5234", Encoding.UTF8), "Longitude" },
        };

        content.Add(Part(ProofApi.Png, "image/png"), "Signature", "signature.png");
        content.Add(Part(ProofApi.Jpeg, "image/jpeg"), "Photos", "photo.jpg");

        return new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = content,
        };
    }

    private static ByteArrayContent Part(byte[] bytes, string contentType)
    {
        var part = new ByteArrayContent(bytes);

        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        return part;
    }
}

/// <summary>One caller-facing REST endpoint, as the sweep reads it.</summary>
/// <param name="Verb">The HTTP method, upper-cased as the routing table records it.</param>
/// <param name="Template">
/// The route template without its leading slash, exactly as <c>EndpointDataSource</c> publishes it
/// — constraints included, because that is the string the inventory test compares against.
/// </param>
/// <param name="IsPublic">
/// True for the two endpoints an anonymous caller is <em>meant</em> to reach. Exactly two, pinned
/// from the other direction by <c>EndpointInventoryTests</c>.
/// </param>
/// <param name="Allowed">
/// The roles that pass authorization for <em>this</em> request. Everyone else is expected to be
/// refused with <c>AUTH_FORBIDDEN</c> at 403, and an anonymous caller with
/// <c>AUTH_UNAUTHENTICATED</c> at 401.
/// </param>
/// <param name="Request">
/// Builds the request afresh on every call, because <see cref="HttpContent"/> cannot be replayed —
/// and the sweep sends each row at least twice.
/// <para>
/// It takes the world so a row <em>can</em> address a seeded id, and today none does: the shape that
/// lets an allowed caller probe a route without writing to it is a missing id or a rejected body, so
/// every row currently builds itself from <see cref="AuthorizationWorld.MissingId"/> alone. A row
/// that needs a real one — a route with no non-mutating probe — has somewhere to get it.
/// </para>
/// </param>
internal sealed record EndpointRow(
    string Verb,
    string Template,
    bool IsPublic,
    IReadOnlySet<UserRole> Allowed,
    Func<AuthorizationWorld, HttpRequestMessage> Request)
{
    /// <summary>How a theory names this row: <c>VERB template</c>.</summary>
    public string Key => Verb + " " + Template;

    /// <summary>The roles this request is expected to refuse.</summary>
    public IEnumerable<UserRole> Refused =>
        IsPublic ? [] : EndpointMatrix.Roles.Where(role => !Allowed.Contains(role));
}
