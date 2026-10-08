using System.Text;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Who.Application.Authentication;
using Who.Domain.Accounts;
using Who.Domain.Authentication;
using Who.Infrastructure.Identity;
using Who.Infrastructure.Persistence;

namespace Who.Infrastructure.Authentication;

public sealed class AuthService(WhoDbContext db, UserManager<ApplicationUser> users,
    IVerificationEmailSender emailSender, IOptions<AuthOptions> options, JwtTokens jwt, TimeProvider clock) : IAuthService
{
    private DateTimeOffset Now => clock.GetUtcNow();
    private static readonly ApplicationUser DummyIdentity = new();
    private static readonly Lazy<string> DummyPasswordHash = new(() => new PasswordHasher<ApplicationUser>().HashPassword(
        DummyIdentity, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));

    public async Task<PendingRegistrationResponse> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(request.BirthDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birth))
            throw new AuthException("BIRTH_DATE_INVALID");
        var now = Now;
        if (AccountRules.BirthDateError(birth, DateOnly.FromDateTime(now.UtcDateTime)) is { } ageError)
            throw new AuthException(ageError); // Before any Identity create/hash/email call.
        var username = AccountRules.NormalizeUsername(request.Username) ?? throw new AuthException("USERNAME_INVALID");
        if (await db.UserProfiles.AnyAsync(p => p.NormalizedUsername == username, ct)) throw new AuthException("USERNAME_UNAVAILABLE", 409);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.EnumerateRunes().Count() > AccountRules.DisplayNameMaxLength)
            throw new AuthException("DISPLAY_NAME_INVALID");
        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 256 || !new EmailAddressAttribute().IsValid(email)) throw new AuthException("EMAIL_INVALID");
        if (await users.FindByEmailAsync(email) is not null) throw new AuthException("EMAIL_UNAVAILABLE", 409);
        if (request.Password is not { Length: >= 10 and <= 128 }) throw new AuthException("PASSWORD_INVALID");

        var identity = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        var profile = new UserProfile
        {
            UserId = identity.Id,
            RegistrationId = Guid.NewGuid(),
            Username = username,
            NormalizedUsername = username,
            DisplayName = request.DisplayName,
            BirthDate = birth,
            CreatedAt = now,
            UpdatedAt = now
        };
        var (challenge, code) = NewChallenge(profile.UserId, now);
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                var created = await users.CreateAsync(identity, request.Password);
                if (!created.Succeeded) throw IdentityError(created);
                db.UserProfiles.Add(profile); db.EmailVerificationChallenges.Add(challenge);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
            {
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                throw UniqueError(pg.ConstraintName);
            }
        }
        return await DeliverAsync(profile.RegistrationId, email, code, ct);
    }

    public async Task<VerificationResponse> VerifyAsync(VerifyEmailRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var profile = await LockRegistrationAsync(request.RegistrationId, ct);
        var user = await users.FindByIdAsync(profile.UserId.ToString()) ?? throw new AuthException("VERIFICATION_UNAVAILABLE", 409);
        if (user.EmailConfirmed || profile.AccountStatus != AccountStatus.PendingEmailVerification)
            throw new AuthException("VERIFICATION_UNAVAILABLE", 409);
        var challenge = await db.EmailVerificationChallenges.Where(c => c.UserId == user.Id)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).FirstOrDefaultAsync(ct)
            ?? throw new AuthException("VERIFICATION_UNAVAILABLE", 409);
        var error = challenge.Verify(AuthCryptography.CodeMatches(options.Value.VerificationPepper,
            challenge.Id, user.Id, request.Code, challenge.CodeHash), Now);
        if (error is not null)
        {
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            throw new AuthException(error, error == "VERIFICATION_ATTEMPTS_EXCEEDED" ? 429 : 400);
        }
        user.EmailConfirmed = true;
        profile.UpdatedAt = Now;
        // Same context/transaction; EmailConfirmed does not make the WHO account Active.
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(true, true, jwt.Onboarding(user.Id), AuthOptions.OnboardingLifetimeSeconds);
    }

    public async Task<PendingRegistrationResponse> ResendAsync(Guid registrationId, CancellationToken ct)
    {
        UserProfile profile; ApplicationUser user; string code;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            profile = await LockRegistrationAsync(registrationId, ct);
            user = await users.FindByIdAsync(profile.UserId.ToString()) ?? throw new AuthException("VERIFICATION_UNAVAILABLE", 409);
            if (user.EmailConfirmed || profile.AccountStatus != AccountStatus.PendingEmailVerification)
                throw new AuthException("VERIFICATION_UNAVAILABLE", 409);
            var previous = await db.EmailVerificationChallenges.Where(c => c.UserId == user.Id).OrderByDescending(c => c.CreatedAt).FirstAsync(ct);
            var now = Now;
            var seconds = RemainingCooldown(previous.CreatedAt, now);
            if (seconds > 0) throw new AuthException("VERIFICATION_RESEND_TOO_SOON", 429,
                new Dictionary<string, object?> { ["resendAvailableInSeconds"] = seconds });
            if (previous.ConsumedAt is null && previous.InvalidatedAt is null) previous.InvalidatedAt = now;
            await db.SaveChangesAsync(ct); // Release the partial unique slot before inserting the latest challenge.
            var history = await db.EmailVerificationChallenges.Where(c => c.UserId == user.Id).ToListAsync(ct);
            var created = NewChallenge(user.Id, now);
            // Do not let a randomly repeated value make an older six-digit code valid again.
            while (history.Any(c => AuthCryptography.CodeMatches(options.Value.VerificationPepper, c.Id, user.Id, created.Code, c.CodeHash)))
                created = NewChallenge(user.Id, now);
            code = created.Code;
            db.EmailVerificationChallenges.Add(created.Challenge);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        }
        return await DeliverAsync(profile.RegistrationId, user.Email!, code, ct);
    }

    public async Task<TokenResponse> CompleteOnboardingAsync(Guid userId, string? visibility, RequestMetadata metadata, CancellationToken ct)
    {
        var preset = visibility switch
        {
            "public" => AccountVisibility.Public,
            "private" => AccountVisibility.Private,
            _ => throw new AuthException("PRIVACY_PRESET_INVALID")
        };
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var profile = await LockUserAsync(userId, ct);
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new AuthException("ACCOUNT_UNAVAILABLE", 403);
        if (profile.OnboardingCompleted) throw new AuthException("ONBOARDING_ALREADY_COMPLETED", 409);
        CheckAccountStatus(profile);
        if (!user.EmailConfirmed) throw new AuthException("EMAIL_VERIFICATION_REQUIRED", 409);
        var now = Now;
        var privacy = UserPrivacyPreferences.Preset(userId, preset, now);
        db.UserPrivacyPreferences.Add(privacy);
        profile.AccountStatus = AccountStatus.Active; profile.OnboardingCompleted = true; profile.UpdatedAt = now;
        var tokens = CreateSession(userId, null, metadata, now);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(jwt.Access(userId, tokens.Session.Id), tokens.Token, AuthOptions.AccessLifetimeSeconds, ToAuthUser(user, profile, privacy));
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest request, RequestMetadata metadata, CancellationToken ct)
    {
        if (request.Email is null || request.Email.Length > 256 || request.Password is not { Length: >= 1 and <= 128 })
            throw new AuthException("INVALID_CREDENTIALS", 401);
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            // Spend the same Identity verification work on unknown emails; no custom password crypto.
            users.PasswordHasher.VerifyHashedPassword(DummyIdentity, DummyPasswordHash.Value, request.Password);
            throw new AuthException("INVALID_CREDENTIALS", 401);
        }
        if (!await users.CheckPasswordAsync(user, request.Password)) throw new AuthException("INVALID_CREDENTIALS", 401);
        ValidateDevice(request.Device);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var profile = await LockUserAsync(user.Id, ct);
        CheckAccountStatus(profile);
        if (!user.EmailConfirmed)
        {
            var created = await db.EmailVerificationChallenges.Where(c => c.UserId == user.Id).MaxAsync(c => c.CreatedAt, ct);
            throw new AuthException("EMAIL_VERIFICATION_REQUIRED", 409, new Dictionary<string, object?>
            {
                ["registrationId"] = profile.RegistrationId,
                ["maskedEmail"] = MaskEmail(user.Email!),
                ["resendAvailableInSeconds"] = RemainingCooldown(created, Now)
            });
        }
        if (!profile.OnboardingCompleted)
            throw new AuthException("ONBOARDING_REQUIRED", 409, new Dictionary<string, object?>
            { ["onboardingToken"] = jwt.Onboarding(user.Id), ["expiresIn"] = AuthOptions.OnboardingLifetimeSeconds });
        if (profile.AccountStatus != AccountStatus.Active) throw new AuthException("ACCOUNT_UNAVAILABLE", 403);
        var privacy = await db.UserPrivacyPreferences.SingleOrDefaultAsync(p => p.UserId == user.Id, ct)
            ?? throw new AuthException("ACCOUNT_UNAVAILABLE", 403);
        var pair = CreateSession(user.Id, request.Device, metadata, Now);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(jwt.Access(user.Id, pair.Session.Id), pair.Token, AuthOptions.AccessLifetimeSeconds, ToAuthUser(user, profile, privacy));
    }

    public async Task<TokenResponse> RefreshAsync(string? plaintext, CancellationToken ct)
    {
        if (!AuthCryptography.IsRefresh(plaintext)) throw new AuthException("INVALID_REFRESH_TOKEN", 401);
        var hash = AuthCryptography.RefreshHash(plaintext!);
        var lookup = await (from t in db.SessionRefreshTokens.AsNoTracking()
                            join s in db.UserSessions.AsNoTracking() on t.SessionId equals s.Id
                            where t.TokenHash == hash
                            select new { TokenId = t.Id, SessionId = s.Id, s.UserId }).SingleOrDefaultAsync(ct)
            ?? throw new AuthException("INVALID_REFRESH_TOKEN", 401);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // All mutations acquire profile -> session locks in this order (also logout-all).
        var profile = await LockUserAsync(lookup.UserId, ct);
        var session = await db.UserSessions.FromSqlInterpolated($"SELECT * FROM \"UserSessions\" WHERE \"Id\" = {lookup.SessionId} FOR UPDATE").SingleAsync(ct);
        var token = await db.SessionRefreshTokens.SingleAsync(t => t.Id == lookup.TokenId, ct);
        var now = Now;
        if (session.RevokedAt is not null) throw new AuthException("SESSION_REVOKED", 401);
        if (token.UsedAt is not null)
        {
            await RevokeAsync(session, "refresh_token_reuse", now, ct);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            throw new AuthException("REFRESH_TOKEN_REUSE_DETECTED", 401);
        }
        if (session.IsExpired(now))
        {
            await RevokeAsync(session, "expired", now, ct);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            throw new AuthException("SESSION_EXPIRED", 401);
        }
        if (token.RevokedAt is not null) throw new AuthException("INVALID_REFRESH_TOKEN", 401);
        CheckAccountStatus(profile);
        var user = await users.FindByIdAsync(profile.UserId.ToString()) ?? throw new AuthException("ACCOUNT_UNAVAILABLE", 403);
        var privacy = await db.UserPrivacyPreferences.SingleOrDefaultAsync(p => p.UserId == profile.UserId, ct);
        if (profile.AccountStatus != AccountStatus.Active || !user.EmailConfirmed || !profile.OnboardingCompleted || privacy is null)
            throw new AuthException("ACCOUNT_UNAVAILABLE", 403);
        var newPlaintext = AuthCryptography.GenerateRefresh();
        var child = new SessionRefreshToken { Id = Guid.NewGuid(), SessionId = session.Id, TokenHash = AuthCryptography.RefreshHash(newPlaintext), CreatedAt = now };
        db.SessionRefreshTokens.Add(child);
        token.UsedAt = now; token.ReplacedByTokenId = child.Id;
        session.Slide(now);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(jwt.Access(user.Id, session.Id), newPlaintext, AuthOptions.AccessLifetimeSeconds, ToAuthUser(user, profile, privacy));
    }

    public async Task LogoutAsync(Guid userId, Guid sessionId, bool all, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockUserAsync(userId, ct);
        var sessions = await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null && (all || s.Id == sessionId)).ToListAsync(ct);
        foreach (var session in sessions) await RevokeAsync(session, all ? "logout_all" : "logout", Now, ct);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }

    public async Task<bool> IsAccessAllowedAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var now = Now;
        return await (from profile in db.UserProfiles
                      join user in db.Users on profile.UserId equals user.Id
                      join privacy in db.UserPrivacyPreferences on user.Id equals privacy.UserId
                      join session in db.UserSessions on user.Id equals session.UserId
                      where user.Id == userId && session.Id == sessionId && user.EmailConfirmed && profile.OnboardingCompleted
                            && profile.AccountStatus == AccountStatus.Active && session.RevokedAt == null
                            && session.IdleExpiresAt > now && session.AbsoluteExpiresAt > now
                      select user.Id).AnyAsync(ct);
    }

    public async Task<CurrentUserDto> MeAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new AuthException("ACCOUNT_UNAVAILABLE", 403);
        var profile = await db.UserProfiles.SingleAsync(p => p.UserId == userId, ct);
        var privacy = await db.UserPrivacyPreferences.SingleAsync(p => p.UserId == userId, ct);
        return ToDto(user, profile, privacy);
    }

    private Task<UserProfile> LockUserAsync(Guid userId, CancellationToken ct) => LockProfileAsync(
        db.UserProfiles.FromSqlInterpolated($"SELECT * FROM \"UserProfiles\" WHERE \"UserId\" = {userId} FOR UPDATE"), "ACCOUNT_UNAVAILABLE", ct);
    private Task<UserProfile> LockRegistrationAsync(Guid registrationId, CancellationToken ct) => LockProfileAsync(
        db.UserProfiles.FromSqlInterpolated($"SELECT * FROM \"UserProfiles\" WHERE \"RegistrationId\" = {registrationId} FOR UPDATE"), "VERIFICATION_UNAVAILABLE", ct);
    private static async Task<UserProfile> LockProfileAsync(IQueryable<UserProfile> query, string error, CancellationToken ct) =>
        await query.SingleOrDefaultAsync(ct) ?? throw new AuthException(error, 404);

    private (EmailVerificationChallenge Challenge, string Code) NewChallenge(Guid userId, DateTimeOffset now)
    {
        var id = Guid.NewGuid(); var code = AuthCryptography.GenerateCode();
        return (new EmailVerificationChallenge
        {
            Id = id,
            UserId = userId,
            CodeHash = AuthCryptography.CodeHash(options.Value.VerificationPepper, id, userId, code),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10)
        }, code);
    }

    private async Task<PendingRegistrationResponse> DeliverAsync(Guid registrationId, string email, string code, CancellationToken ct)
    {
        // Database commit precedes SMTP; delivery failure leaves a resumable pending account.
        try { await emailSender.SendAsync(email, code, ct); return new(registrationId, true, 600, 60, "sent"); }
        catch (Exception) when (!ct.IsCancellationRequested)
        { return new(registrationId, true, 600, 60, "failed", "VERIFICATION_DELIVERY_FAILED"); }
    }

    private (UserSession Session, string Token) CreateSession(Guid userId, DeviceMetadata? device, RequestMetadata metadata, DateTimeOffset now)
    {
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenFamilyId = Guid.NewGuid(),
            CreatedAt = now,
            LastUsedAt = now,
            IdleExpiresAt = now.AddDays(30),
            AbsoluteExpiresAt = now.AddDays(180),
            Platform = device?.Platform,
            DeviceName = device?.DeviceName,
            AppVersion = device?.AppVersion,
            LastIpAddress = Truncate(metadata.IpAddress, 45),
            LastUserAgent = Truncate(metadata.UserAgent, 256)
        };
        var token = AuthCryptography.GenerateRefresh();
        db.UserSessions.Add(session);
        db.SessionRefreshTokens.Add(new() { Id = Guid.NewGuid(), SessionId = session.Id, CreatedAt = now, TokenHash = AuthCryptography.RefreshHash(token) });
        return (session, token);
    }

    private async Task RevokeAsync(UserSession session, string reason, DateTimeOffset now, CancellationToken ct)
    {
        session.Revoke(now, reason);
        var tokens = await db.SessionRefreshTokens.Where(t => t.SessionId == session.Id && t.RevokedAt == null).ToListAsync(ct);
        foreach (var token in tokens) token.RevokedAt = now;
    }
    private static void CheckAccountStatus(UserProfile profile)
    {
        if (profile.AccountStatus == AccountStatus.Suspended) throw new AuthException("ACCOUNT_SUSPENDED", 403);
        if (profile.AccountStatus is AccountStatus.Deleted or AccountStatus.DeletionRequested) throw new AuthException("ACCOUNT_UNAVAILABLE", 403);
    }
    private static AuthException IdentityError(IdentityResult result) =>
        result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName") ? new("EMAIL_UNAVAILABLE", 409) :
        result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal)) ? new("PASSWORD_INVALID") : new("EMAIL_INVALID");
    private static AuthException UniqueError(string? constraint) => constraint == "UX_UserProfiles_NormalizedUsername" ?
        new("USERNAME_UNAVAILABLE", 409) : constraint is "UX_Identity_NormalizedEmail" or "UserNameIndex" ?
        new("EMAIL_UNAVAILABLE", 409) : new("REGISTRATION_CONFLICT", 409);
    private static int RemainingCooldown(DateTimeOffset created, DateTimeOffset now) => Math.Max(0, (int)Math.Ceiling((created.AddSeconds(60) - now).TotalSeconds));
    private static string MaskEmail(string email) => email[..1] + "***" + email[email.IndexOf('@')..];
    private static string? Truncate(string? value, int length) => value is { Length: var n } && n > length ? value[..length] : value;
    private static void ValidateDevice(DeviceMetadata? device)
    {
        if (device?.Platform?.Length > 32 || device?.DeviceName?.Length > 128 || device?.AppVersion?.Length > 32)
            throw new AuthException("DEVICE_METADATA_INVALID");
    }
    private static AuthUserDto ToAuthUser(ApplicationUser user, UserProfile profile, UserPrivacyPreferences privacy)
    {
        var own = ToDto(user, profile, privacy);
        return new(own.Id, own.Email, own.Username, own.DisplayName, own.AccountType, own.AccountStatus, own.OnboardingCompleted, own.Privacy);
    }
    private static CurrentUserDto ToDto(ApplicationUser user, UserProfile profile, UserPrivacyPreferences privacy) => new(
        user.Id, user.Email!, profile.Username, profile.DisplayName, profile.BirthDate, "user", profile.AccountStatus switch
        {
            AccountStatus.Active => "active",
            AccountStatus.PendingEmailVerification => "pendingEmailVerification",
            AccountStatus.Suspended => "suspended",
            AccountStatus.DeletionRequested => "deletionRequested",
            _ => "deleted"
        }, profile.OnboardingCompleted,
        new(privacy.AccountVisibility == AccountVisibility.Public ? "public" : "private",
            privacy.FollowPermission == FollowPermission.Everyone ? "everyone" : "requestRequired", privacy.Searchable,
            privacy.RecommendationsEnabled, privacy.ProfilePollVisibility == ProfilePollVisibility.Everyone ? "everyone" : "followersOnly",
            privacy.SocialListVisibility switch { SocialListVisibility.Everyone => "everyone", SocialListVisibility.FollowersOnly => "followersOnly", _ => "nobody" }));
}
