using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Who.Infrastructure.Configuration;
using Who.Infrastructure.Persistence;

namespace Who.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddWhoInfrastructure(this IServiceCollection services)
    {
        // Resolve after all configuration providers (including test overrides).
        // ValidateOnStart fails before HTTP requests, without requiring DB uptime.
        services.AddOptions<DatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = DatabaseConfiguration.Resolve(configuration))
            .ValidateOnStart();
        services.AddDbContext<WhoDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
        return services;
    }
}
