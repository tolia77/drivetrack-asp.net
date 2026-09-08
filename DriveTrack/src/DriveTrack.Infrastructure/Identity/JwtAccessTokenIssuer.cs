using System.Globalization;
using System.Security.Claims;
using System.Text;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DriveTrack.Infrastructure.Identity;

/// <summary>
/// Signs the bearer token the REST adapter accepts (FR-4).
/// <para>
/// The claim <em>names</em> come from <see cref="DriveTrackClaimTypes"/>, which the cookie scheme
/// writes too: a caller is the same <c>ICurrentUser</c> whichever credential carried them.
/// </para>
/// <para>
/// AD-13: the expiry is read from the injected <see cref="TimeProvider"/>, never from the ambient
/// clock, so a token-lifetime rule can be tested by moving a fake clock rather than by waiting.
/// </para>
/// </summary>
internal sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider)
    : IAccessTokenIssuer
{
    private readonly JwtOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    /// <inheritdoc />
    public string Issue(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var issuedAt = timeProvider.GetUtcNow();

        var claims = new List<Claim>
        {
            new(DriveTrackClaimTypes.UserId, account.Id.Value.ToString(CultureInfo.InvariantCulture)),
            new(DriveTrackClaimTypes.Role, account.Role.ToString()),
            new(DriveTrackClaimTypes.Name, account.FirstName + " " + account.LastName),
            new(DriveTrackClaimTypes.Email, account.Email),
        };

        // The subtype id travels only when there is one. An empty claim would be a value every
        // reader has to remember to treat as absent, which is the kind of thing readers forget.
        if (account.DriverId is { } driverId)
        {
            claims.Add(new Claim(
                DriveTrackClaimTypes.DriverId,
                driverId.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (account.ClientId is { } clientId)
        {
            claims.Add(new Claim(
                DriveTrackClaimTypes.ClientId,
                clientId.Value.ToString(CultureInfo.InvariantCulture)));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = issuedAt.AddMinutes(_options.LifetimeMinutes).UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        return _handler.CreateToken(descriptor);
    }
}
