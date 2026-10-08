namespace Who.Domain.Authentication;

public sealed class EmailVerificationChallenge
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }

    public string? Verify(bool matches, DateTimeOffset now)
    {
        if (AttemptCount >= 5) return "VERIFICATION_ATTEMPTS_EXCEEDED";
        if (ConsumedAt is not null || InvalidatedAt is not null) return "VERIFICATION_UNAVAILABLE";
        if (now >= ExpiresAt) return "VERIFICATION_CODE_EXPIRED";
        if (matches) { ConsumedAt = now; return null; }
        AttemptCount++;
        if (AttemptCount == 5) { InvalidatedAt = now; return "VERIFICATION_ATTEMPTS_EXCEEDED"; }
        return "VERIFICATION_CODE_INVALID";
    }
}
