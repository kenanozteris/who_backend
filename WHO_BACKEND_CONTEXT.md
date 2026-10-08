# WHO Backend — Source of Truth

Updated 8 October 2026 (implementation and runtime evidence: 7 October).: Auth Core V1 on codex/auth-core-v1.
Backend /Users/kenanozteris/Source/Repos/who_backend; baseline clean main 32e8d6e,
origin/main same Foundation checkpoint. Flutter /Users/kenanozteris/Source/Repos/who_app
read-only, clean at 78c6896; no integration/context edit.

## Foundation V1 retained

ASP.NET Core/PostgreSQL, separate repo, modular monolith, net10.0/SDK 10.0.401.
Domain pure(no EF/ASP.NET/Identity/HTTP); Application→Domain; Infrastructure→Domain+
Application; API→Application+Infrastructure. Docker PostgreSQL 18-alpine,
Mailpit v1.31.4; who_postgres_data at /var/lib/postgresql; local5432/1025/8025/API5080.
No native PostgreSQL/pgAdmin. Independent live, real ready, Development OpenAPI,
Production-safe ProblemDetails. Historical 11/11 coverage retained with auth-schema/
harness changes documented in AUTH_CORE_REPORT.

## Auth Core V1

Infrastructure ApplicationUser:IdentityUser<Guid> is auth only; Identity username
email, not WHO handle. Domain UserProfile shared UserId PK/FK, distinct random unique
RegistrationId, canonical Username/NormalizedUsername, exact Unicode DisplayName,
private DateOnly BirthDate, AccountType.User, AccountStatus, OnboardingCompleted,
UTC CreatedAt/UpdatedAt. RegistrationId pending handle grants no normal access.

Username exactly Flutter: Unicode-whitespace/BOM trim, one optional @ remove,
nonempty ASCII letters/digits/underscore, lowercase, no artificial length cap.
PostgreSQL text + DB-generated SHA256 key + UNIQUE UX_UserProfiles_NormalizedUsername
avoids large-text B-tree limits. Canonical ASCII/equality DB check; unique registration
handle/normalized email. DisplayName max 120 Unicode scalars is explicit backend
policy(Flutter has no max); no text/case/spacing normalization. BirthDate DATE,
timestamps UTC timestamptz. No stored age/age-band/safety-class.

Order:date parse→civil age→username/availability→display name→email/availability→
password→atomic Identity/profile/challenge commit→SMTP. TimeProvider UTC day,
civil arithmetic, Feb 29→March 1 non-leap matches Flutter. Exactly 13 allowed; under 13
before Identity creation/hash/email. Future/older than 130 years invalid. Password 10–128,
spaces/no classes, unchanged Identity hasher. Pending profile, no privacy/session.

EmailVerificationChallenge:Id/UserId/CodeHash/CreatedAt/ExpiresAt/AttemptCount/
ConsumedAt/InvalidatedAt. Secure decimal 6-digit RNG incl.leading zero; scoped HMAC-
SHA256 pepper, no plaintext.10min/exact deadline expired;60sec cooldown;5 failures,
fifth invalidates. Consumed/invalidated no reuse. Resend invalidates prior, avoids
historical code-value repetition. Profile FOR UPDATE locks + partial unique active
challenge index enforce concurrency. SMTP after DB commit;5sec timeout; Application
email abstraction/Infrastructure development-only Mailpit, WHO? bilingual 10 min/code
template. Failure returns 202 pending registrationId/deliveryStatus failed/
VERIFICATION_DELIVERY_FAILED; no half account, resend possible.

Email verification confirms Identity email but profile stays pending. 15 min onboarding
JWT sub/jti/typ=onboarding plus standard time/iss/aud, no sid/person/privacy/safety.
WhoOnboarding only; normal endpoints require WhoAccess. Privacy + activation +
first session atomic; replay 409 without duplicate. Privacy first created here.
Public:Public/Everyone/Searchable=true/RecommendationsEnabled=true.
Private:Private/RequestRequired/Searchable=true/RecommendationsEnabled=true.
Both ProfilePollVisibility=everyone/SocialListVisibility=everyone, Flutter canonical
defaults. Safety permissions never overridden by preferences. Active flows/policy
require privacy. Only public/private preset accepted, no arbitrary low-level fields.

Login email/password, optional bounded platform/device/app metadata. Unknown/wrong
same 401 INVALID_CREDENTIALS and Identity verification work on unknown email.
Correct unverified→409 EMAIL_VERIFICATION_REQUIRED(handle/maskedEmail/cooldown);
verified incomplete→409 ONBOARDING_REQUIRED(new onboarding token); no normal session
or login auto-email. Suspended/deleted/unavailable blocked. Each active login
independent session; no fixed device limit.

Access JWT 15 min sub/sid/jti/typ=access + iss/aud/iat/nbf/exp. No social/person/private/
safety claims. Strong env base64 HS256 key; issuer/audience/signature/algorithm/
lifetime validated, zero skew. Missing/weak pepper/key/issuer/audience fail fast,
no value logging. Production signing/key management/asymmetric strategy needs review.

UserSession:Id/UserId/TokenFamilyId, CreatedAt/LastUsedAt/IdleExpiresAt/AbsoluteExpiresAt,
revocation, bounded device/IP/UserAgent. IP/UserAgent not identity.
SessionRefreshToken:Id/SessionId/SHA256 TokenHash/CreatedAt/UsedAt/RevokedAt/
ReplacedByTokenId. URL-safe 256-bit opaque randomness; hash-only, retained used history.
Every refresh rotates, marks old used/links new, same family; idle=min(now+30days,
absolute), absolute fixed 180 days. Reuse suspicious family only, other devices survive.
Profile→session lock ordering; concurrent refresh at most one child, loser revokes
same family. Logout current; logout-all all. WhoAccess rereads mutable account/email/
onboarding/privacy/session state, immediate revoked/suspended denial, not JWT alone.

Separate own AuthUserDto summaries omit BirthDate; DOB only authenticated GET /me
CurrentUserDto. No public-user endpoint or private security material in DTOs/logs.
CamelCase/explicit strings, rejected unknown request fields. Stable codes in README.
Rates per source IP/fixed 60 sec/queue0:register5/login10/verify20/resend5/refresh30,
WHO_AUTH_RATE_* overrides1..10000.429 RATE_LIMIT_EXCEEDED/Retry-After60; challenge
limits DB-authoritative, no CAPTCHA, no default proxy-header trust.

Migration 20261007125527_InitialAuthAndUser:Identity + five WHO auth tables, no social.
Explicitly applied to existing empty dev DB. No startup Migrate/EnsureCreated/wipe.
WHO/history FK RESTRICT; standard Identity internal cascades blocked behind profile.
Deletion/audit design future. Real disposable PostgreSQL 18 migration tests, controlled
sender/clock; no SQLite/InMemory. Functional rates10000, default limiter separately
tested; actual formatted log guards + real-Mailpit output scans.
Final **39 unit + 35 integration = 74/74**, no skips ; 0 build warnings/errors.
Foundation 11 retained:7 unit unchanged;4 integration coverage retained with model/
migration assertion and test-only exception middleware/harness updates.
Reproducible commands/evidence/synthetic users:README and AUTH_CORE_REPORT.

## Recovery and future

Recovery mechanism still product-review/future; no forgot/reset/change endpoint,
SMS/magic-link/deep-link/reset-code UX invented. Future successful password
change/reset MUST revoke all sessions.
Next Flutter Auth Integration V1, secure token storage later.
Later External Auth Apple/Google; Profile Photo/Media/object storage; Social Graph;
Poll/Notifications/Daily server authority; Business; WHO Coin/sponsorship.
Backend future authoritative; Flutter persistence future cache. None implemented here.

## Boundaries

Never log password/code/access-refresh-reset token/private key/JWT key/pepper/secret.
.env ignored, no HTTP-body/sensitive EF/provider error-detail logging, no extra
framework/provider/deployment. No Flutter/stage/commit/push/SDK/nativePG install or
unrelated container changes. Reviewable local branch; PostgreSQL/Mailpit may stay
running. Preserve .env/volume; never down -v or DB/volume drop.
