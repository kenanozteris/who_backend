using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Who.Application.Authentication;
using Who.Infrastructure.Authentication;

namespace Who.Api.Authentication;

public sealed class AccessRequirement : IAuthorizationRequirement { }
public sealed class AccessHandler(IAuthService auth) : AuthorizationHandler<AccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AccessRequirement requirement)
    {
        if (context.User.FindFirst("typ")?.Value != "access" ||
            !Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId) ||
            !Guid.TryParse(context.User.FindFirst("sid")?.Value, out var sessionId)) return;
        var cancellationToken = context.Resource is HttpContext http ? http.RequestAborted : CancellationToken.None;
        if (await auth.IsAccessAllowedAsync(userId, sessionId, cancellationToken)) context.Succeed(requirement);
    }
}

public sealed class ConfigureJwt(IOptions<AuthOptions> authOptions, TimeProvider clock) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);
    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme) return;
        var settings = authOptions.Value;
        options.MapInboundClaims = false; options.SaveToken = false; options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new()
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(settings.SigningKey),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = (notBefore, expires, _, _) => expires.HasValue &&
                (!notBefore.HasValue || notBefore <= clock.GetUtcNow().UtcDateTime) && expires > clock.GetUtcNow().UtcDateTime
        };
    }
}

public static class AuthAuthorization
{
    public static IServiceCollection AddWhoAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwt>();
        services.AddScoped<IAuthorizationHandler, AccessHandler>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("WhoAccess", policy => policy.RequireAuthenticatedUser().AddRequirements(new AccessRequirement()));
            options.DefaultPolicy = options.GetPolicy("WhoAccess")!;
            options.FallbackPolicy = options.DefaultPolicy;
            options.AddPolicy("WhoOnboarding", policy => policy.RequireAuthenticatedUser().RequireClaim("typ", "onboarding")
                .RequireAssertion(c => Guid.TryParse(c.User.FindFirst("sub")?.Value, out _)));
        });
        return services;
    }
}
