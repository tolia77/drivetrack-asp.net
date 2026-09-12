using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Deliveries;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Reviews;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DriveTrack.Integration.Tests.Api;

/// <summary>
/// A body that never bound answers 422 and not 500 (DW-9, DW-12, DW-47).
/// <para>
/// AD-7's first suppression means a failed body binding does not short-circuit: the argument is
/// omitted, the action runs with a null command, and the <c>ArgumentNullException.ThrowIfNull</c>
/// opening every Application write method used to surface as an unmodelled 500 - the caller's own
/// malformed bytes reported as a defect of the server. <c>RequestBodyBindingFilter</c> refuses it
/// first, and these rows assert the refusal on real routes rather than on a probe, because the
/// claim is that it holds for every <c>[FromBody]</c> action and not for one contrived one.
/// </para>
/// </summary>
public class RequestBodyBindingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_filter_is_registered_globally_in_the_running_host()
    {
        // The property that makes this a pipeline fix rather than a controller fix. Scoping the
        // filter to one controller with an attribute would leave every row below green while the
        // other eight controllers went on answering 500 - a regression visible nowhere else.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);

        // Forces the host to start, so these are the filters the pipeline is actually using.
        using var client = factory.CreateClient();

        var filters = factory.Services.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;

        Assert.Contains(
            filters,
            filter => filter is TypeFilterAttribute { ImplementationType: var type }
                && type == typeof(RequestBodyBindingFilter));
    }

    [Fact]
    public async Task An_empty_body_is_refused_as_a_validation_failure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            "/api/auth/register",
            token: null,
            json: string.Empty,
            cancellationToken);

        var envelope = await AssertRefusedAsync(response, cancellationToken);

        AssertNamesNoField(envelope);
    }

    [Fact]
    public async Task A_malformed_body_is_refused_as_a_validation_failure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            "/api/auth/register",
            token: null,
            json: """{"email":""",
            cancellationToken);

        var envelope = await AssertRefusedAsync(response, cancellationToken);

        // Measured, not assumed: a payload truncated mid-property still carries a JSON path, and
        // the parser records it as `$.email`. So this row does name a field - and the name is one
        // the caller's own bytes spelled, which is exactly what NFR-4 asks for. The guard here is
        // that it is *that* name and nothing invented: a key derived from the CLR type would fail.
        var fields = envelope.GetProperty("error").GetProperty("fields");

        Assert.Equal(
            ["email"],
            fields.EnumerateObject().Select(field => field.Name).ToArray());
    }

    [Fact]
    public async Task A_literal_null_body_is_refused_as_a_validation_failure()
    {
        // The one shape that binds *successfully* to nothing: `null` is valid JSON, so the argument
        // is present and the value is null. Without the null arm of the filter's test this reaches
        // the action and becomes the 500 the whole story is about.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            "/api/auth/register",
            token: null,
            json: "null",
            cancellationToken);

        await AssertRefusedAsync(response, cancellationToken);
    }

    [Fact]
    public async Task A_wrong_typed_field_on_an_anonymous_route_is_refused()
    {
        // Anonymous, so nothing about this answer can be attributed to the access guard: the
        // refusal is the adapter's, and it reaches a caller who has not signed in.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            "/api/auth/register",
            token: null,
            json: """{"email": 7}""",
            cancellationToken);

        await AssertRefusedAsync(response, cancellationToken);
    }

    [Fact]
    public async Task A_wrong_typed_field_names_the_offending_property()
    {
        // DW-47's measured case, on an authenticated route - the same answer as the anonymous one
        // above, which is what makes this a pipeline fix rather than a per-route one. NFR-4: the
        // key is the name the caller sent, so a form can attach the message to its own input.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        using var response = await PostJsonAsync(
            client,
            "/api/vehicles",
            token,
            json: """
                {
                  "model": "Рено Мастер",
                  "licensePlate": "AA12345678",
                  "capacityKg": 1200,
                  "mileage": "abc"
                }
                """,
            cancellationToken);

        var envelope = await AssertRefusedAsync(response, cancellationToken);
        var fields = envelope.GetProperty("error").GetProperty("fields");

        Assert.Equal(JsonValueKind.Object, fields.ValueKind);
        Assert.True(
            fields.TryGetProperty("mileage", out var messages),
            "Expected error.fields to key the offending property, got: " + fields);

        // The message is the catalogue's, like every other one on this wire - never the parser's.
        Assert.All(
            messages.EnumerateArray(),
            message => Assert.False(string.IsNullOrWhiteSpace(message.GetString())));
    }

    [Fact]
    public async Task A_fractional_value_for_an_integer_field_is_refused()
    {
        // DW-47's evidence was measured on this exact request, and an earlier review pass refuted
        // it by reasoning that 3.5 "arrives at the action as the int default 0". It does not: the
        // deserializer refuses the token, the whole body fails to bind, and before this filter the
        // action ran with a null command. Kept as a row because the refutation was plausible.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var carried = await ReviewApi.DeliveryAsync(client, dispatcher, cancellationToken);

        using var response = await PostJsonAsync(
            client,
            "/api/reviews",
            carried.Client.Token,
            json: $$"""{"deliveryId": {{carried.DeliveryId}}, "rating": 3.5, "text": "текст"}""",
            cancellationToken);

        var envelope = await AssertRefusedAsync(response, cancellationToken);
        var fields = envelope.GetProperty("error").GetProperty("fields");

        Assert.True(
            fields.TryGetProperty("rating", out _),
            "Expected error.fields to key the offending property, got: " + fields);
    }

    [Fact]
    public async Task A_wrong_typed_field_beside_a_route_value_names_only_the_body_property()
    {
        // The shape the anonymous rows cannot reach: a body bound alongside a route value, so model
        // state holds an `id` entry next to the formatter's `$.note`. This is the only surface where
        // FieldsFrom's anchoring has a competing key to reject - unanchor the `$.` test and a key
        // from another binding source starts contributing a field name the caller never sent.
        //
        // The id need not exist: binding fails before the action runs, so the service is never asked
        // about delivery 999. That is the claim, not an accident of the fixture.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        using var response = await PostJsonAsync(
            client,
            "/api/deliveries/999/status",
            dispatcher,
            json: """{"note": 7}""",
            cancellationToken);

        var envelope = await AssertRefusedAsync(response, cancellationToken);
        var fields = envelope.GetProperty("error").GetProperty("fields");

        Assert.Equal(
            ["note"],
            fields.EnumerateObject().Select(field => field.Name).ToArray());
    }

    [Fact]
    public async Task A_wrong_typed_optional_field_is_refused_but_names_nothing()
    {
        // Measured, and the reason the promise in the intent is worded "where the model state
        // carries the JSON path". OptionalJsonConverter reads its value through a nested
        // JsonSerializer.Deserialize, which starts a fresh path: the JsonException comes back with
        // Path `$` rather than `$.mileage`, so the input formatter records a key that carries no
        // property name and the filter has nothing to name. Measured directly: Optional<int> gives
        // `$`, a plain int in the same position gives `$.mileage`.
        //
        // Every update route is AD-23 Optional-typed, so this is half the write surface answering
        // 422 without an error.fields key - a real NFR-4 shortfall, carried in the spec's deferred
        // list rather than patched here, because closing it means changing how the converter reads.
        // Pinned so that closing it turns this row red rather than passing unnoticed.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);
        var vehicleId = await CreateVehicleAsync(client, token, cancellationToken);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Put,
            "/api/vehicles/" + vehicleId,
            token,
            json: """{"mileage": "abc"}""",
            cancellationToken);

        var envelope = await AssertRefusedAsync(response, cancellationToken);

        AssertNamesNoField(envelope);
    }

    [Fact]
    public async Task An_explicit_null_for_a_non_nullable_field_is_still_the_converters_refusal()
    {
        // The I/O matrix calls this row "unchanged", and unchanged is exactly what needs pinning:
        // there are now two producers of 422 for one input class. OptionalJsonConverter throws
        // during model binding, before any action filter, so it answers first and names no field;
        // this filter answers only when binding fails outright and names the JSON path. If that
        // precedence ever swapped - the converter throwing JsonException, say - the wire shape would
        // gain an `error.fields` member with nothing red to show for it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);
        var vehicleId = await CreateVehicleAsync(client, token, cancellationToken);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Put,
            "/api/vehicles/" + vehicleId,
            token,
            json: """{"mileage": null}""",
            cancellationToken);

        var envelope = await AssertRefusedAsync(response, cancellationToken);

        AssertNamesNoField(envelope);
    }

    [Fact]
    public async Task An_anonymous_caller_still_meets_401_before_this_refusal()
    {
        // AD-3's floor, and the reason this is an action filter rather than anything earlier: an
        // unbindable body on a route requiring authentication must answer 401, not 422. A 422 here
        // would tell an anonymous caller that /api/vehicles exists and takes a body.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            "/api/vehicles",
            token: null,
            json: """{"model": 7}""",
            cancellationToken);

        await FleetApi.AssertFailureAsync(
            response,
            HttpStatusCode.Unauthorized,
            ErrorCode.AUTH_UNAUTHENTICATED,
            cancellationToken);
    }

    [Fact]
    public async Task An_authenticated_caller_lacking_the_role_meets_this_refusal_before_403()
    {
        // The deliberate half of the precedence, pinned so that moving it is visible. A client may
        // not manage the fleet, and IAccessGuard would answer 403 - but there is no command to hand
        // the service, so the adapter answers first. What AD-3 protects is an unauthorized caller
        // learning something about stored state; "your JSON does not parse" is a fact about the
        // caller's own bytes and discloses nothing. The 401 row above is the line that does not move.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Client, cancellationToken);

        using var response = await PostJsonAsync(
            client,
            "/api/vehicles",
            token,
            json: """{"model": 7}""",
            cancellationToken);

        await AssertRefusedAsync(response, cancellationToken);
    }

    [Fact]
    public async Task An_unsupported_media_type_is_still_refused_as_415()
    {
        // The framework's UnsupportedContentTypeFilter short-circuits before this story's filter
        // runs, and must go on doing so: 415 says "send me different bytes", 422 says "send me
        // different values", and a caller acting on the wrong one re-sends the same request.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/auth/register", UriKind.Relative))
        {
            Content = new StringContent("plain text", Encoding.UTF8, "text/plain"),
        };

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task A_well_formed_body_still_reaches_the_action()
    {
        // The other half of the claim: the filter is inert on everything that binds.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            "/api/auth/register",
            token: null,
            json: RegistrationJson(FleetApi.UniqueEmail()),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await FleetApi.ReadAsync(response, cancellationToken);

        Assert.True(envelope.GetProperty("success").GetBoolean());
    }

    /// <summary>
    /// A payload the register endpoint accepts, spelled out rather than serialized from an object
    /// so that the one well-formed row travels the same raw-string path as the malformed ones.
    /// </summary>
    /// <remarks>
    /// The address is interpolated rather than substituted into a sentinel: a sentinel that is
    /// later renamed or reformatted fails open, posting a literal placeholder as the email and
    /// leaving this row green for the wrong reason.
    /// </remarks>
    private static string RegistrationJson(string email) => $$"""
        {
          "firstName": "Олена",
          "lastName": "Петренко",
          "email": "{{email}}",
          "phoneNumber": "+380441234567",
          "password": "{{FleetApi.Password}}",
          "passwordConfirmation": "{{FleetApi.Password}}"
        }
        """;

    /// <summary>
    /// A vehicle to aim a <c>PUT</c> at, created through the real create route.
    /// </summary>
    /// <remarks>
    /// Seeded over HTTP rather than into the database, so the row the update targets is one the
    /// adapter itself produced. Sent through <c>FleetApi.SendAsync</c>, because this payload is
    /// well-formed and has no reason to travel the raw-string path the refusal rows need.
    /// </remarks>
    private static async Task<int> CreateVehicleAsync(
        HttpClient client,
        string token,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/vehicles",
            token,
            new
            {
                model = "Рено Мастер",
                licensePlate = FleetApi.UniquePlate(),
                capacityKg = 1200m,
                mileage = 42_000,
                nextMaintenanceDate = "2026-12-01",
            },
            cancellationToken);

        var data = await FleetApi.DataAsync(response, cancellationToken);

        return data.GetProperty("id").GetInt32();
    }

    /// <summary>One POST carrying a raw string as <c>application/json</c>.</summary>
    private static Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string path,
        string? token,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(client, HttpMethod.Post, path, token, json, cancellationToken);

    /// <summary>
    /// One request carrying a raw string as <c>application/json</c>.
    /// </summary>
    /// <remarks>
    /// Built by hand rather than through <c>FleetApi.SendAsync</c>, which serializes an object and
    /// therefore cannot send the shapes this suite is about: nothing at all, something that is not
    /// JSON, a literal null, and a field of the wrong type.
    /// </remarks>
    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? token,
        string json,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Asserts the 422 envelope, and that nothing framework-shaped rode along with it.
    /// </summary>
    /// <remarks>
    /// NFR-3 and NFR-14 are the point of the negative half: the pipeline knows the parser's
    /// message, the JSON path and the CLR type of the command, and none of the three may reach a
    /// client. Asserting only the code and status would leave that free to regress silently.
    /// </remarks>
    private static async Task<JsonElement> AssertRefusedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var envelope = document.RootElement.Clone();

        Assert.False(envelope.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("data").ValueKind);

        var error = envelope.GetProperty("error");

        Assert.Equal(
            nameof(ErrorCode.COMMON_VALIDATION_FAILED),
            error.GetProperty("code").GetString());

        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        // No ProblemDetails, no model-state prose, no JSON path, no type or stack frame.
        foreach (var forbidden in (string[])
                 [
                     "traceId",
                     "\"title\"",
                     "JSON value could not be converted",
                     "Path:",
                     "$.",
                     "Command",
                     "System.",
                     "DriveTrack.",
                 ])
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
        }

        return envelope;
    }

    /// <summary>
    /// Asserts the refusal named no field at all.
    /// </summary>
    /// <remarks>
    /// A bodiless request carries no JSON path, so the filter has no name for the input and NFR-4
    /// is better served by silence than by a key invented from the CLR type - a name the caller's
    /// form does not have. Asserted rather than assumed, because the other rows all read
    /// <c>error.fields</c> expecting a key, and the invention would otherwise go unnoticed.
    /// </remarks>
    private static void AssertNamesNoField(JsonElement envelope) =>
        Assert.False(
            envelope.GetProperty("error").TryGetProperty("fields", out var fields),
            "Expected no error.fields on a request that carries no JSON path, got: " + fields);
}
