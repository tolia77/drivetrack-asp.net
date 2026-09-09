using System.Globalization;
using System.Net;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Identity;

/// <summary>
/// The client rows of story 7.1's I/O matrix (FR-46, FR-47, FR-48), over HTTP, against the real
/// <c>Program.cs</c> pipeline with the real JWT scheme.
/// <para>
/// These claims are only true over the wire and against the schema. "A dispatcher may read the
/// roster and may not edit it" is a property of the guard reached through the whole adapter, and
/// "deleting a client leaves its deliveries unassigned" is a property of five foreign keys nobody
/// can assert from C#.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ClientAdministrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_admin_reads_every_client_with_their_name_email_and_phone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var first = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Олена", phoneNumber: "+380441234567");
        var second = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Марія", phoneNumber: "+380509876543");

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, "/api/clients", admin, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = (await AdministrationApi.ReadAsync(response, cancellationToken)).GetProperty("data");

        var one = Row(rows, first.UserId);
        var two = Row(rows, second.UserId);

        Assert.Equal("Олена", one.GetProperty("firstName").GetString());
        Assert.Equal(first.Email, one.GetProperty("email").GetString());
        Assert.Equal("+380441234567", one.GetProperty("phoneNumber").GetString());

        Assert.Equal("Марія", two.GetProperty("firstName").GetString());
        Assert.Equal("+380509876543", two.GetProperty("phoneNumber").GetString());

        // AD-22: the two identities are separate numbers on the wire as well as in the type system,
        // and the client row's own key is what a delivery will one day point at.
        Assert.True(one.GetProperty("clientId").GetInt32() > 0);
    }

    [Fact]
    public async Task A_dispatcher_reads_the_same_roster()
    {
        // FR-48, and the reason the read is RequireRole(Dispatcher) rather than RequireRole(Admin):
        // a dispatcher needs this list to attach a client to a delivery.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var dispatcher = await AdministrationApi.CreateDispatcherAsync(client, admin, cancellationToken);

        var token = await AdministrationApi.SignInAsync(
            client, dispatcher.Email, AdministrationApi.Password, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, "/api/clients", token, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = (await AdministrationApi.ReadAsync(response, cancellationToken)).GetProperty("data");

        Assert.Equal(subject.Email, Row(rows, subject.UserId).GetProperty("email").GetString());

        // The detail read too, and asserted separately because it is guarded separately: GetAsync is
        // RequireRole(Dispatcher) while every write beside it is RequireRole(Admin). Without this,
        // narrowing the single-client read to Admin would break FR-48 and leave the suite green.
        using var detail = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, Route(subject.UserId), token, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(
            subject.Email,
            (await AdministrationApi.ReadAsync(detail, cancellationToken))
                .GetProperty("data").GetProperty("email").GetString());
    }

    [Fact]
    public async Task The_roster_is_ordered_by_surname_rather_than_by_insertion()
    {
        // ListByRoleAsync documents `ORDER BY last_name, first_name, id`, and every other assertion
        // in this file finds its row by id - so the ordering was asserted nowhere and dropping the
        // clause would have changed nothing anybody could see. The two surnames sort opposite to the
        // order the accounts are opened in, so insertion order cannot pass by coincidence.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var later = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Олена", lastName: "Яремчук");
        var earlier = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Марія", lastName: "Андрієнко");

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, "/api/clients", admin, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ids = (await AdministrationApi.ReadAsync(response, cancellationToken))
            .GetProperty("data")
            .EnumerateArray()
            .Select(row => row.GetProperty("userId").GetInt32())
            .ToArray();

        var earlierAt = Array.IndexOf(ids, earlier.UserId);
        var laterAt = Array.IndexOf(ids, later.UserId);

        Assert.True(earlierAt >= 0 && laterAt >= 0, "The roster is missing one of the two clients.");

        // Андрієнко before Яремчук, although Яремчук was registered first and holds the lower id.
        Assert.True(
            earlierAt < laterAt,
            "The roster is ordered by insertion rather than by surname.");
    }

    [Fact]
    public async Task A_client_is_refused_the_roster_and_told_nothing_about_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var token = await AdministrationApi.SignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, "/api/clients", token, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AdministrationApi.AssertFailureAsync(response, ErrorCode.AUTH_FORBIDDEN, cancellationToken);

        // Nothing disclosed: the refusal carries no data at all, not even the caller's own row.
        var body = await AdministrationApi.BodyAsync(response, cancellationToken);

        Assert.DoesNotContain(subject.Email, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anonymous_caller_is_unauthenticated_rather_than_forbidden()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, "/api/clients", token: null, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AdministrationApi.AssertFailureAsync(
            response, ErrorCode.AUTH_UNAUTHENTICATED, cancellationToken);
    }

    [Fact]
    public async Task An_admin_edits_only_the_fields_the_body_carries()
    {
        // AD-23's whole point, over the wire: a body naming two fields changes two fields, and the
        // last name and the password are not "unspecified", they are untouched.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Олена", lastName: "Петренко");
        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        using var response = await AdministrationApi.SendAsync(
            client,
            HttpMethod.Patch,
            Route(subject.UserId),
            admin,
            new { firstName = "Оксана", phoneNumber = "+380671112233" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = (await AdministrationApi.ReadAsync(response, cancellationToken)).GetProperty("data");

        Assert.Equal("Оксана", updated.GetProperty("firstName").GetString());
        Assert.Equal("Петренко", updated.GetProperty("lastName").GetString());
        Assert.Equal("+380671112233", updated.GetProperty("phoneNumber").GetString());

        // Read back rather than trusted from the response body: the phone number is the one field
        // written by mutating the tracked client row instead of through a repository member, so the
        // response would carry the new value whether or not the commit ever wrote it. Padded on the
        // way in as well, because the merge trims before it validates.
        using (var read = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), admin,
            new { phoneNumber = "  +380509876543  " }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        var stored = await GetAsync(client, admin, subject.UserId, cancellationToken);

        Assert.Equal("+380509876543", stored.GetProperty("phoneNumber").GetString());
        Assert.Equal("Оксана", stored.GetProperty("firstName").GetString());

        // And the password the body never mentioned still signs in (FR-50).
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task An_omitted_field_and_a_null_one_are_different_requests()
    {
        // AD-23 on the wire, which is the only place the distinction can be proved: the same
        // property, absent and then explicitly null, has to produce two different answers. If the
        // JSON converter folded null back into absent - which is what happens without one - the
        // first request would pass and the second would pass too, and nothing else in the suite
        // would notice.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Олена");
        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        // Absent: the name is not mentioned, so it is left alone.
        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), admin,
            new { phoneNumber = "+380671112233" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal("Олена", (await GetAsync(client, admin, subject.UserId, cancellationToken))
            .GetProperty("firstName").GetString());

        // Present and null: the caller asked to clear a field that cannot be cleared, and is told
        // so rather than quietly ignored or handed an empty name.
        using (var response = await SendRawAsync(
            client, Route(subject.UserId), admin, """{"firstName":null}""", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var fields = (await AdministrationApi.ReadAsync(response, cancellationToken))
                .GetProperty("error")
                .GetProperty("fields");

            Assert.True(fields.TryGetProperty("firstName", out _));
        }

        Assert.Equal("Олена", (await GetAsync(client, admin, subject.UserId, cancellationToken))
            .GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task An_admin_replaces_a_client_password_and_the_old_one_stops_working()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), admin,
            new { password = "New-Passw0rd" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.False(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, "New-Passw0rd", cancellationToken));

        // FR-8: what landed is a hash, and the plaintext is nowhere in the row.
        Assert.Equal(
            0L,
            await AdministrationApi.CountAsync(
                factory,
                "asp_net_users",
                $"id = {subject.UserId} AND password_hash LIKE '%New-Passw0rd%'",
                cancellationToken));
    }

    [Fact]
    public async Task A_refused_edit_writes_nothing_at_all()
    {
        // Both 422 rows of the matrix, and the half that is easy to miss: the name in the same body
        // must not survive the refusal, because the whole edit is one unit of work (AD-5).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Олена");
        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), admin,
            new { firstName = "Оксана", phoneNumber = "044 12" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var error = (await AdministrationApi.ReadAsync(response, cancellationToken))
                .GetProperty("error");

            Assert.Equal(
                nameof(ErrorCode.COMMON_VALIDATION_FAILED),
                error.GetProperty("code").GetString());

            // NFR-4: the key is the name the caller sent, so a form can attach it to the input.
            var message = error.GetProperty("fields").GetProperty("phoneNumber")[0].GetString();

            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain(
                nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID), message, StringComparison.Ordinal);
        }

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), admin,
            new { password = "short" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var fields = (await AdministrationApi.ReadAsync(response, cancellationToken))
                .GetProperty("error")
                .GetProperty("fields");

            Assert.True(fields.TryGetProperty("password", out _));
        }

        // Nothing written by either attempt.
        Assert.Equal("Олена", (await GetAsync(client, admin, subject.UserId, cancellationToken))
            .GetProperty("firstName").GetString());
        Assert.True(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task A_dispatcher_may_read_the_roster_and_may_not_change_it()
    {
        // The split that makes RequireRole worth having two call sites for: the same caller, the
        // same resource, one verb allowed and the other refused.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var subject = await AdministrationApi.RegisterClientAsync(
            client, cancellationToken, firstName: "Олена");
        var dispatcher = await AdministrationApi.CreateDispatcherAsync(client, admin, cancellationToken);

        var token = await AdministrationApi.SignInAsync(
            client, dispatcher.Email, AdministrationApi.Password, cancellationToken);

        using (var edit = await AdministrationApi.SendAsync(
            client, HttpMethod.Patch, Route(subject.UserId), token,
            new { firstName = "Оксана" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
            await AdministrationApi.AssertFailureAsync(edit, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using (var delete = await AdministrationApi.SendAsync(
            client, HttpMethod.Delete, Route(subject.UserId), token, body: null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }

        Assert.Equal("Олена", (await GetAsync(client, admin, subject.UserId, cancellationToken))
            .GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task Deleting_a_client_takes_the_account_and_the_reviews_and_leaves_the_deliveries()
    {
        // FR-47, and the claim no unit test can make: this is five declared foreign keys behaving,
        // not five lines of C#. It is also why the story ships no migration - the schema already
        // says all of it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var subject = await AdministrationApi.RegisterClientAsync(client, cancellationToken);
        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);

        var row = await GetAsync(client, admin, subject.UserId, cancellationToken);
        var clientId = new ClientId(row.GetProperty("clientId").GetInt32());

        int deliveryId;

        await using (var context = await factory.Database.CreateContextAsync(cancellationToken))
        {
            var delivery = await Seed.DeliveryAsync(context, cancellationToken, clientId);

            context.Reviews.Add(new Review
            {
                DeliveryId = delivery.Id,
                ClientId = clientId,
                Rating = 5,
                Text = "Вчасно і без пошкоджень.",
                CreatedAt = Seed.Instant,
            });

            await context.SaveChangesAsync(cancellationToken);

            deliveryId = delivery.Id;
        }

        using (var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Delete, Route(subject.UserId), admin, body: null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // 204 forbids a body, and the envelope stops where HTTP does (NFR-1).
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        }

        Assert.Equal(0L, await AdministrationApi.CountAsync(
            factory, "asp_net_users", $"id = {subject.UserId}", cancellationToken));
        Assert.Equal(0L, await AdministrationApi.CountAsync(
            factory, "clients", $"id = {clientId.Value}", cancellationToken));
        Assert.Equal(0L, await AdministrationApi.CountAsync(
            factory, "reviews", $"client_id = {clientId.Value}", cancellationToken));

        // The delivery survives with no client rather than being erased or making the account
        // undeletable - the failure the original shipped.
        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory, "deliveries", $"id = {deliveryId} AND client_id IS NULL", cancellationToken));

        // And the account really is gone, not merely hidden.
        Assert.False(await AdministrationApi.CanSignInAsync(
            client, subject.Email, AdministrationApi.Password, cancellationToken));
    }

    [Fact]
    public async Task The_client_surface_answers_a_non_client_id_as_absent()
    {
        // Symmetrical with the dispatcher surface: an id that is not a client's is not found, so
        // this endpoint can neither edit an administrator nor be used to discover which ids exist.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await AdministrationApi.CreateHostAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await AdministrationApi.AdminTokenAsync(client, cancellationToken);
        var adminUserId = await AdministrationApi.AdminUserIdAsync(client, cancellationToken);
        var dispatcher = await AdministrationApi.CreateDispatcherAsync(client, admin, cancellationToken);

        foreach (var id in new[] { adminUserId, dispatcher.UserId, 999_999 })
        {
            using var read = await AdministrationApi.SendAsync(
                client, HttpMethod.Get, Route(id), admin, body: null, cancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            await AdministrationApi.AssertFailureAsync(read, ErrorCode.COMMON_NOT_FOUND, cancellationToken);

            using var delete = await AdministrationApi.SendAsync(
                client, HttpMethod.Delete, Route(id), admin, body: null, cancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }

        // The administrator is still there, which is the point of the whole check.
        Assert.Equal(1L, await AdministrationApi.CountAsync(
            factory, "asp_net_users", $"id = {adminUserId}", cancellationToken));
    }

    private static string Route(int userId) =>
        "/api/clients/" + userId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A body written out by hand, because an anonymous object cannot express "this property is
    /// present and its value is null" once the serializer has been told to skip nulls - and that is
    /// precisely the request under test.
    /// </summary>
    private static async Task<HttpResponseMessage> SendRawAsync(
        HttpClient client,
        string path,
        string token,
        string json,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<JsonElement> GetAsync(
        HttpClient client,
        string token,
        int userId,
        CancellationToken cancellationToken)
    {
        using var response = await AdministrationApi.SendAsync(
            client, HttpMethod.Get, Route(userId), token, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await AdministrationApi.ReadAsync(response, cancellationToken)).GetProperty("data");
    }

    private static JsonElement Row(JsonElement rows, int userId)
    {
        foreach (var row in rows.EnumerateArray())
        {
            if (row.GetProperty("userId").GetInt32() == userId)
            {
                return row;
            }
        }

        Assert.Fail("The roster carried no row for user " + userId.ToString(CultureInfo.InvariantCulture) + ".");

        return default;
    }
}
