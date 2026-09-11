using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Deliveries;

/// <summary>
/// The arrangement the two proof suites share: a multipart capture, a proof read, and the one thing
/// no other helper here can supply — a real session cookie.
/// <para>
/// A capture is <c>multipart/form-data</c> rather than JSON, so <see cref="FleetApi.SendAsync"/>
/// cannot post one: it builds a JSON body. The rest of the plumbing is reused unchanged — the
/// envelope reading, the failure assertion, the tokens — because the point of these suites is which
/// caller may do what, and that is only meaningful if every role's credential is obtained the way
/// the product issues it.
/// </para>
/// </summary>
internal static class ProofApi
{
    /// <summary>A one-pixel PNG, small enough to inline and real enough to be a file.</summary>
    /// <remarks>
    /// The bytes matter in exactly one place: the asset route answers them back, and a round-trip
    /// assertion needs something it can compare. Everywhere else the store is a fake and any bytes
    /// would do.
    /// </remarks>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>A handful of bytes standing in for a photograph.</summary>
    public static readonly byte[] Jpeg = Encoding.UTF8.GetBytes("не справжній знімок, але байти");

    /// <summary>
    /// Posts a capture. The response is handed back rather than asserted, because most of this
    /// story's matrix is about which refusal a caller gets.
    /// </summary>
    /// <param name="client">The API client.</param>
    /// <param name="token">A bearer token, or null for an anonymous request.</param>
    /// <param name="deliveryId">The delivery being proved.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <param name="recipientName">Who took the parcel, or null to omit the field.</param>
    /// <param name="latitude">The capture latitude, or null to omit it.</param>
    /// <param name="longitude">The capture longitude, or null to omit it.</param>
    /// <param name="signature">The signature bytes, or null to send no signature at all.</param>
    /// <param name="photos">The photographs, or null for one ordinary one.</param>
    /// <param name="photoContentType">The type every photograph declares.</param>
    public static async Task<HttpResponseMessage> CaptureAsync(
        HttpClient client,
        string? token,
        int deliveryId,
        CancellationToken cancellationToken,
        string? recipientName = "Олена Петренко",
        double? latitude = 50.4501,
        double? longitude = 30.5234,
        byte[]? signature = null,
        IReadOnlyList<byte[]>? photos = null,
        string photoContentType = "image/jpeg")
    {
        // Disposed with the request below rather than with a using of its own: MultipartFormDataContent
        // owns the parts, and disposing it before SendAsync has read them would close the streams the
        // request is about to write.
        var content = new MultipartFormDataContent();

        if (recipientName is not null)
        {
            content.Add(new StringContent(recipientName, Encoding.UTF8), "RecipientName");
        }

        if (latitude is { } capturedLatitude)
        {
            content.Add(Number(capturedLatitude), "Latitude");
        }

        if (longitude is { } capturedLongitude)
        {
            content.Add(Number(capturedLongitude), "Longitude");
        }

        if (signature is not null)
        {
            content.Add(File(signature, "image/png"), "Signature", "signature.png");
        }

        foreach (var photo in photos ?? [Jpeg])
        {
            content.Add(File(photo, photoContentType), "Photos", "photo.jpg");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/deliveries/{deliveryId}/proof", UriKind.Relative))
        {
            Content = content,
        };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>Posts the ordinary, legal capture and fails loudly if it was refused.</summary>
    public static async Task CaptureOkAsync(
        HttpClient client,
        string token,
        int deliveryId,
        CancellationToken cancellationToken)
    {
        using var response = await CaptureAsync(
            client,
            token,
            deliveryId,
            cancellationToken,
            signature: Png);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Reads a delivery's proof (FR-122).</summary>
    public static Task<HttpResponseMessage> ReadAsync(
        HttpClient client,
        string? token,
        int deliveryId,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client,
            HttpMethod.Get,
            $"/api/deliveries/{deliveryId}/proof",
            token,
            body: null,
            cancellationToken);

    /// <summary>Fetches one asset's bytes from the route an <c>&lt;img&gt;</c> points at.</summary>
    /// <param name="client">The API client.</param>
    /// <param name="assetId">The asset row's id.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <param name="token">A bearer token, or null to present none.</param>
    /// <param name="cookie">A session cookie, or null to present none.</param>
    public static async Task<HttpResponseMessage> AssetAsync(
        HttpClient client,
        int assetId,
        CancellationToken cancellationToken,
        string? token = null,
        string? cookie = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/proof-assets/{assetId}", UriKind.Relative));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Signs in through the statically rendered form and returns the session cookie it issued.
    /// <para>
    /// The form rather than <c>/api/auth/sign-in</c>, because the two issue different credentials:
    /// the REST endpoint answers a bearer token and this is the only path in the product that writes
    /// <c>Set-Cookie</c>. The asset route's whole reason for existing outside <c>/api</c> is that it
    /// accepts this credential as well as the other one, and a test that faked the cookie would be
    /// asserting against its own fake.
    /// </para>
    /// </summary>
    public static async Task<string> CookieAsync(
        ApiFactory factory,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

        using var page = await client.GetAsync(new Uri("/sign-in", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var html = await page.Content.ReadAsStringAsync(cancellationToken);

        var antiforgery = page.Headers.TryGetValues("Set-Cookie", out var issued)
            ? string.Join("; ", issued.Select(value => value.Split(';')[0]))
            : string.Empty;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Form.Email"] = email,
            ["Form.Password"] = password,
            ["_handler"] = "signIn",
            ["__RequestVerificationToken"] = AntiforgeryToken(html),
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/sign-in", UriKind.Relative))
        {
            Content = form,
        };

        if (antiforgery.Length > 0)
        {
            request.Headers.Add("Cookie", antiforgery);
        }

        using var response = await client.SendAsync(request, cancellationToken);

        var session = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(value =>
                value.StartsWith("drivetrack.session=", StringComparison.Ordinal))
            : null;

        Assert.True(
            session is not null,
            "The sign-in form issued no session cookie; the asset route's cookie half cannot be "
                + "asserted without one.");

        return session.Split(';')[0];
    }

    /// <summary>
    /// Invariantly formatted, and that is load-bearing: the host runs under uk-UA, which writes
    /// 50,4501 - and a comma in a form field is a value the binder reads as something else entirely.
    /// </summary>
    private static StringContent Number(double value) =>
        new(value.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);

    private static ByteArrayContent File(byte[] bytes, string contentType)
    {
        var part = new ByteArrayContent(bytes);

        // Set on the part rather than left to the framework: the declared type is what the validator
        // judges (NFR-28), so a test about a refused type has to be able to declare one.
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        return part;
    }

    private static string AntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, "The sign-in form did not render an antiforgery token.");

        return match.Groups[1].Value;
    }
}
