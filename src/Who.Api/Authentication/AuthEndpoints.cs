using Who.Application.Authentication;

namespace Who.Api.Authentication;

public static class AuthEndpoints
{
    private static Guid UserId(HttpContext context) => Guid.Parse(context.User.FindFirst("sub")!.Value);
    private static Guid SessionId(HttpContext context) => Guid.Parse(context.User.FindFirst("sid")!.Value);
    private static RequestMetadata Metadata(HttpContext context) => new(context.Connection.RemoteIpAddress?.ToString(), context.Request.Headers.UserAgent.ToString());

    public static void MapWhoAuth(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Auth");
        auth.MapPost("/register", async (RegisterRequest request, IAuthService service, CancellationToken ct) =>
            Results.Json(await service.RegisterAsync(request, ct), statusCode: 202))
            .AllowAnonymous().RequireRateLimiting("register").Produces<PendingRegistrationResponse>(202).ProducesProblem(400).ProducesProblem(409);
        auth.MapPost("/verify-email", async (VerifyEmailRequest request, IAuthService service, CancellationToken ct) =>
            Results.Ok(await service.VerifyAsync(request, ct)))
            .AllowAnonymous().RequireRateLimiting("verify").Produces<VerificationResponse>().ProducesProblem(400).ProducesProblem(429);
        auth.MapPost("/resend-verification", async (ResendVerificationRequest request, IAuthService service, CancellationToken ct) =>
            Results.Json(await service.ResendAsync(request.RegistrationId, ct), statusCode: 202))
            .AllowAnonymous().RequireRateLimiting("resend").Produces<PendingRegistrationResponse>(202).ProducesProblem(429);
        auth.MapPost("/login", async (LoginRequest request, HttpContext context, IAuthService service, CancellationToken ct) =>
            Results.Ok(await service.LoginAsync(request, Metadata(context), ct)))
            .AllowAnonymous().RequireRateLimiting("login").Produces<TokenResponse>().ProducesProblem(401).ProducesProblem(409);
        auth.MapPost("/refresh", async (RefreshRequest request, IAuthService service, CancellationToken ct) =>
            Results.Ok(await service.RefreshAsync(request.RefreshToken, ct)))
            .AllowAnonymous().RequireRateLimiting("refresh").Produces<TokenResponse>().ProducesProblem(401);
        auth.MapPost("/logout", async (HttpContext context, IAuthService service, CancellationToken ct) =>
        {
            await service.LogoutAsync(UserId(context), SessionId(context), false, ct); return Results.NoContent();
        }).RequireAuthorization("WhoAccess").Produces(204).ProducesProblem(401).ProducesProblem(403);
        auth.MapPost("/logout-all", async (HttpContext context, IAuthService service, CancellationToken ct) =>
        {
            await service.LogoutAsync(UserId(context), SessionId(context), true, ct); return Results.NoContent();
        }).RequireAuthorization("WhoAccess").Produces(204).ProducesProblem(401).ProducesProblem(403);
        app.MapPost("/api/v1/onboarding/privacy", async (PrivacyOnboardingRequest request, HttpContext context, IAuthService service, CancellationToken ct) =>
            Results.Ok(await service.CompleteOnboardingAsync(UserId(context), request.AccountVisibility, Metadata(context), ct)))
            .WithTags("Onboarding").RequireAuthorization("WhoOnboarding").Produces<TokenResponse>().ProducesProblem(400).ProducesProblem(409);
        app.MapGet("/api/v1/me", async (HttpContext context, IAuthService service, CancellationToken ct) =>
            Results.Ok(await service.MeAsync(UserId(context), ct)))
            .WithTags("Current user").RequireAuthorization("WhoAccess").Produces<CurrentUserDto>().ProducesProblem(401).ProducesProblem(403);
    }
}
