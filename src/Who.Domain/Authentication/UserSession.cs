namespace Who.Domain.Authentication;

public sealed class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TokenFamilyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public DateTimeOffset IdleExpiresAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? Platform { get; set; }
    public string? DeviceName { get; set; }
    public string? AppVersion { get; set; }
    public string? LastIpAddress { get; set; }
    public string? LastUserAgent { get; set; }

    public bool IsExpired(DateTimeOffset now) => now >= IdleExpiresAt || now >= AbsoluteExpiresAt;
    public void Slide(DateTimeOffset now)
    {
        LastUsedAt = now;
        IdleExpiresAt = now.AddDays(30) < AbsoluteExpiresAt ? now.AddDays(30) : AbsoluteExpiresAt;
    }
    public void Revoke(DateTimeOffset now, string reason) { RevokedAt ??= now; RevokedReason ??= reason; }
}

public sealed class SessionRefreshToken
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
}
