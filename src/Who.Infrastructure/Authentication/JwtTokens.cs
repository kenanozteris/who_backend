using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Who.Infrastructure.Authentication;

public sealed class JwtTokens(IOptions<AuthOptions> options, TimeProvider clock)
{
    public string Onboarding(Guid userId) => Issue(userId, null);
    public string Access(Guid userId, Guid sessionId) => Issue(userId, sessionId);

    private string Issue(Guid userId, Guid? sessionId)
    {
        var now = clock.GetUtcNow();
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()), new("jti", Guid.NewGuid().ToString()),
            new("typ", sessionId is null ? "onboarding" : "access"),
            new("iat", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        };
        if (sessionId is { } sid) claims.Add(new("sid", sid.ToString()));
        var settings = options.Value;
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience, claims, now.UtcDateTime,
            now.AddSeconds(AuthOptions.AccessLifetimeSeconds).UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(settings.SigningKey), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
