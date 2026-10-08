using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Who.Domain.Accounts;
using Who.Domain.Authentication;
using Who.Infrastructure.Identity;

namespace Who.Infrastructure.Persistence;

public sealed class WhoDbContext(DbContextOptions<WhoDbContext> options) : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<UserPrivacyPreferences> UserPrivacyPreferences => Set<UserPrivacyPreferences>();
    public DbSet<EmailVerificationChallenge> EmailVerificationChallenges => Set<EmailVerificationChallenge>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<SessionRefreshToken> SessionRefreshTokens => Set<SessionRefreshToken>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<ApplicationUser>().HasIndex(u => u.NormalizedEmail).IsUnique().HasDatabaseName("UX_Identity_NormalizedEmail");
        var profile = model.Entity<UserProfile>();
        profile.ToTable("UserProfiles", table => table.HasCheckConstraint("CK_UserProfiles_Username",
            "\"NormalizedUsername\" ~ '^[a-z0-9_]+$' AND \"Username\" = \"NormalizedUsername\"")); profile.HasKey(p => p.UserId);
        profile.HasIndex(p => p.RegistrationId).IsUnique();
        // A fixed-size DB-generated key preserves Flutter's unlimited handles without PostgreSQL's B-tree tuple limit.
        profile.Property<byte[]>("NormalizedUsernameKey").HasColumnType("bytea")
            .HasComputedColumnSql("sha256(\"NormalizedUsername\"::bytea)", stored: true);
        profile.HasIndex("NormalizedUsernameKey").IsUnique().HasDatabaseName("UX_UserProfiles_NormalizedUsername");
        profile.Property(p => p.Username).IsRequired().HasColumnType("text");
        profile.Property(p => p.NormalizedUsername).IsRequired().HasColumnType("text");
        profile.Property(p => p.DisplayName).IsRequired().HasMaxLength(AccountRules.DisplayNameMaxLength);
        profile.Property(p => p.BirthDate).HasColumnType("date");
        profile.Property(p => p.AccountType).HasConversion<string>().HasMaxLength(32);
        profile.Property(p => p.AccountStatus).HasConversion<string>().HasMaxLength(32);
        profile.HasOne<ApplicationUser>().WithOne().HasForeignKey<UserProfile>(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
        var privacy = model.Entity<UserPrivacyPreferences>();
        privacy.ToTable("UserPrivacyPreferences"); privacy.HasKey(p => p.UserId);
        privacy.Property(p => p.AccountVisibility).HasConversion<string>().HasMaxLength(32);
        privacy.Property(p => p.FollowPermission).HasConversion<string>().HasMaxLength(32);
        privacy.Property(p => p.ProfilePollVisibility).HasConversion<string>().HasMaxLength(32);
        privacy.Property(p => p.SocialListVisibility).HasConversion<string>().HasMaxLength(32);
        privacy.HasOne<UserProfile>().WithOne().HasForeignKey<UserPrivacyPreferences>(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
        var challenge = model.Entity<EmailVerificationChallenge>();
        challenge.ToTable("EmailVerificationChallenges"); challenge.HasKey(c => c.Id);
        challenge.Property(c => c.CodeHash).IsRequired().HasMaxLength(64);
        challenge.HasIndex(c => c.UserId).IsUnique().HasDatabaseName("UX_Challenge_ActiveUser")
            .HasFilter("\"ConsumedAt\" IS NULL AND \"InvalidatedAt\" IS NULL");
        challenge.HasOne<UserProfile>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        var session = model.Entity<UserSession>();
        session.ToTable("UserSessions"); session.HasKey(s => s.Id);
        session.HasIndex(s => s.UserId); session.HasIndex(s => s.TokenFamilyId).IsUnique();
        session.Property(s => s.RevokedReason).HasMaxLength(64);
        session.Property(s => s.Platform).HasMaxLength(32); session.Property(s => s.DeviceName).HasMaxLength(128);
        session.Property(s => s.AppVersion).HasMaxLength(32); session.Property(s => s.LastIpAddress).HasMaxLength(45);
        session.Property(s => s.LastUserAgent).HasMaxLength(256);
        session.HasOne<UserProfile>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        var token = model.Entity<SessionRefreshToken>();
        token.ToTable("SessionRefreshTokens"); token.HasKey(t => t.Id);
        token.Property(t => t.TokenHash).IsRequired().HasMaxLength(64);
        token.HasIndex(t => t.TokenHash).IsUnique();
        token.HasOne<UserSession>().WithMany().HasForeignKey(t => t.SessionId).OnDelete(DeleteBehavior.Restrict);
        token.HasOne<SessionRefreshToken>().WithMany().HasForeignKey(t => t.ReplacedByTokenId).OnDelete(DeleteBehavior.Restrict);
        // WHO security/history records restrict physical deletion. No deletion endpoint.
        // Keep Identity's own internal mappings; deleting a principal is blocked by its profile.
    }
}
