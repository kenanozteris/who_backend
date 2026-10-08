namespace Who.Domain.Accounts;

public enum AccountType { User }
public enum AccountStatus { PendingEmailVerification, Active, Suspended, DeletionRequested, Deleted }
public enum AccountVisibility { Public, Private }
public enum FollowPermission { Everyone, RequestRequired }
public enum ProfilePollVisibility { Everyone, FollowersOnly }
public enum SocialListVisibility { Everyone, FollowersOnly, Nobody }

public sealed class UserProfile
{
    public Guid UserId { get; set; }
    public Guid RegistrationId { get; set; }
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateOnly BirthDate { get; set; }
    public AccountType AccountType { get; set; } = AccountType.User;
    public AccountStatus AccountStatus { get; set; } = AccountStatus.PendingEmailVerification;
    public bool OnboardingCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class UserPrivacyPreferences
{
    public Guid UserId { get; set; }
    public AccountVisibility AccountVisibility { get; set; }
    public FollowPermission FollowPermission { get; set; }
    public bool Searchable { get; set; }
    public bool RecommendationsEnabled { get; set; }
    public ProfilePollVisibility ProfilePollVisibility { get; set; }
    public SocialListVisibility SocialListVisibility { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static UserPrivacyPreferences Preset(Guid userId, AccountVisibility visibility, DateTimeOffset now) => new()
    {
        UserId = userId,
        AccountVisibility = visibility,
        FollowPermission = visibility == AccountVisibility.Public ? FollowPermission.Everyone : FollowPermission.RequestRequired,
        Searchable = true,
        RecommendationsEnabled = true,
        ProfilePollVisibility = ProfilePollVisibility.Everyone,
        SocialListVisibility = SocialListVisibility.Everyone,
        UpdatedAt = now
    };
}
