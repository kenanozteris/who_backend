using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Who.Application.Authentication;
using Who.Infrastructure.Authentication;
using Who.Infrastructure.Configuration;
using Who.Infrastructure.Email;
using Who.Infrastructure.Identity;
using Who.Infrastructure.Persistence;

namespace Who.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddWhoInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) => options.ConnectionString = DatabaseConfiguration.Resolve(configuration)).ValidateOnStart();
        services.AddOptions<AuthOptions>().Configure<IConfiguration>((options, configuration) => options.Load(configuration)).ValidateOnStart();
        services.AddDbContext<WhoDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
        services.TryAddSingleton(TimeProvider.System);
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.User.AllowedUserNameCharacters = string.Empty; // Identity username is an email, not the WHO handle.
            options.Password.RequiredLength = 10; options.Password.RequiredUniqueChars = 1;
            options.Password.RequireDigit = false; options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false; options.Password.RequireNonAlphanumeric = false;
        }).AddEntityFrameworkStores<WhoDbContext>();
        // Policy is length-only, including spaces; retain the unmodified Identity password hasher.
        services.RemoveAll<IPasswordValidator<ApplicationUser>>();
        services.AddScoped<IPasswordValidator<ApplicationUser>, PasswordLengthValidator>();
        services.AddScoped<JwtTokens>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IVerificationEmailSender, MailpitVerificationEmailSender>();
        return services;
    }
}
