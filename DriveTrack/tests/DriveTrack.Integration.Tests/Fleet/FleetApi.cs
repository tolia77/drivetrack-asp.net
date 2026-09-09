using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Fleet;

/// <summary>
/// The plumbing the two fleet suites share: a host with the real schemes, a bearer token for a
/// caller of a chosen role, and the reading of the one envelope every response wears.
/// <para>
/// Collected here rather than repeated because the interesting half of these suites is the
/// authorization matrix, and that matrix is only meaningful if every role's token is obtained the
/// same way — through the production sign-in endpoint, against an account the production Identity
/// port created.
/// </para>
/// </summary>
internal static class FleetApi
{
    /// <summary>A password comfortably over Identity's configured policy.</summary>
    public const string Password = "Passw0rd-Test";

    /// <summary>
    /// A host whose default scheme is the production path selector rather than the probe: the
    /// fleet's whole refusal story is about real bearer tokens carrying a real role claim.
    /// </summary>
    public static Task<ApiFactory> CreateAsync(string connectionString, CancellationToken cancellationToken) =>
        ApiFactory.CreateAsync(connectionString, cancellationToken, useProbeAuthentication: false);

    /// <summary>An address no other test has used.</summary>
    public static string UniqueEmail() => Guid.NewGuid().ToString("N")[..12] + "@drivetrack.test";

    /// <summary>A plate no other test has used, inside the column's twenty characters.</summary>
    public static string UniquePlate() => "AA" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>
    /// A bearer token for a caller of that role.
    /// <para>
    /// The admin is the seeded one (FR-9). A dispatcher has no registration endpoint — the product
    /// deliberately ships none — so the account is created through the same Identity port the
    /// application uses and then signed in over HTTP, which is what makes the token real rather
    /// than hand-assembled.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="role"/> is <see cref="UserRole.Driver"/>. A driver is not an account with a
    /// role on it: it is an account <em>and</em> a <c>drivers</c> row, and the generic path here
    /// would create the first without the second — an account holding role <c>Driver</c> with
    /// nothing behind it, which is the orphan state <c>IUserAccountRepository.DeleteAsync</c>'s own
    /// comment calls worse than either half. A test that wants one asks the fleet capability to
    /// take a driver on, which is what <c>DriverTokenAsync</c> does.
    /// </exception>
    public static async Task<string> TokenAsync(
        ApiFactory factory,
        HttpClient client,
        UserRole role,
        CancellationToken cancellationToken)
    {
        if (role == UserRole.Driver)
        {
            throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "A driver account cannot be created here: it would hold role Driver with no drivers "
                    + "row behind it. Create one through POST /api/drivers - see DriverTokenAsync.");
        }

        if (role == UserRole.Admin)
        {
            return await SignInAsync(
                client,
                TestConfiguration.AdminEmail,
                TestConfiguration.AdminPassword,
                cancellationToken);
        }

        if (role == UserRole.Client)
        {
            var clientEmail = UniqueEmail();

            using (var registration = await client.PostAsJsonAsync(
                       new Uri("/api/auth/register", UriKind.Relative),
                       new
                       {
                           firstName = "Олена",
                           lastName = "Петренко",
                           email = clientEmail,
                           phoneNumber = "+380441234567",
                           password = Password,
                           passwordConfirmation = Password,
                       },
                       cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
            }

            return await SignInAsync(client, clientEmail, Password, cancellationToken);
        }

        var email = UniqueEmail();

        await using (var unitOfWork = await factory.Database.UnitOfWorkFactory
                         .CreateAsync(cancellationToken))
        {
            await unitOfWork.Users.CreateAsync(
                new NewUserAccount("Диспетчер", "Тестовий", email),
                Password,
                role,
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        return await SignInAsync(client, email, Password, cancellationToken);
    }

    /// <summary>Exchanges credentials for a token through the production endpoint.</summary>
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

    /// <summary>One request, with an optional bearer token and an optional JSON body.</summary>
    /// <remarks>
    /// The caller disposes the response. The request message is disposed here because nothing in
    /// the response depends on it.
    /// </remarks>
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
    /// The envelope, parsed.
    /// </summary>
    /// <remarks>
    /// Read as a string and parsed rather than deserialized straight off the response stream: the
    /// buffered string can be read again, and several assertions here look at one response twice —
    /// once for its code and once for the message that code resolved to.
    /// </remarks>
    public static async Task<JsonElement> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(json), "The response carried no body.");

        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    /// <summary>Asserts the response is the failure envelope for that code, at the status AD-7 assigns it.</summary>
    public static async Task AssertFailureAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        ErrorCode expectedCode,
        CancellationToken cancellationToken)
    {
        Assert.Equal(expectedStatus, response.StatusCode);

        var envelope = await ReadAsync(response, cancellationToken);

        Assert.False(envelope.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("data").ValueKind);

        var error = envelope.GetProperty("error");

        Assert.Equal(expectedCode.ToString(), error.GetProperty("code").GetString());

        var message = error.GetProperty("message").GetString();

        Assert.False(string.IsNullOrWhiteSpace(message));

        // NFR-3: the wire message is the localized string for the code and nothing else, so no
        // SQLSTATE, constraint name or stack frame has a path to a client.
        Assert.DoesNotContain("SQLSTATE", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("23505", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ix_", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(expectedCode.ToString(), message, StringComparison.Ordinal);
    }

    /// <summary>The success payload of a response that must have succeeded.</summary>
    public static async Task<JsonElement> DataAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var envelope = await ReadAsync(response, cancellationToken);

        Assert.True(
            envelope.GetProperty("success").GetBoolean(),
            "Expected a success envelope, got: " + envelope.ToString());

        return envelope.GetProperty("data");
    }
}
