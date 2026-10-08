namespace Who.Application.Authentication;

public sealed record RegisterRequest(string? BirthDate, string? Username, string? DisplayName, string? Email, string? Password);
public sealed record VerifyEmailRequest(Guid RegistrationId, string? Code);
public sealed record ResendVerificationRequest(Guid RegistrationId);
public sealed record PrivacyOnboardingRequest(string? AccountVisibility);
public sealed record DeviceMetadata(string? Platform, string? DeviceName, string? AppVersion);
public sealed record LoginRequest(string? Email, string? Password, DeviceMetadata? Device = null);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record RequestMetadata(string? IpAddress, string? UserAgent);
public sealed record PendingRegistrationResponse(Guid RegistrationId, bool VerificationRequired, int ExpiresInSeconds,
    int ResendAvailableInSeconds, string EmailDeliveryStatus, string? Code = null);
public sealed record VerificationResponse(bool EmailVerified, bool OnboardingRequired, string OnboardingToken, int ExpiresIn);
public sealed record PrivacyDto(string AccountVisibility, string FollowPermission, bool Searchable, bool RecommendationsEnabled,
    string ProfilePollVisibility, string SocialListVisibility);
public sealed record CurrentUserDto(Guid Id, string Email, string Username, string DisplayName, DateOnly BirthDate,
    string AccountType, string AccountStatus, bool OnboardingCompleted, PrivacyDto Privacy);
public sealed record AuthUserDto(Guid Id, string Email, string Username, string DisplayName,
    string AccountType, string AccountStatus, bool OnboardingCompleted, PrivacyDto Privacy);
public sealed record TokenResponse(string AccessToken, string RefreshToken, int AccessTokenExpiresInSeconds, AuthUserDto User);

public sealed class AuthException(string code, int status = 400, IReadOnlyDictionary<string, object?>? data = null) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public IReadOnlyDictionary<string, object?> DataFields { get; } = data ?? new Dictionary<string, object?>();
}

public interface IVerificationEmailSender
{
    Task SendAsync(string email, string code, CancellationToken cancellationToken);
}

public interface IAuthService
{
    Task<PendingRegistrationResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<VerificationResponse> VerifyAsync(VerifyEmailRequest request, CancellationToken cancellationToken);
    Task<PendingRegistrationResponse> ResendAsync(Guid registrationId, CancellationToken cancellationToken);
    Task<TokenResponse> CompleteOnboardingAsync(Guid userId, string? visibility, RequestMetadata metadata, CancellationToken cancellationToken);
    Task<TokenResponse> LoginAsync(LoginRequest request, RequestMetadata metadata, CancellationToken cancellationToken);
    Task<TokenResponse> RefreshAsync(string? token, CancellationToken cancellationToken);
    Task LogoutAsync(Guid userId, Guid sessionId, bool all, CancellationToken cancellationToken);
    Task<CurrentUserDto> MeAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> IsAccessAllowedAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
}
