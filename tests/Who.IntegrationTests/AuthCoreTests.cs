using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Who.Application.Authentication;
using Who.Domain.Accounts;
using Who.Infrastructure.Authentication;
using Who.Infrastructure.Persistence;

namespace Who.IntegrationTests;

public sealed class AuthCoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private sealed class Harness : IDisposable
    {
        public TestClock Clock { get; } = new();
        public RecordingEmailSender Sender { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public RecordingLogs Logs { get; } = new();
        public System.Collections.Concurrent.ConcurrentBag<string> Secrets { get; } = new();
        public HttpClient Client { get; }
        public Harness(string connection, bool relaxedRates = true)
        {
            Factory = new ApiFactory(connection, relaxedRates: relaxedRates).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IVerificationEmailSender>(); services.AddSingleton<IVerificationEmailSender>(Sender);
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
                services.AddLogging(logging => logging.AddProvider(Logs));
            }));
            Client = Factory.CreateClient();
        }
        public Task<T> Db<T>(Func<WhoDbContext, Task<T>> action)
        {
            return Run();
            async Task<T> Run() { using var scope = Factory.Services.CreateScope(); return await action(scope.ServiceProvider.GetRequiredService<WhoDbContext>()); }
        }
        public void Dispose()
        {
            Client.Dispose(); Factory.Dispose();
            var messages = Logs.Messages.ToArray();
            foreach (var secret in Secrets.Concat(new[] { TestAuthConfiguration.Pepper, TestAuthConfiguration.SigningKey }).Where(s => !string.IsNullOrWhiteSpace(s)))
                Assert.False(messages.Any(m => m.Contains(secret, StringComparison.Ordinal)), "Sensitive value was found in application logs.");
            foreach (var code in Sender.AllCodes)
                Assert.False(messages.Any(m => System.Text.RegularExpressions.Regex.IsMatch(m, @"(?<!\d)" + code + @"(?!\d)")), "Verification code was found in application logs.");
        }
    }
    private Harness New(bool relaxedRates = true) => new(postgres.Container.GetConnectionString(), relaxedRates);
    private static RegisterRequest Request(Harness h, string? username = null, string? email = null, int age = 26) => new(
        DateOnly.FromDateTime(h.Clock.GetUtcNow().UtcDateTime).AddYears(-age).ToString("yyyy-MM-dd"), username ?? ("user_" + Guid.NewGuid().ToString("N")),
        "Kenan Özteriş — İpek", email ?? (Guid.NewGuid().ToString("N") + "@example.test"), "synthetic test passphrase");
    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task<JsonElement> Error(HttpResponseMessage response, string code, HttpStatusCode? status = null)
    {
        if (status is { } s) Assert.Equal(s, response.StatusCode); else Assert.False(response.IsSuccessStatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        Assert.Equal(code, json.GetProperty("code").GetString()); return json;
    }
    private static async Task<HttpResponseMessage> Post(Harness h, string path, object body, string? access = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (access is not null) request.Headers.Authorization = new("Bearer", access);
        if (access is not null) h.Secrets.Add(access);
        if (body is RegisterRequest registration) { if (registration.Password is { } p) h.Secrets.Add(p); if (registration.BirthDate is { } birth && DateOnly.TryParse(birth, out _)) h.Secrets.Add(birth); }
        if (body is LoginRequest login && login.Password is { } password) h.Secrets.Add(password);
        if (body is RefreshRequest refresh && refresh.RefreshToken is { } token) h.Secrets.Add(token);
        var response = await h.Client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync() is { Length: > 0 } text ? text : "{}");
        foreach (var name in new[] { "accessToken", "refreshToken", "onboardingToken" })
            if (json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) h.Secrets.Add(value.GetString()!);
        return response;
    }
    private static async Task<HttpResponseMessage> Me(Harness h, string? access = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        if (access is not null) request.Headers.Authorization = new("Bearer", access);
        return await h.Client.SendAsync(request);
    }
    private static async Task<(RegisterRequest Request, PendingRegistrationResponse Pending)> Register(Harness h, RegisterRequest? request = null)
    {
        request ??= Request(h);
        var pending = await Read<PendingRegistrationResponse>(await Post(h, "/api/v1/auth/register", request), HttpStatusCode.Accepted);
        return (request, pending);
    }
    private static async Task<VerificationResponse> Verify(Harness h, RegisterRequest request, PendingRegistrationResponse pending) =>
        await Read<VerificationResponse>(await Post(h, "/api/v1/auth/verify-email", new VerifyEmailRequest(pending.RegistrationId, h.Sender.CodeFor(request.Email!))), HttpStatusCode.OK);
    private static async Task<(RegisterRequest Request, TokenResponse Pair)> Active(Harness h, string visibility = "public")
    {
        var (request, pending) = await Register(h); var verified = await Verify(h, request, pending);
        var pair = await Read<TokenResponse>(await Post(h, "/api/v1/onboarding/privacy", new PrivacyOnboardingRequest(visibility), verified.OnboardingToken), HttpStatusCode.OK);
        return (request, pair);
    }
    private static async Task<TokenResponse> Login(Harness h, RegisterRequest request) =>
        await Read<TokenResponse>(await Post(h, "/api/v1/auth/login", new LoginRequest(request.Email, request.Password, new("ios", "Test device", "1.0"))), HttpStatusCode.OK);
    private static async Task<TokenResponse> Refresh(Harness h, string refresh) =>
        await Read<TokenResponse>(await Post(h, "/api/v1/auth/refresh", new RefreshRequest(refresh)), HttpStatusCode.OK);

    [Fact]
    public async Task Under_thirteen_rejects_before_any_identity_profile_challenge_or_email()
    {
        using var h = New();
        var before = await h.Db(async db => new[] { await db.Users.CountAsync(), await db.UserProfiles.CountAsync(), await db.EmailVerificationChallenges.CountAsync() });
        var request = Request(h, age: 12) with { Username = "bad-name", Email = "invalid", Password = "short" };
        await Error(await Post(h, "/api/v1/auth/register", request), "AGE_NOT_ELIGIBLE", HttpStatusCode.BadRequest);
        var after = await h.Db(async db => new[] { await db.Users.CountAsync(), await db.UserProfiles.CountAsync(), await db.EmailVerificationChallenges.CountAsync() });
        Assert.Equal(before, after); Assert.Equal(0, h.Sender.Count);
    }
    [Theory]
    [InlineData("invalid")]
    [InlineData("2099-01-01")]
    [InlineData("1800-01-01")]
    public async Task Invalid_birth_dates_fail_safely(string birth)
    {
        using var h = New(); await Error(await Post(h, "/api/v1/auth/register", Request(h) with { BirthDate = birth }), "BIRTH_DATE_INVALID", HttpStatusCode.BadRequest);
        Assert.Equal(0, h.Sender.Count);
    }
    [Fact]
    public async Task Exactly_thirteen_registers_atomically_pending_without_privacy_or_session_and_Identity_hashes_password()
    {
        using var h = New(); var (request, pending) = await Register(h, Request(h, age: 13));
        Assert.True(pending.VerificationRequired); Assert.Equal(600, pending.ExpiresInSeconds); Assert.Equal(60, pending.ResendAvailableInSeconds);
        await h.Db(async db =>
        {
            var profile = await db.UserProfiles.SingleAsync(p => p.RegistrationId == pending.RegistrationId);
            var user = await db.Users.SingleAsync(u => u.Id == profile.UserId);
            Assert.True(user.Id != pending.RegistrationId); Assert.False(user.EmailConfirmed);
            Assert.Equal(request.Email, user.UserName); Assert.True(user.UserName != profile.Username);
            Assert.True(user.PasswordHash != request.Password); Assert.NotNull(user.PasswordHash);
            Assert.Equal(AccountStatus.PendingEmailVerification, profile.AccountStatus); Assert.False(profile.OnboardingCompleted);
            Assert.Equal(request.DisplayName, profile.DisplayName);
            Assert.False(await db.UserPrivacyPreferences.AnyAsync(p => p.UserId == user.Id)); Assert.False(await db.UserSessions.AnyAsync(s => s.UserId == user.Id));
            var challenge = await db.EmailVerificationChallenges.SingleAsync(c => c.UserId == user.Id);
            Assert.Equal(TimeSpan.FromMinutes(10), challenge.ExpiresAt - challenge.CreatedAt);
            Assert.True(challenge.CodeHash != h.Sender.CodeFor(request.Email!)); Assert.Equal(64, challenge.CodeHash.Length);
            return true;
        });
    }
    [Fact]
    public async Task Password_policy_allows_spaces_and_enforces_ten_to_128_without_character_classes()
    {
        using var h = New();
        await Error(await Post(h, "/api/v1/auth/register", Request(h) with { Password = new string('a', 9) }), "PASSWORD_INVALID");
        await Error(await Post(h, "/api/v1/auth/register", Request(h) with { Password = new string('a', 129) }), "PASSWORD_INVALID");
        await Register(h, Request(h) with { Password = new string(' ', 10) });
        await Register(h, Request(h) with { Password = new string('a', 128) });
    }
    [Fact]
    public async Task Username_and_email_normalization_are_canonical_and_errors_do_not_echo_credentials()
    {
        using var h = New(); var name = "U_" + Guid.NewGuid().ToString("N");
        var (request, pending) = await Register(h, Request(h, username: "  @" + name.ToUpperInvariant() + "  "));
        await h.Db(async db => { var p = await db.UserProfiles.SingleAsync(p => p.RegistrationId == pending.RegistrationId); Assert.Equal(name.ToLowerInvariant(), p.Username); return true; });
        await Error(await Post(h, "/api/v1/auth/register", Request(h, username: name)), "USERNAME_UNAVAILABLE", HttpStatusCode.Conflict);
        await Error(await Post(h, "/api/v1/auth/register", Request(h, email: "  " + request.Email!.ToUpperInvariant() + "  ")), "EMAIL_UNAVAILABLE", HttpStatusCode.Conflict);
        await Error(await Post(h, "/api/v1/auth/register", Request(h) with { Username = "@@invalid" }), "USERNAME_INVALID");
        await Error(await Post(h, "/api/v1/auth/register", Request(h) with { DisplayName = new string('x', 121) }), "DISPLAY_NAME_INVALID");
        await Error(await Post(h, "/api/v1/auth/register", Request(h) with { Email = "bad" }), "EMAIL_INVALID");
    }
    [Fact]
    public async Task Unbounded_Flutter_username_uses_database_unique_key_without_B_tree_length_failure()
    {
        using var h = New();
        var username = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(5000)).ToLowerInvariant();
        await Register(h, Request(h, username: username));
        await Error(await Post(h, "/api/v1/auth/register", Request(h, username: username.ToUpperInvariant())), "USERNAME_UNAVAILABLE", HttpStatusCode.Conflict);
    }
    [Fact]
    public async Task Correct_code_confirms_email_but_does_not_activate_or_create_normal_session_and_cannot_be_reused()
    {
        using var h = New(); var (request, pending) = await Register(h); var verified = await Verify(h, request, pending);
        Assert.True(verified.EmailVerified); Assert.True(verified.OnboardingRequired); Assert.Equal(900, verified.ExpiresIn);
        await h.Db(async db =>
        {
            var p = await db.UserProfiles.SingleAsync(p => p.RegistrationId == pending.RegistrationId);
            Assert.True((await db.Users.SingleAsync(u => u.Id == p.UserId)).EmailConfirmed);
            Assert.Equal(AccountStatus.PendingEmailVerification, p.AccountStatus); Assert.False(p.OnboardingCompleted);
            Assert.NotNull((await db.EmailVerificationChallenges.SingleAsync(c => c.UserId == p.UserId)).ConsumedAt);
            Assert.False(await db.UserSessions.AnyAsync(s => s.UserId == p.UserId)); return true;
        });
        await Error(await Post(h, "/api/v1/auth/verify-email", new VerifyEmailRequest(pending.RegistrationId, h.Sender.CodeFor(request.Email!))), "VERIFICATION_UNAVAILABLE");
    }
    [Fact]
    public async Task Concurrent_wrong_attempts_are_counted_atomically_and_five_lock_out_even_correct_code()
    {
        using var h = New(); var (request, pending) = await Register(h); var correct = h.Sender.CodeFor(request.Email!);
        var wrong = correct == "000000" ? "999999" : "000000";
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Post(h, "/api/v1/auth/verify-email", new VerifyEmailRequest(pending.RegistrationId, wrong))));
        Assert.All(responses, r => Assert.False(r.IsSuccessStatusCode));
        await h.Db(async db => { var p = await db.UserProfiles.SingleAsync(p => p.RegistrationId == pending.RegistrationId); var c = await db.EmailVerificationChallenges.SingleAsync(c => c.UserId == p.UserId); Assert.Equal(5, c.AttemptCount); Assert.NotNull(c.InvalidatedAt); return true; });
        await Error(await Post(h, "/api/v1/auth/verify-email", new VerifyEmailRequest(pending.RegistrationId, correct)), "VERIFICATION_ATTEMPTS_EXCEEDED", HttpStatusCode.TooManyRequests);
    }
    [Fact]
    public async Task Exact_expiry_rejects_correct_code()
    {
        using var h = New(); var (request, pending) = await Register(h); h.Clock.Advance(TimeSpan.FromMinutes(10));
        await Error(await Post(h, "/api/v1/auth/verify-email", new VerifyEmailRequest(pending.RegistrationId, h.Sender.CodeFor(request.Email!))), "VERIFICATION_CODE_EXPIRED");
    }
    [Fact]
    public async Task Resend_at_sixty_seconds_invalidates_old_challenge_and_only_latest_code_works()
    {
        using var h = New(); var (request, pending) = await Register(h); var old = h.Sender.CodeFor(request.Email!);
        await Error(await Post(h, "/api/v1/auth/resend-verification", new ResendVerificationRequest(pending.RegistrationId)), "VERIFICATION_RESEND_TOO_SOON", HttpStatusCode.TooManyRequests);
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        await Read<PendingRegistrationResponse>(await Post(h, "/api/v1/auth/resend-verification", new ResendVerificationRequest(pending.RegistrationId)), HttpStatusCode.Accepted);
        var latest = h.Sender.CodeFor(request.Email!);
        // Even if random six-digit values collide, challenge hashes are scoped and the prior record is invalidated.
        await h.Db(async db => { var p = await db.UserProfiles.SingleAsync(p => p.RegistrationId == pending.RegistrationId); var list = await db.EmailVerificationChallenges.Where(c => c.UserId == p.UserId).OrderBy(c => c.CreatedAt).ToListAsync(); Assert.Equal(2, list.Count); Assert.NotNull(list[0].InvalidatedAt); Assert.Null(list[1].InvalidatedAt); return true; });
        Assert.True(old != latest);
        await Error(await Post(h, "/api/v1/auth/verify-email", new VerifyEmailRequest(pending.RegistrationId, old)), "VERIFICATION_CODE_INVALID");
        await Verify(h, request, pending); Assert.Equal(2, h.Sender.Count);
    }
    [Fact]
    public async Task Failed_email_delivery_keeps_atomic_pending_account_and_resend_can_continue()
    {
        using var h = New(); h.Sender.Fail = true; var (request, pending) = await Register(h);
        Assert.Equal("failed", pending.EmailDeliveryStatus); Assert.Equal("VERIFICATION_DELIVERY_FAILED", pending.Code);
        Assert.Equal(0, h.Sender.Count);
        await h.Db(async db => { var p = await db.UserProfiles.SingleAsync(p => p.RegistrationId == pending.RegistrationId); Assert.True(await db.Users.AnyAsync(u => u.Id == p.UserId)); Assert.True(await db.EmailVerificationChallenges.AnyAsync(c => c.UserId == p.UserId)); return true; });
        h.Sender.Fail = false; h.Clock.Advance(TimeSpan.FromSeconds(60));
        var resent = await Read<PendingRegistrationResponse>(await Post(h, "/api/v1/auth/resend-verification", new ResendVerificationRequest(pending.RegistrationId)), HttpStatusCode.Accepted);
        Assert.Equal("sent", resent.EmailDeliveryStatus); await Verify(h, request, pending);
    }
    [Theory]
    [InlineData("public", "everyone")]
    [InlineData("private", "requestRequired")]
    public async Task Onboarding_creates_exact_privacy_and_first_session_atomically_and_replay_fails(string visibility, string follow)
    {
        using var h = New(); var (request, pending) = await Register(h); var verified = await Verify(h, request, pending);
        var pair = await Read<TokenResponse>(await Post(h, "/api/v1/onboarding/privacy", new PrivacyOnboardingRequest(visibility), verified.OnboardingToken), HttpStatusCode.OK);
        var user = pair.User; Assert.Equal("active", user.AccountStatus); Assert.True(user.OnboardingCompleted);
        Assert.Equal(visibility, user.Privacy.AccountVisibility); Assert.Equal(follow, user.Privacy.FollowPermission);
        Assert.True(user.Privacy.Searchable); Assert.True(user.Privacy.RecommendationsEnabled);
        Assert.Equal("everyone", user.Privacy.ProfilePollVisibility); Assert.Equal("everyone", user.Privacy.SocialListVisibility);
        await h.Db(async db => { Assert.Equal(1, await db.UserPrivacyPreferences.CountAsync(p => p.UserId == user.Id)); Assert.Equal(1, await db.UserSessions.CountAsync(s => s.UserId == user.Id)); return true; });
        await Error(await Post(h, "/api/v1/onboarding/privacy", new PrivacyOnboardingRequest(visibility), verified.OnboardingToken), "ONBOARDING_ALREADY_COMPLETED", HttpStatusCode.Conflict);
        await Read<CurrentUserDto>(await Me(h, pair.AccessToken), HttpStatusCode.OK);
    }
    [Fact]
    public async Task Token_type_confusion_and_registration_handle_never_grant_normal_access_and_claims_are_minimal()
    {
        using var h = New(); var (request, pending) = await Register(h); var verified = await Verify(h, request, pending);
        Assert.Equal(HttpStatusCode.Forbidden, (await Me(h, verified.OnboardingToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(h, pending.RegistrationId.ToString())).StatusCode);
        var onboarding = new JwtSecurityTokenHandler().ReadJwtToken(verified.OnboardingToken);
        Assert.Equal("onboarding", onboarding.Claims.Single(c => c.Type == "typ").Value);
        Assert.DoesNotContain(onboarding.Claims, c => new[] { "sid", "birthDate", "age", "username", "privacy", "displayName" }.Contains(c.Type));
        var pair = await Read<TokenResponse>(await Post(h, "/api/v1/onboarding/privacy", new PrivacyOnboardingRequest("public"), verified.OnboardingToken), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(h, "/api/v1/onboarding/privacy", new PrivacyOnboardingRequest("public"), pair.AccessToken)).StatusCode);
        var access = new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken);
        Assert.Equal(new[] { "aud", "exp", "iat", "iss", "jti", "nbf", "sid", "sub", "typ" }, access.Claims.Select(c => c.Type).Order().ToArray());
        Assert.Equal("access", access.Claims.Single(c => c.Type == "typ").Value);
        Assert.Equal(TimeSpan.FromMinutes(15), access.ValidTo - access.ValidFrom);
        h.Clock.Advance(TimeSpan.FromMinutes(15)); Assert.Equal(HttpStatusCode.Unauthorized, (await Me(h, pair.AccessToken)).StatusCode);
    }
    [Fact]
    public async Task Login_pending_states_do_not_issue_sessions_and_unknown_and_wrong_password_have_same_external_error()
    {
        using var h = New(); var (request, pending) = await Register(h);
        var unverified = await Error(await Post(h, "/api/v1/auth/login", new LoginRequest(request.Email, request.Password)), "EMAIL_VERIFICATION_REQUIRED", HttpStatusCode.Conflict);
        Assert.Equal(pending.RegistrationId, unverified.GetProperty("registrationId").GetGuid()); Assert.Equal(1, h.Sender.Count);
        await Verify(h, request, pending);
        var incomplete = await Error(await Post(h, "/api/v1/auth/login", new LoginRequest(request.Email, request.Password)), "ONBOARDING_REQUIRED", HttpStatusCode.Conflict);
        Assert.True(incomplete.TryGetProperty("onboardingToken", out _));
        await h.Db(async db => { var p = await db.UserProfiles.SingleAsync(p => p.RegistrationId == pending.RegistrationId); Assert.False(await db.UserSessions.AnyAsync(s => s.UserId == p.UserId)); return true; });
        await Error(await Post(h, "/api/v1/auth/login", new LoginRequest(request.Email, "incorrect synthetic password")), "INVALID_CREDENTIALS", HttpStatusCode.Unauthorized);
        await Error(await Post(h, "/api/v1/auth/login", new LoginRequest("unknown@example.test", request.Password)), "INVALID_CREDENTIALS", HttpStatusCode.Unauthorized);
    }
    [Fact]
    public async Task Active_login_creates_independent_session_and_refresh_rotation_preserves_hash_history_and_lifetimes()
    {
        using var h = New(); var (request, first) = await Active(h); var second = await Login(h, request);
        Assert.True(first.RefreshToken != second.RefreshToken);
        h.Clock.Advance(TimeSpan.FromDays(20)); var rotated = await Refresh(h, first.RefreshToken);
        Assert.True(rotated.RefreshToken != first.RefreshToken);
        await h.Db(async db =>
        {
            var hash = AuthCryptography.RefreshHash(first.RefreshToken); var old = await db.SessionRefreshTokens.SingleAsync(t => t.TokenHash == hash);
            Assert.NotNull(old.UsedAt); Assert.NotNull(old.ReplacedByTokenId);
            var child = await db.SessionRefreshTokens.SingleAsync(t => t.Id == old.ReplacedByTokenId);
            Assert.True(child.TokenHash != rotated.RefreshToken); Assert.Equal(old.SessionId, child.SessionId);
            var session = await db.UserSessions.SingleAsync(s => s.Id == old.SessionId);
            Assert.Equal(session.CreatedAt.AddDays(180), session.AbsoluteExpiresAt);
            Assert.Equal(h.Clock.GetUtcNow(), session.LastUsedAt); Assert.Equal(h.Clock.GetUtcNow().AddDays(30), session.IdleExpiresAt); return true;
        });
    }
    [Fact]
    public async Task Refresh_reuse_revokes_only_suspicious_family_and_immediately_blocks_its_access_token()
    {
        using var h = New(); var (request, first) = await Active(h); var second = await Login(h, request); var rotated = await Refresh(h, first.RefreshToken);
        await Error(await Post(h, "/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken)), "REFRESH_TOKEN_REUSE_DETECTED", HttpStatusCode.Unauthorized);
        await Error(await Post(h, "/api/v1/auth/refresh", new RefreshRequest(rotated.RefreshToken)), "SESSION_REVOKED", HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.Forbidden, (await Me(h, rotated.AccessToken)).StatusCode);
        await Refresh(h, second.RefreshToken);
    }
    [Fact]
    public async Task Concurrent_refresh_has_only_one_child_and_loser_revokes_only_that_session()
    {
        using var h = New(); var (request, first) = await Active(h); var other = await Login(h, request);
        var replies = await Task.WhenAll(Post(h, "/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken)), Post(h, "/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken)));
        Assert.Equal(1, replies.Count(r => r.IsSuccessStatusCode)); Assert.Equal(1, replies.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
        await h.Db(async db => { var old = await db.SessionRefreshTokens.SingleAsync(t => t.TokenHash == AuthCryptography.RefreshHash(first.RefreshToken)); Assert.Equal(2, await db.SessionRefreshTokens.CountAsync(t => t.SessionId == old.SessionId)); Assert.NotNull((await db.UserSessions.SingleAsync(s => s.Id == old.SessionId)).RevokedAt); return true; });
        await Refresh(h, other.RefreshToken);
    }
    [Fact]
    public async Task Logout_current_preserves_other_device_and_logout_all_revokes_every_session_immediately()
    {
        using var h = New(); var (request, first) = await Active(h); var other = await Login(h, request);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(h, "/api/v1/auth/logout", new { }, first.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Me(h, first.AccessToken)).StatusCode);
        await Error(await Post(h, "/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken)), "SESSION_REVOKED");
        other = await Refresh(h, other.RefreshToken); var third = await Login(h, request);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(h, "/api/v1/auth/logout-all", new { }, other.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Me(h, other.AccessToken)).StatusCode);
        await Error(await Post(h, "/api/v1/auth/refresh", new RefreshRequest(other.RefreshToken)), "SESSION_REVOKED");
        await Error(await Post(h, "/api/v1/auth/refresh", new RefreshRequest(third.RefreshToken)), "SESSION_REVOKED");
    }
    [Theory]
    [InlineData(30)]
    [InlineData(180)]
    public async Task Session_expiry_is_authoritative_at_exact_boundaries(int days)
    {
        using var h = New(); var (_, pair) = await Active(h); h.Clock.Advance(TimeSpan.FromDays(days));
        await Error(await Post(h, "/api/v1/auth/refresh", new RefreshRequest(pair.RefreshToken)), "SESSION_EXPIRED", HttpStatusCode.Unauthorized);
    }
    [Fact]
    public async Task Suspended_account_blocks_me_refresh_and_new_login()
    {
        using var h = New(); var (request, pair) = await Active(h);
        await h.Db(async db => { var p = await db.UserProfiles.SingleAsync(p => p.UserId == pair.User.Id); p.AccountStatus = AccountStatus.Suspended; await db.SaveChangesAsync(); return true; });
        Assert.Equal(HttpStatusCode.Forbidden, (await Me(h, pair.AccessToken)).StatusCode);
        await Error(await Post(h, "/api/v1/auth/refresh", new RefreshRequest(pair.RefreshToken)), "ACCOUNT_SUSPENDED", HttpStatusCode.Forbidden);
        await Error(await Post(h, "/api/v1/auth/login", new LoginRequest(request.Email, request.Password)), "ACCOUNT_SUSPENDED", HttpStatusCode.Forbidden);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_registration_unique_constraints_leave_no_partial_accounts(bool sameUsername)
    {
        using var h = New(); var a = Request(h); var b = Request(h);
        b = sameUsername ? b with { Username = a.Username!.ToUpperInvariant() } : b with { Email = a.Email!.ToUpperInvariant() };
        var replies = await Task.WhenAll(Post(h, "/api/v1/auth/register", a), Post(h, "/api/v1/auth/register", b));
        Assert.Equal(1, replies.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        await Error(replies.Single(r => r.StatusCode != HttpStatusCode.Accepted), sameUsername ? "USERNAME_UNAVAILABLE" : "EMAIL_UNAVAILABLE", HttpStatusCode.Conflict);
        await h.Db(async db =>
        {
            Assert.Equal(1, await db.UserProfiles.CountAsync(p => p.NormalizedUsername == a.Username!.ToLowerInvariant() || p.NormalizedUsername == b.Username!.ToLowerInvariant()));
            Assert.Equal(1, await db.Users.CountAsync(u => u.NormalizedEmail == a.Email!.ToUpperInvariant() || u.NormalizedEmail == b.Email!.ToUpperInvariant()));
            Assert.False(await db.Users.AnyAsync(u => !db.UserProfiles.Any(p => p.UserId == u.Id))); return true;
        });
    }
    [Fact]
    public async Task Actual_migration_from_empty_Postgres_has_expected_tables_and_unique_indexes_and_no_social_domain()
    {
        using var h = New(); await h.Db(async db =>
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray(); Assert.Single(applied); Assert.EndsWith("InitialAuthAndUser", applied[0]);
            var connection = db.Database.GetDbConnection(); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname='public'";
            var names = new List<string>(); await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            foreach (var expected in new[] { "AspNetUsers", "UserProfiles", "UserPrivacyPreferences", "EmailVerificationChallenges", "UserSessions", "SessionRefreshTokens" }) Assert.Contains(expected, names);
            Assert.DoesNotContain(names, n => n.Contains("Poll", StringComparison.Ordinal) || n.Contains("Circle", StringComparison.Ordinal) || n.Contains("Follow", StringComparison.Ordinal));
            command.CommandText = "SELECT count(*) FROM pg_indexes WHERE schemaname='public' AND indexdef LIKE 'CREATE UNIQUE INDEX%' AND indexname IN ('UX_UserProfiles_NormalizedUsername','UX_Identity_NormalizedEmail','UX_Challenge_ActiveUser')";
            Assert.Equal(3L, await command.ExecuteScalarAsync()); return true;
        });
    }

    [Fact]
    public async Task Onboarding_rejects_arbitrary_low_level_fields_and_concurrent_completion_creates_only_one_session()
    {
        using var h = New();
        var (request, pending) = await Register(h);
        var verified = await Verify(h, request, pending);
        await Error(await Post(h, "/api/v1/onboarding/privacy",
            new { accountVisibility = "public", searchable = false }, verified.OnboardingToken), "REQUEST_INVALID", HttpStatusCode.BadRequest);
        var responses = await Task.WhenAll(
            Post(h, "/api/v1/onboarding/privacy", new PrivacyOnboardingRequest("public"), verified.OnboardingToken),
            Post(h, "/api/v1/onboarding/privacy", new PrivacyOnboardingRequest("private"), verified.OnboardingToken));
        Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));
        await Error(responses.Single(r => !r.IsSuccessStatusCode), "ONBOARDING_ALREADY_COMPLETED", HttpStatusCode.Conflict);
        await h.Db(async db =>
        {
            var profile = await db.UserProfiles.SingleAsync(p => p.RegistrationId == pending.RegistrationId);
            Assert.True(profile.OnboardingCompleted); Assert.Equal(AccountStatus.Active, profile.AccountStatus);
            Assert.Equal(1, await db.UserPrivacyPreferences.CountAsync(p => p.UserId == profile.UserId));
            Assert.Equal(1, await db.UserSessions.CountAsync(s => s.UserId == profile.UserId));
            return true;
        });
    }
    [Fact]
    public async Task Concurrent_resend_has_one_winner_and_one_active_challenge()
    {
        using var h = New(); var (request, pending) = await Register(h);
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        var responses = await Task.WhenAll(
            Post(h, "/api/v1/auth/resend-verification", new ResendVerificationRequest(pending.RegistrationId)),
            Post(h, "/api/v1/auth/resend-verification", new ResendVerificationRequest(pending.RegistrationId)));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        await Error(responses.Single(r => r.StatusCode != HttpStatusCode.Accepted), "VERIFICATION_RESEND_TOO_SOON", HttpStatusCode.TooManyRequests);
        Assert.Equal(2, h.Sender.Count);
        await h.Db(async db =>
        {
            var profile = await db.UserProfiles.SingleAsync(p => p.RegistrationId == pending.RegistrationId);
            Assert.Equal(1, await db.EmailVerificationChallenges.CountAsync(c => c.UserId == profile.UserId && c.ConsumedAt == null && c.InvalidatedAt == null));
            return true;
        });
        await Verify(h, request, pending);
    }

    [Fact]
    public async Task Auth_responses_and_recorded_logs_keep_private_security_material_out_of_unauthenticated_contracts()
    {
        using var h = New(); var (request, pending) = await Register(h);
        var pendingJson = JsonSerializer.Serialize(pending);
        Assert.False(pendingJson.Contains(request.Password!, StringComparison.Ordinal));
        Assert.False(pendingJson.Contains(request.BirthDate!, StringComparison.Ordinal));
        Assert.False(pendingJson.Contains(h.Sender.CodeFor(request.Email!), StringComparison.Ordinal));
        var verified = await Verify(h, request, pending); var verificationJson = JsonSerializer.Serialize(verified);
        Assert.False(verificationJson.Contains(request.BirthDate!, StringComparison.Ordinal));
        var pair = await Read<TokenResponse>(await Post(h, "/api/v1/onboarding/privacy", new PrivacyOnboardingRequest("private"), verified.OnboardingToken), HttpStatusCode.OK);
        Assert.False(JsonSerializer.Serialize(pair).Contains(request.BirthDate!, StringComparison.Ordinal));
        var own = await Me(h, pair.AccessToken); var ownJson = await own.Content.ReadAsStringAsync();
        foreach (var field in new[] { "passwordHash", "securityStamp", "codeHash", "tokenHash", "normalizedUsernameKey" })
            Assert.False(ownJson.Contains(field, StringComparison.OrdinalIgnoreCase));
        Assert.False(ownJson.Contains(pair.RefreshToken, StringComparison.Ordinal));
        // Harness disposal inspects actual formatted ASP.NET/EF logs against passwords, DOB, codes, tokens and keys held only in memory.
    }
    [Fact]
    public async Task Default_login_rate_limit_returns_safe_429_and_cannot_replace_authoritative_challenge_limit()
    {
        using var h = New(relaxedRates: false);
        for (var i = 0; i < 10; i++) await Error(await Post(h, "/api/v1/auth/login", new LoginRequest("unknown-rate@example.test", "synthetic password")), "INVALID_CREDENTIALS", HttpStatusCode.Unauthorized);
        var denied = await Post(h, "/api/v1/auth/login", new LoginRequest("unknown-rate@example.test", "synthetic password"));
        await Error(denied, "RATE_LIMIT_EXCEEDED", HttpStatusCode.TooManyRequests); Assert.NotNull(denied.Headers.RetryAfter);
    }
}
