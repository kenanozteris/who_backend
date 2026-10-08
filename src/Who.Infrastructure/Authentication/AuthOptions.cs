using Microsoft.Extensions.Configuration;

namespace Who.Infrastructure.Authentication;

public sealed class AuthOptions
{
    public byte[] VerificationPepper { get; set; } = [];
    public byte[] SigningKey { get; set; } = [];
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public const int AccessLifetimeSeconds = 900;
    public const int OnboardingLifetimeSeconds = 900;

    public void Load(IConfiguration config)
    {
        VerificationPepper = Secret(config, "WHO_AUTH_VERIFICATION_PEPPER");
        SigningKey = Secret(config, "WHO_AUTH_JWT_SIGNING_KEY");
        Issuer = Required(config, "WHO_AUTH_JWT_ISSUER");
        Audience = Required(config, "WHO_AUTH_JWT_AUDIENCE");
    }

    private static string Required(IConfiguration config, string key) =>
        !string.IsNullOrWhiteSpace(config[key]) ? config[key]! : throw new InvalidOperationException($"Required auth configuration: {key}.");

    private static byte[] Secret(IConfiguration config, string key)
    {
        byte[] value;
        try { value = Convert.FromBase64String(Required(config, key)); }
        catch (FormatException) { throw new InvalidOperationException($"{key} must be base64 with at least 32 decoded bytes."); }
        if (value.Length < 32) throw new InvalidOperationException($"{key} must contain at least 32 decoded bytes.");
        return value;
    }
}
