using DriveTrack.Application.Common;
using Microsoft.AspNetCore.Http;

namespace DriveTrack.Web.Api;

/// <summary>
/// AD-7's third suppression: the challenge and forbid writers.
/// <para>
/// Adapter-level authorization short-circuits before any exception middleware runs, and a cookie
/// handler's default challenge is a <em>302 to a login path</em> - not a 401, so it never trips the
/// "status of 400 or above with an empty body" backstop either. Left alone, a REST caller without
/// credentials gets a redirect to HTML, which is precisely the second wire shape NFR-1 forbids.
/// </para>
/// <para>
/// Both methods are scheme-agnostic and take only the context, so Epic 2 attaches the same two to
/// both schemes without either owning a copy of the shape. They are also <em>path</em>-agnostic,
/// which is the part that needs care: the JWT handler serves only REST, so its <c>OnChallenge</c>
/// and <c>OnForbidden</c> call these unconditionally, but the cookie handler also serves the Blazor
/// shell, where a browser navigating to a protected page must still be redirected to sign in.
/// Its <c>OnRedirectToLogin</c>/<c>OnRedirectToAccessDenied</c> must therefore branch on
/// <c>context.Request.Path.StartsWithSegments("/api")</c> - envelope on an API path, the default
/// redirect otherwise - or every protected page answers a browser with JSON.
/// </para>
/// </summary>
internal static class EnvelopeAuthenticationEvents
{
    /// <summary>
    /// Writes the 401 envelope. FR-13's session-expiry flow branches on this single code, which is
    /// why every scheme answers an unauthenticated request with the same one.
    /// </summary>
    public static Task WriteChallengeAsync(HttpContext context) =>
        EnvelopeWriter.WriteAsync(context, ErrorCode.AUTH_UNAUTHENTICATED);

    /// <summary>Writes the 403 envelope for a principal that failed the endpoint's policy.</summary>
    public static Task WriteForbiddenAsync(HttpContext context) =>
        EnvelopeWriter.WriteAsync(context, ErrorCode.AUTH_FORBIDDEN);
}
