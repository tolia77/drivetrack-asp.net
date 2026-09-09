using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Identity;

/// <summary>
/// The moves both administration suites make: boot a host on the real schemes, get a bearer token
/// for a caller of a given kind, and read the envelope back.
/// <para>
/// Collected here rather than repeated per class for the same reason <c>SharedMarkup</c> exists:
/// every one of these is a place a test could quietly stop asserting anything — a host built with
/// the probe scheme instead of the real one would make every authorization assertion below a test
/// of a stub.
/// </para>
/// </summary>
internal static class AdministrationApi
{
    /// <summary>The password every account these suites open is given.</summary>
    public const string Password = "Passw0rd-Test";

    /// <summary>
    /// A host whose default scheme is the production path selector, not the probe. Authorization is
    /// what these suites are about, so opting out of the probe is the whole point.
    /// </summary>
    public static Task<ApiFactory> CreateHostAsync(
        string connectionString,
        CancellationToken cancellationToken) =>
        ApiFactory.CreateAsync(connectionString, cancellationToken, useProbeAuthentication: false);

    /// <summary>A unique address, so parallel suites on one container never collide.</summary>
    public static string UniqueEmail() => Guid.NewGuid().ToString("N")[..12] + "@drivetrack.test";

    /// <summary>Signs in and returns the bearer token, failing loudly if the credentials are refused.</summary>
    public static async Task<string> SignInAsync(
        HttpClient client,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/sign-in", UriKind.Relative),
            new { email, password },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await ReadAsync(response, cancellationToken))
            .GetProperty("data")
            .GetProperty("accessToken")
            .GetString()!;
    }

    /// <summary>Whether those credentials are accepted, without asserting either way (FR-8, FR-50).</summary>
    public static async Task<bool> CanSignInAsync(
        HttpClient client,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/sign-in", UriKind.Relative),
            new { email, password },
            cancellationToken);

        return response.StatusCode == HttpStatusCode.OK;
    }

    /// <summary>The seeded administrator's token (FR-9).</summary>
    public static Task<string> AdminTokenAsync(HttpClient client, CancellationToken cancellationToken) =>
        SignInAsync(client, TestConfiguration.AdminEmail, TestConfiguration.AdminPassword, cancellationToken);

    /// <summary>The seeded administrator's own user id, which is a perfectly good non-client id.</summary>
    public static async Task<int> AdminUserIdAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/sign-in", UriKind.Relative),
            new { email = TestConfiguration.AdminEmail, password = TestConfiguration.AdminPassword },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await ReadAsync(response, cancellationToken))
            .GetProperty("data")
            .GetProperty("userId")
            .GetInt32();
    }

    /// <summary>Opens a client account through the public registration endpoint (FR-1).</summary>
    public static async Task<(int UserId, string Email)> RegisterClientAsync(
        HttpClient client,
        CancellationToken cancellationToken,
        string firstName = "Олена",
        string lastName = "Петренко",
        string phoneNumber = "+380441234567")
    {
        var email = UniqueEmail();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new
            {
                firstName,
                lastName,
                email,
                phoneNumber,
                password = Password,
                passwordConfirmation = Password,
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = (await ReadAsync(response, cancellationToken)).GetProperty("data");

        return (session.GetProperty("userId").GetInt32(), email);
    }

    /// <summary>
    /// Opens a dispatcher account through the endpoint this story ships, because there is no other
    /// way to make one - which is itself half of FR-49.
    /// </summary>
    public static async Task<(int UserId, string Email)> CreateDispatcherAsync(
        HttpClient client,
        string adminToken,
        CancellationToken cancellationToken,
        string firstName = "Ігор",
        string lastName = "Ковальчук")
    {
        var email = UniqueEmail();

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            "/api/dispatchers",
            adminToken,
            new { firstName, lastName, email, password = Password },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var account = (await ReadAsync(response, cancellationToken)).GetProperty("data");

        return (account.GetProperty("userId").GetInt32(), email);
    }

    /// <summary>One request, with an optional bearer token and an optional JSON body.</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? token,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// The envelope, parsed. Read as a string and parsed rather than deserialized off the stream,
    /// so a test that wants both the shape and the raw text - "the refusal disclosed nothing" is
    /// exactly that claim - can ask twice without the second read finding a closed stream.
    /// </summary>
    public static async Task<JsonElement> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        JsonDocument.Parse(await BodyAsync(response, cancellationToken)).RootElement;

    /// <summary>The response body as text.</summary>
    public static Task<string> BodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Asserts the failure envelope: the code, and a message that is not the code.</summary>
    public static async Task AssertFailureAsync(
        HttpResponseMessage response,
        ErrorCode expected,
        CancellationToken cancellationToken)
    {
        var envelope = await ReadAsync(response, cancellationToken);

        Assert.False(envelope.GetProperty("success").GetBoolean());

        var error = envelope.GetProperty("error");

        Assert.Equal(expected.ToString(), error.GetProperty("code").GetString());

        // NFR-14: what arrives is the Ukrainian sentence, not the code the service named.
        var message = error.GetProperty("message").GetString();

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain(expected.ToString(), message, StringComparison.Ordinal);
    }

    /// <summary>A row count outside EF, for proving what the schema itself did.</summary>
    public static async Task<long> CountAsync(
        ApiFactory factory,
        string table,
        string predicate,
        CancellationToken cancellationToken)
    {
        var value = await factory.Database.ScalarAsync(
            $"SELECT COUNT(*) FROM {table} WHERE {predicate}",
            cancellationToken);

        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
