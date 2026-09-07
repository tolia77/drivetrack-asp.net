using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Api;

/// <summary>
/// NFR-1 and AD-7 asserted where they are actually promised: on the wire. One test per row of the
/// story's HTTP matrix, each driving the real <c>Program.cs</c> pipeline through a probe endpoint.
/// <para>
/// The original system's defect was never that any one endpoint was wrong - it was that each
/// endpoint was right in its own way. A contract asserted per shape rather than per endpoint is the
/// only kind that survives eight more epics of new controllers.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EnvelopeContractTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Success_returns_the_envelope_with_the_action_value()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/success", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await ReadAsync(response, cancellationToken);

        Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, envelope.RootElement.GetProperty("error").ValueKind);
        Assert.Equal("ok", envelope.RootElement.GetProperty("data").GetProperty("name").GetString());
        Assert.Equal(42, envelope.RootElement.GetProperty("data").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Enum_in_a_payload_is_serialized_as_its_member_name()
    {
        // AD-21: an ordinal is a number whose meaning changes the day someone reorders the enum.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/enum", UriKind.Relative),
            cancellationToken);

        var envelope = await ReadAsync(response, cancellationToken);
        var status = envelope.RootElement.GetProperty("data").GetProperty("status");

        Assert.Equal(JsonValueKind.String, status.ValueKind);
        Assert.Equal("InTransit", status.GetString());
    }

    [Fact]
    public async Task An_MVC_client_error_result_becomes_the_envelope_and_not_a_problem_document()
    {
        // SuppressMapClientErrors off would rewrite NotFound() into a ProblemDetails here.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/mvc-not-found", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain("\"title\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"traceId\"", body, StringComparison.Ordinal);

        using var envelope = JsonDocument.Parse(body);

        AssertFailure(envelope, ErrorCode.COMMON_NOT_FOUND);
    }

    [Fact]
    public async Task A_typed_not_found_carries_its_own_code()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/typed-not-found", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var envelope = await ReadAsync(response, cancellationToken);

        AssertFailure(envelope, ErrorCode.COMMON_NOT_FOUND);

        // NFR-3: the message is the resource string for the code, never the exception's.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.DoesNotContain("Probe delivery 7", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("domain-rule")]
    public async Task Both_conflict_kinds_return_409(string route)
    {
        // NFR-2: one failure kind, one status. AD-10 puts DomainRuleException on 409 beside
        // ConflictException precisely so a caller never has to know which one a service used.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/" + route, UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var envelope = await ReadAsync(response, cancellationToken);

        AssertFailure(envelope, ErrorCode.COMMON_CONFLICT);
    }

    [Fact]
    public async Task A_validation_failure_returns_422_and_names_every_field()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/validation", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);

        var envelope = await ReadAsync(response, cancellationToken);

        AssertFailure(envelope, ErrorCode.COMMON_VALIDATION_FAILED);

        var fields = envelope.RootElement.GetProperty("error").GetProperty("fields");

        // NFR-4: the field name survives verbatim, so a form can attach the message to its input.
        var rating = fields.GetProperty("rating");
        var weight = fields.GetProperty("packageWeightKg");

        Assert.Single(rating.EnumerateArray());
        Assert.Single(weight.EnumerateArray());
        Assert.All(
            rating.EnumerateArray().Concat(weight.EnumerateArray()),
            message => Assert.True(IsUkrainian(message.GetString())));
    }

    [Fact]
    public async Task An_unknown_field_message_key_falls_back_to_the_validation_text()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/validation-unknown-key", UriKind.Relative),
            cancellationToken);

        var envelope = await ReadAsync(response, cancellationToken);
        var message = envelope.RootElement
            .GetProperty("error").GetProperty("fields").GetProperty("rating")[0].GetString();

        // The raw key would be an untranslated identifier in front of a user (NFR-14).
        Assert.NotEqual(ProbeController.UnknownMessageKey, message);
        Assert.Equal(envelope.RootElement.GetProperty("error").GetProperty("message").GetString(), message);
        Assert.True(IsUkrainian(message));
    }

    [Fact]
    public async Task A_guard_rejection_returns_403()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/forbidden", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        AssertFailure(await ReadAsync(response, cancellationToken), ErrorCode.AUTH_FORBIDDEN);
    }

    [Fact]
    public async Task An_unauthenticated_request_gets_the_401_envelope_from_the_challenge_writer()
    {
        // AD-7's third suppression. Without it a cookie handler answers 302 to a login page, which
        // is HTML - a second wire shape at exactly the point a REST client needs to detect expiry.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync(
            new Uri("/api/probe/authenticated", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        AssertFailure(await ReadAsync(response, cancellationToken), ErrorCode.AUTH_UNAUTHENTICATED);
    }

    [Fact]
    public async Task An_authenticated_principal_failing_the_policy_gets_the_403_envelope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/probe/authorized", UriKind.Relative));
        request.Headers.Add(ProbeAuthentication.UserHeader, "probe-person");

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        AssertFailure(await ReadAsync(response, cancellationToken), ErrorCode.AUTH_FORBIDDEN);
    }

    [Fact]
    public async Task A_principal_holding_the_claim_reaches_the_action()
    {
        // The other half of the row above: the 403 has to mean the policy failed, not that the
        // probe scheme rejects everything.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/probe/authorized", UriKind.Relative));
        request.Headers.Add(ProbeAuthentication.UserHeader, "probe-person");
        request.Headers.Add(ProbeAuthentication.ClaimHeader, "yes");

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await ReadAsync(response, cancellationToken);

        Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task An_unexpected_failure_returns_500_and_leaks_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/boom", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // NFR-3. The body never reads the exception at all, so these three absences are structural
        // rather than a matter of having remembered to strip them.
        Assert.DoesNotContain("boom", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);

        using var envelope = JsonDocument.Parse(body);

        AssertFailure(envelope, ErrorCode.COMMON_UNEXPECTED_ERROR);
    }

    [Fact]
    public async Task An_unmatched_api_route_answers_json_and_not_the_html_page()
    {
        // Program.cs's UseStatusCodePagesWithReExecute would otherwise serve /not-found here.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/does-not-exist", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        AssertFailure(await ReadAsync(response, cancellationToken), ErrorCode.COMMON_NOT_FOUND);
    }

    [Fact]
    public async Task A_real_validator_names_its_fields_the_way_the_caller_spelled_them()
    {
        // FluentValidation reports the CLR name (Rating); the payload spelled it "rating". A form
        // cannot attach a message to an input it cannot find, so NFR-4 is only satisfied once the
        // adapter converts the key with the same naming policy it serializes everything else with.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/validator", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);

        var envelope = await ReadAsync(response, cancellationToken);
        var fields = envelope.RootElement.GetProperty("error").GetProperty("fields");

        Assert.Equal(
            ["rating", "title"],
            fields.EnumerateObject().Select(field => field.Name).Order().ToArray());
    }

    [Fact]
    public async Task A_status_that_forbids_a_body_does_not_get_one()
    {
        // NoContent() is a StatusCodeResult, so the arm that rescues NotFound() would otherwise put
        // {"success":true} on a 204 - a response no conforming client is allowed to read.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/no-content", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    [Fact]
    public async Task A_JsonResult_is_enveloped_like_any_other_value()
    {
        // JsonResult is not an ObjectResult, so it is the one helper that looks like it is already
        // doing the right thing while leaving unenveloped - the second wire shape NFR-1 closes.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/json-result", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await ReadAsync(response, cancellationToken);

        Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("json", envelope.RootElement.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_unmatched_path_outside_the_api_still_gets_the_HTML_not_found_page()
    {
        // The other side of the UseWhen predicate. Every other test here asks for /api, so a
        // predicate mis-edited to match everything would turn the Blazor shell into JSON with the
        // whole suite green.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/does-not-exist", UriKind.Relative),
            cancellationToken);

        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain("\"success\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_MVC_conflict_result_becomes_the_409_envelope()
    {
        // The 404 twin above is the only client-error status the filter's value-less arm was ever
        // driven at, which left ErrorContract.DefaultCodeFor's other arms unobserved over HTTP.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/probe/mvc-conflict", UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        AssertFailure(await ReadAsync(response, cancellationToken), ErrorCode.COMMON_CONFLICT);
    }

    [Fact]
    public async Task A_unique_violation_reaches_the_client_as_the_409_envelope()
    {
        // The defect this whole story exists to remove, asserted where it was actually reported: a
        // second review for one delivery used to leave as a 500. ConstraintTranslationTests proves
        // the translation and ErrorContractTests proves the status, but neither observes the join,
        // and "500 instead of 409" is a claim about the wire.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var (deliveryId, clientId) = await SeedDeliveryAsync(factory.Database, cancellationToken);

        using var first = await PersistReviewAsync(client, deliveryId, clientId, rating: 5, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await PersistReviewAsync(client, deliveryId, clientId, rating: 4, cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        AssertFailure(
            await ReadAsync(second, cancellationToken),
            ErrorCode.PERSISTENCE_UNIQUE_VIOLATION);
    }

    [Fact]
    public async Task A_check_violation_reaches_the_client_as_the_422_envelope()
    {
        // The other half of NFR-2: a rating of 6 is 422 on the wire whether a validator or the check
        // constraint caught it. The body must also carry nothing of the constraint - its name is a
        // fact about the schema (NFR-3).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var (deliveryId, clientId) = await SeedDeliveryAsync(factory.Database, cancellationToken);

        using var response = await PersistReviewAsync(client, deliveryId, clientId, rating: 6, cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain("23514", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ck_reviews_rating", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rating >= 1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DbUpdateException", body, StringComparison.Ordinal);

        using var envelope = JsonDocument.Parse(body);

        AssertFailure(envelope, ErrorCode.PERSISTENCE_CHECK_VIOLATION);
    }

    /// <summary>Seeds the client and delivery a review needs, in the host's own database.</summary>
    private static async Task<(int DeliveryId, int ClientId)> SeedDeliveryAsync(
        TestDatabase database,
        CancellationToken cancellationToken)
    {
        await using var context = await database.CreateContextAsync(cancellationToken);

        var seededClient = await Seed.ClientAsync(context, cancellationToken);
        var delivery = await Seed.DeliveryAsync(context, cancellationToken, seededClient.Id);

        return (delivery.Id, seededClient.Id.Value);
    }

    /// <summary>Commits one review through the probe, i.e. through the real production commit path.</summary>
    private static Task<HttpResponseMessage> PersistReviewAsync(
        HttpClient client,
        int deliveryId,
        int clientId,
        int rating,
        CancellationToken cancellationToken) =>
        client.PostAsync(
            new Uri(
                FormattableString.Invariant(
                    $"/api/probe/persist-review?deliveryId={deliveryId}&clientId={clientId}&rating={rating}"),
                UriKind.Relative),
            content: null,
            cancellationToken);

    /// <summary>Every failure envelope has the same three properties and a localized message.</summary>
    private static void AssertFailure(JsonDocument envelope, ErrorCode expected)
    {
        Assert.False(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, envelope.RootElement.GetProperty("data").ValueKind);

        var error = envelope.RootElement.GetProperty("error");

        Assert.Equal(expected.ToString(), error.GetProperty("code").GetString());
        Assert.True(IsUkrainian(error.GetProperty("message").GetString()));
    }

    /// <summary>True when the string carries Cyrillic and no Latin word (NFR-14).</summary>
    private static bool IsUkrainian(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Any(character => character is >= 'Ѐ' and <= 'ӿ')
        && !HasLatinWord(value);

    /// <summary>
    /// True when three or more Latin letters run together: an untranslated fragment or a leaked
    /// resource key. Two is not enough to be a word and would reject a legitimate unit or initial.
    /// </summary>
    private static bool HasLatinWord(string value)
    {
        var run = 0;

        foreach (var character in value)
        {
            run = char.IsAsciiLetter(character) ? run + 1 : 0;

            if (run >= 3)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<JsonDocument> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken)
        ?? throw new InvalidOperationException("The response body was not JSON.");
}
