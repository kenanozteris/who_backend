using System.Threading.RateLimiting;

namespace Who.Api.Authentication;

public static class AuthRateLimits
{
    public static IServiceCollection AddWhoRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        var defaults = new Dictionary<string, int> { ["register"] = 5, ["login"] = 10, ["verify"] = 20, ["resend"] = 5, ["refresh"] = 30 };
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await Results.Problem(statusCode: 429, title: "Too many requests.", extensions: new Dictionary<string, object?>
                { ["code"] = "RATE_LIMIT_EXCEEDED" }).ExecuteAsync(context.HttpContext);
            };
            foreach (var (policy, defaultLimit) in defaults)
            {
                var value = configuration[$"WHO_AUTH_RATE_{policy.ToUpperInvariant()}"];
                var limit = value is null ? defaultLimit : int.TryParse(value, out var parsed) && parsed > 0 && parsed <= 10000
                    ? parsed : throw new InvalidOperationException($"Invalid rate limit configuration for {policy}.");
                options.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new()
                    { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            }
        });
        return services;
    }
}
