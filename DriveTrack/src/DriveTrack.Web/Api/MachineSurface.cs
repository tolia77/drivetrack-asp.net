using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace DriveTrack.Web.Api;

/// <summary>
/// Which paths belong to the machine-readable surface, and which credential a request on one
/// carries. One decision, four call sites in the composition root.
/// <para>
/// Until story 6.1 there was exactly one such path prefix — <c>/api</c> — and it was a
/// <c>const string</c> in <c>Program.cs</c> repeated in four places: the scheme selector, the two
/// cookie redirect events and the envelope branch. A second prefix arriving as four separate edits
/// is how three of them get made and the fourth does not, and the one that gets missed is silent: an
/// unauthenticated <c>&lt;img&gt;</c> request would receive the HTML sign-in page at status 200, and
/// the browser would render a broken image with no explanation anywhere.
/// </para>
/// <para>
/// <b>Why the asset route is not simply under <c>/api</c>.</b> AD-22 makes <c>/api/*</c> bearer-only,
/// and the Blazor circuit holds no token — it holds a cookie. An <c>&lt;img src&gt;</c> on a proof
/// screen is a browser request with that cookie and nothing else, so it could never authenticate
/// there. Putting the route outside <c>/api</c> and choosing the scheme by the presence of an
/// <c>Authorization: Bearer</c> header is what lets the same URL serve a driver's browser and a REST
/// client, which is what "accepts either scheme" has to mean.
/// </para>
/// </summary>
internal static class MachineSurface
{
    /// <summary>The REST surface (AD-22): bearer tokens only.</summary>
    public const string ApiPrefix = "/api";

    /// <summary>
    /// The proof-asset route: <c>GET /proof-assets/{id}</c>, which answers image bytes rather than
    /// an envelope and takes either credential.
    /// </summary>
    public const string ProofAssetPrefix = "/proof-assets";

    /// <summary>
    /// Whether a failure on this path leaves as the JSON envelope rather than as an HTML page.
    /// <para>
    /// True for both prefixes, and that is the whole reason this predicate exists rather than a
    /// second <c>StartsWithSegments</c> beside the first. The asset route's <em>successes</em> are
    /// raw bytes with no envelope at all — it is a minimal-API endpoint, so no result filter ever
    /// sees them — but its <em>failures</em> must be structured: a 401 there has to be a JSON body a
    /// caller can read, never a redirect to <c>/sign-in</c> answered at 200 with a page of HTML
    /// where an image was expected.
    /// </para>
    /// </summary>
    /// <param name="path">The request path.</param>
    public static bool WantsEnvelope(PathString path) =>
        path.StartsWithSegments(ApiPrefix) || path.StartsWithSegments(ProofAssetPrefix);

    /// <summary>
    /// Whether this request should be authenticated as a bearer token rather than as a cookie.
    /// <para>
    /// On <c>/api</c> the answer is the path, unconditionally: AD-22 says a cookie presented there
    /// is never even looked at. On the asset prefix the answer is the header, because the route
    /// serves two audiences and the credential the caller actually presented is the only honest way
    /// to tell them apart — a browser sends a cookie and no <c>Authorization</c> header, an API
    /// client sends the header.
    /// </para>
    /// </summary>
    /// <param name="request">The request.</param>
    public static bool PrefersBearer(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Path.StartsWithSegments(ApiPrefix))
        {
            return true;
        }

        return request.Path.StartsWithSegments(ProofAssetPrefix)
            && request.Headers.TryGetValue(HeaderNames.Authorization, out var authorization)
            && authorization.Any(value =>
                value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true);
    }
}
