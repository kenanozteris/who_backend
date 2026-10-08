using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Who.Infrastructure.Authentication;

public static class AuthCryptography
{
    public static string GenerateCode() => RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    public static bool IsCode(string? value) => value is { Length: 6 } && value.All(c => c is >= '0' and <= '9');
    public static string CodeHash(byte[] pepper, Guid challengeId, Guid userId, string code) =>
        Convert.ToHexString(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes($"email-verification:{challengeId:N}:{userId:N}:{code}")));
    public static bool CodeMatches(byte[] pepper, Guid id, Guid userId, string? code, string hash) =>
        IsCode(code) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(CodeHash(pepper, id, userId, code!)));
    public static string GenerateRefresh() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    public static bool IsRefresh(string? token) => token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static string RefreshHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
