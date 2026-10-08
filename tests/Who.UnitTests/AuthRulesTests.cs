using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Who.Domain.Accounts;
using Who.Domain.Authentication;
using Who.Infrastructure.Authentication;

namespace Who.UnitTests;

public sealed class AuthRulesTests
{
    [Theory]
    [InlineData("2013-10-07", null)]
    [InlineData("2013-10-08", "AGE_NOT_ELIGIBLE")]
    [InlineData("2026-10-08", "BIRTH_DATE_INVALID")]
    [InlineData("1800-01-01", "BIRTH_DATE_INVALID")]
    public void Civil_age_boundaries_use_the_explicit_day(string birth, string? error) =>
        Assert.Equal(error, AccountRules.BirthDateError(DateOnly.Parse(birth), new(2026, 10, 7)));

    [Fact]
    public void Leap_birthdays_follow_Flutter_March_first_rule()
    {
        Assert.Equal("AGE_NOT_ELIGIBLE", AccountRules.BirthDateError(new(2012, 2, 29), new(2025, 2, 28)));
        Assert.Null(AccountRules.BirthDateError(new(2012, 2, 29), new(2025, 3, 1)));
    }

    [Theory]
    [InlineData("  @WHO_123  ", "who_123")]
    [InlineData("A", "a")]
    [InlineData("_", "_")]
    [InlineData("123", "123")]
    [InlineData("\uFEFF\u2003@KENAN\u00a0\uFEFF", "kenan")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("@@kenan", null)]
    [InlineData("@ kenan", null)]
    [InlineData("Çağrı", null)]
    [InlineData("foo-bar", null)]
    [InlineData("foo.bar", null)]
    [InlineData("foo\nbar", null)]
    [InlineData("🙂", null)]
    public void Username_matches_Flutter_without_changing_display_text(string input, string? normalized) =>
        Assert.Equal(normalized, AccountRules.NormalizeUsername(input));

    [Fact]
    public void Username_has_no_invented_length_cap()
    {
        var value = new string('A', 10000);
        Assert.Equal(value.ToLowerInvariant(), AccountRules.NormalizeUsername(value));
    }

    [Theory]
    [InlineData(AccountVisibility.Public, FollowPermission.Everyone)]
    [InlineData(AccountVisibility.Private, FollowPermission.RequestRequired)]
    public void Privacy_presets_keep_discovery_enabled_and_Flutter_history_defaults(AccountVisibility visibility, FollowPermission follow)
    {
        var privacy = UserPrivacyPreferences.Preset(Guid.NewGuid(), visibility, DateTimeOffset.UnixEpoch);
        Assert.Equal(visibility, privacy.AccountVisibility); Assert.Equal(follow, privacy.FollowPermission);
        Assert.True(privacy.Searchable); Assert.True(privacy.RecommendationsEnabled);
        Assert.Equal(ProfilePollVisibility.Everyone, privacy.ProfilePollVisibility);
        Assert.Equal(SocialListVisibility.Everyone, privacy.SocialListVisibility);
    }

    private static EmailVerificationChallenge Challenge(DateTimeOffset now) => new()
    { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), CreatedAt = now, ExpiresAt = now.AddMinutes(10) };

    [Fact]
    public void Verification_expires_exactly_at_the_ten_minute_boundary()
    {
        var start = DateTimeOffset.UnixEpoch; var challenge = Challenge(start);
        Assert.Equal("VERIFICATION_CODE_EXPIRED", challenge.Verify(true, start.AddMinutes(10)));
        Assert.Null(challenge.ConsumedAt); Assert.Equal(0, challenge.AttemptCount);
    }
    [Fact]
    public void Fifth_wrong_attempt_invalidates_and_correct_code_cannot_recover()
    {
        var now = DateTimeOffset.UnixEpoch; var challenge = Challenge(now);
        for (var i = 1; i <= 4; i++) { Assert.Equal("VERIFICATION_CODE_INVALID", challenge.Verify(false, now)); Assert.Equal(i, challenge.AttemptCount); }
        Assert.Equal("VERIFICATION_ATTEMPTS_EXCEEDED", challenge.Verify(false, now));
        Assert.NotNull(challenge.InvalidatedAt);
        Assert.Equal("VERIFICATION_ATTEMPTS_EXCEEDED", challenge.Verify(true, now)); Assert.Null(challenge.ConsumedAt);
    }
    [Fact]
    public void Consumed_and_resend_invalidated_challenges_cannot_be_reused()
    {
        var now = DateTimeOffset.UnixEpoch; var consumed = Challenge(now);
        Assert.Null(consumed.Verify(true, now)); Assert.NotNull(consumed.ConsumedAt);
        Assert.Equal("VERIFICATION_UNAVAILABLE", consumed.Verify(true, now));
        var old = Challenge(now); old.InvalidatedAt = now;
        Assert.Equal("VERIFICATION_UNAVAILABLE", old.Verify(true, now));
        Assert.Null(Challenge(now).Verify(true, now));
    }
    [Fact]
    public void Codes_accept_leading_zero_and_keyed_hash_is_bound_to_challenge_user_and_pepper()
    {
        var pepper = RandomNumberGenerator.GetBytes(32); var id = Guid.NewGuid(); var userId = Guid.NewGuid();
        const string synthetic = "000123";
        var hash = AuthCryptography.CodeHash(pepper, id, userId, synthetic);
        Assert.True(AuthCryptography.IsCode(synthetic)); Assert.True(hash != synthetic);
        Assert.True(AuthCryptography.CodeMatches(pepper, id, userId, synthetic, hash));
        Assert.False(AuthCryptography.CodeMatches(pepper, Guid.NewGuid(), userId, synthetic, hash));
        Assert.False(AuthCryptography.CodeMatches(pepper, id, Guid.NewGuid(), synthetic, hash));
        Assert.False(AuthCryptography.CodeMatches(RandomNumberGenerator.GetBytes(32), id, userId, synthetic, hash));
        Assert.False(AuthCryptography.CodeMatches(pepper, id, userId, "bad", hash));
        for (var i = 0; i < 50; i++) Assert.True(AuthCryptography.IsCode(AuthCryptography.GenerateCode()));
    }
    [Fact]
    public void Refresh_tokens_are_URL_safe_256_bit_opaque_values_and_only_hashes_are_stored()
    {
        var a = AuthCryptography.GenerateRefresh(); var b = AuthCryptography.GenerateRefresh();
        Assert.True(a != b); Assert.True(AuthCryptography.IsRefresh(a)); Assert.Equal(43, a.Length);
        Assert.Equal(64, AuthCryptography.RefreshHash(a).Length);
        Assert.True(AuthCryptography.RefreshHash(a) != a);
        Assert.False(AuthCryptography.IsRefresh("malformed"));
    }
    [Fact]
    public void Idle_slides_thirty_days_and_is_capped_by_non_sliding_absolute_expiry()
    {
        var now = DateTimeOffset.UnixEpoch;
        var session = new UserSession { CreatedAt = now, LastUsedAt = now, IdleExpiresAt = now.AddDays(30), AbsoluteExpiresAt = now.AddDays(180) };
        Assert.False(session.IsExpired(now.AddDays(30).AddTicks(-1))); Assert.True(session.IsExpired(now.AddDays(30)));
        session.Slide(now.AddDays(20)); Assert.Equal(now.AddDays(50), session.IdleExpiresAt);
        session.Slide(now.AddDays(170)); Assert.Equal(now.AddDays(180), session.IdleExpiresAt);
        Assert.Equal(now.AddDays(180), session.AbsoluteExpiresAt); Assert.True(session.IsExpired(now.AddDays(180)));
        Assert.Equal(900, AuthOptions.AccessLifetimeSeconds); Assert.Equal(900, AuthOptions.OnboardingLifetimeSeconds);
    }
    [Fact]
    public void Repeated_revocation_preserves_original_security_evidence()
    {
        var session = new UserSession(); var first = DateTimeOffset.UnixEpoch;
        session.Revoke(first, "refresh_token_reuse");
        session.Revoke(first.AddDays(1), "logout_all");
        Assert.Equal(first, session.RevokedAt);
        Assert.Equal("refresh_token_reuse", session.RevokedReason);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("invalid-base64")]
    [InlineData("dG9vLXNob3J0")]
    public void Missing_or_weak_auth_secrets_fail_without_echoing_them(string? value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WHO_AUTH_VERIFICATION_PEPPER"] = value }).Build();
        var failure = Assert.Throws<InvalidOperationException>(() => new AuthOptions().Load(config));
        Assert.Contains("WHO_AUTH_VERIFICATION_PEPPER", failure.Message);
        if (value is not null) Assert.DoesNotContain(value, failure.Message);
    }
}
