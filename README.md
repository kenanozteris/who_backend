# WHO Backend — Auth Core V1

ASP.NET Core / PostgreSQL modular monolith on Foundation checkpoint 32e8d6e.
Flutter is separate and unchanged. USER email/password registration, verification,
privacy onboarding, sessions and refresh rotation are implemented. External auth,
recovery, social/media domains, Flutter integration and production deployment are future work.

## Prerequisites and architecture

Installed SDK **10.0.401** is pinned; all six projects target net10.0.
Docker must be running. Verified: macOS 27.0.1 (26A434)/arm64, Git 2.54.0,
Docker client/server 29.8.2, Compose 5.5.1. No native PostgreSQL/pgAdmin or SDK installer.

| Project | Responsibilities / references |
| --- | --- |
| Who.Domain | Pure WHO models/rules; no EF/ASP.NET/Identity/HTTP |
| Who.Application | DTOs, IAuthService, IVerificationEmailSender; Domain |
| Who.Infrastructure | Identity/WHO EF, crypto, transaction orchestration, SMTP; Domain + Application |
| Who.Api | HTTP, DI, JWT validation/policies, rate limits, health/OpenAPI; Application + Infrastructure |
| Who.UnitTests | Rules, boundaries, config, crypto and architecture |
| Who.IntegrationTests | WebApplicationFactory + migrated disposable PostgreSQL 18 |

## Local setup

From repo root, preserve any existing .env and development volume:

~~~sh
python3 scripts/dev-secrets.py
scripts/dev-up.sh
dotnet restore --locked-mode
dotnet tool restore
set -a
source .env
set +a
dotnet ef database update --project src/Who.Infrastructure --startup-project src/Who.Api
dotnet build --no-restore
scripts/dev-api.sh
~~~

dev-secrets.py copies .env.example only if .env is absent, fills missing secrets
with secure randomness, preserves existing values and chmods .env to 600. It prints
no secret. Do not rotate an existing volume's DB password, JWT key or pepper by
replacing .env. JWT key/pepper must be base64 with ≥32 decoded bytes; issuer/audience
nonempty. DB/auth config fail fast with key names only, no values. External
ConnectionStrings__Who overrides WHO_POSTGRES_* fields. API does not auto-load .env;
scripts export it into their child process.

Scripts find docker on PATH or /Applications/Docker.app/Contents/Resources/bin/docker.
For direct CLI commands on this Mac, set PATH in the current terminal only:

~~~sh
export PATH="/Applications/Docker.app/Contents/Resources/bin:$PATH"
docker compose config --quiet
docker compose up -d --wait --wait-timeout 120
docker compose ps
~~~

| Service | Local address |
| --- | --- |
| PostgreSQL postgres:18-alpine | 127.0.0.1:5432 |
| Mailpit axllent/mailpit:v1.31.4 | SMTP localhost:1025; inbox http://localhost:8025 |
| API | http://127.0.0.1:5080 |

Report port conflicts; do not silently move services. who_postgres_data mounts
at /var/lib/postgresql for PG18; pg_isready and Mailpit readyz must be healthy.
WHO_SMTP_HOST/PORT/FROM defaults:127.0.0.1/1025/WHO? <no-reply@who.local>.
SMTP has no dev auth/TLS and is development-only; no production provider.

## Schema and migration

First real migration: **20261007125527_InitialAuthAndUser**. Standard Identity schema,
UserProfiles, UserPrivacyPreferences, EmailVerificationChallenges, UserSessions,
SessionRefreshTokens; no social tables. Startup runs no Migrate/EnsureCreated.

~~~sh
dotnet ef migrations list --project src/Who.Infrastructure --startup-project src/Who.Api
dotnet ef database update --project src/Who.Infrastructure --startup-project src/Who.Api
# Future real model changes only:
dotnet ef migrations add NameOfRealChange --project src/Who.Infrastructure \
  --startup-project src/Who.Api --output-dir Persistence/Migrations
~~~

UserProfile PK/FK UserId equals Identity.Id. RegistrationId is a distinct random
pending handle, not authorization. Social username is only in UserProfile;
Identity.UserName is an email. Flutter canonical matching: Unicode-whitespace/BOM
trim, remove one optional @, nonempty ASCII letters/digits/underscore, lowercase,
no length cap. Canonical PostgreSQL text has a generated SHA256 bytea key with
UNIQUE index UX_UserProfiles_NormalizedUsername, avoiding B-tree large-text limits.
DB check enforces lowercase ASCII canonical equality. NormalizedEmail,
RegistrationId, active challenge per user, family ID and refresh hash are unique.
WHO/history FKs RESTRICT physical deletion; Identity internal cascades retained
behind profile restriction. Deletion/audit semantics remain future design.

DisplayName preserves exact Unicode text; backend max 120 Unicode scalars (Flutter
has no explicit display-name max). BirthDate is DateOnly/DATE, private. Explicit
TimeProvider UTC reference day and civil month/day arithmetic; Feb 29 advances on
March 1 in non-leap years, matching Flutter. Exactly 13 allowed; future dates or
older than 130 years invalid. No age/age-band/safety-class persisted. UTC timestamptz.
Password 10–128, spaces allowed, no case/digit/symbol requirement; unmodified
Identity password hasher.

## HTTP contracts and flow

Paths below are relative to /api/v1. CamelCase DTOs, explicit enum strings,
no EF entity serialization; unknown request properties rejected.

| Method/path | Authorization | Success |
| --- | --- | --- |
| POST /auth/register | anonymous |202 pending|
| POST /auth/verify-email | registrationId+code |200 onboarding token|
| POST /auth/resend-verification | registrationId |202 pending|
| POST /onboarding/privacy | onboarding token only |200 pair + own summary|
| POST /auth/login | email/password |200 pair; pending states structured409|
| POST /auth/refresh | opaque refresh in body |200 rotated pair|
| POST /auth/logout | access + active session |204 current revoked|
| POST /auth/logout-all | access + active session |204 all revoked|
| GET /me | access + active session |200 own DTO|

Register example (placeholders are not real credentials):

~~~json
{"birthDate":"2000-01-01","username":"example_user","displayName":"İpek Öztürk","email":"example@example.test","password":"<10-to-128-character-password>"}
~~~

Validation order: date parse → age → username/canonical availability → display name
→ email validation/availability → password → Identity/profile/challenge.
Under 13 never reaches Identity creation/hash/email. Account/profile/challenge commit
atomically in one context/transaction, then SMTP outside it. Pending response:

~~~json
{"registrationId":"<guid>","verificationRequired":true,"expiresInSeconds":600,"resendAvailableInSeconds":60,"emailDeliveryStatus":"sent","code":null}
~~~

SMTP failure still returns 202, emailDeliveryStatus=failed and
code=VERIFICATION_DELIVERY_FAILED with resumable registrationId. No rollback,
network exception/password/code disclosure. Bounded SMTP timeout 5 sec.

Read the six-digit code from Mailpit; no backend debug endpoint. Verify body:
registrationId and code. Response: emailVerified=true/onboardingRequired=true,
onboardingToken, expiresIn=900. Email confirmation alone does not activate.
Resend body: registrationId. Login never auto-sends mail.

Verification uses secure decimal RNG, leading zero supported, scoped HMAC-SHA256
pepper, no plaintext DB code. Expiry 10 min (exact ExpiresAt expired), cooldown 60 sec,
max 5 failures; fifth invalidates. Consumed/invalidated cannot reuse. Resend
invalidates previous active challenge and avoids reissuing historical code values
in the same pending flow. Profile FOR UPDATE serializes verify/resend; partial
unique index allows one unconsumed/uninvalidated challenge per user.

Onboarding body is only {"accountVisibility":"public"} or "private".
Public→Public/Everyone; private→Private/RequestRequired. Both Searchable and
RecommendationsEnabled true; both profilePollVisibility/socialListVisibility
everyone, matching Flutter defaults. Privacy is first created at onboarding.
Privacy + Active/completed profile + first session commit atomically. Replay409,
no duplicate record/session. Client cannot send arbitrary low-level preferences.

Login body has email/password and optional device (platform≤32, deviceName≤128,
appVersion≤32). Unknown email/wrong password both401 INVALID_CREDENTIALS; unknown
email also incurs Identity verification work. Correct unverified→409
EMAIL_VERIFICATION_REQUIRED with registrationId/maskedEmail/cooldown; verified
incomplete→409 ONBOARDING_REQUIRED with new onboarding token. No normal sessions
for either. Suspended/deleted/unavailable blocked.

Normal pair: accessToken, refreshToken, accessTokenExpiresInSeconds=900, user summary.
BirthDate appears only in authenticated own GET /me, not pair summaries, verification,
JWTs or logs. No public-user endpoint. Access JWT payload:sub/sid/jti/typ=access;
onboarding:sub/jti/typ=onboarding, no sid; both standard iss/aud/iat/nbf/exp.
HS256 strong environment key; issuer/audience/signature/algorithm/lifetime validated,
zero clock skew. Production signing/key-management/asymmetric strategy needs review.

Opaque URL-safe 256-bit refresh; only SHA256 hash/history stored. Every success marks
old token used, links replacement, keeps same session/family, slides idle to
min(now+30days,absolute). Absolute fixed 180 days. Reuse revokes suspicious family only;
other devices survive. Concurrent refresh has at most one child and loser triggers
same revocation. Profile→session lock ordering serializes mutations.
Logout current family; logout-all all. WhoAccess checks mutable account/email/
onboarding/privacy/session state on each protected request: immediate revocation/
suspension rejection, not JWT alone. IP/UserAgent bounded metadata, never identity;
no fixed device limit.

## Rate limits and ProblemDetails

Separate per-source-IP fixed 60 sec windows, queue0:

| Policy / environment override | Permits |
| --- | --- |
|register / WHO_AUTH_RATE_REGISTER|5|
|login / WHO_AUTH_RATE_LOGIN|10|
|verify / WHO_AUTH_RATE_VERIFY|20|
|resend / WHO_AUTH_RATE_RESEND|5|
|refresh / WHO_AUTH_RATE_REFRESH|30|

Overrides1..10000. No proxy headers trusted by default.429 RATE_LIMIT_EXCEEDED with
conservative Retry-After60; five-attempt/cooldown rules remain DB-authoritative.

| HTTP | Stable codes |
| --- | --- |
|400|AGE_NOT_ELIGIBLE, BIRTH_DATE_INVALID, USERNAME_INVALID, DISPLAY_NAME_INVALID, EMAIL_INVALID, PASSWORD_INVALID, VERIFICATION_CODE_INVALID, VERIFICATION_CODE_EXPIRED, PRIVACY_PRESET_INVALID, DEVICE_METADATA_INVALID, REQUEST_INVALID|
|401|INVALID_CREDENTIALS, INVALID_REFRESH_TOKEN, SESSION_EXPIRED, SESSION_REVOKED, REFRESH_TOKEN_REUSE_DETECTED, UNAUTHORIZED|
|403|ACCOUNT_SUSPENDED, ACCOUNT_UNAVAILABLE, FORBIDDEN (including token-type/session-state denial)|
|404|VERIFICATION_UNAVAILABLE / ACCOUNT_UNAVAILABLE for missing handle/account; http_error for unmapped paths|
|409|USERNAME_UNAVAILABLE, EMAIL_UNAVAILABLE, REGISTRATION_CONFLICT, VERIFICATION_UNAVAILABLE for consumed/inapplicable flow, EMAIL_VERIFICATION_REQUIRED, ONBOARDING_REQUIRED, ONBOARDING_ALREADY_COMPLETED|
|429|VERIFICATION_ATTEMPTS_EXCEEDED, VERIFICATION_RESEND_TOO_SOON, RATE_LIMIT_EXCEEDED|
|202|VERIFICATION_DELIVERY_FAILED is a resumable pending-response code|
|500|unexpected_error; Production never exposes exception detail|

No forgot/reset/password-change endpoint. Recovery UX/token mechanism still
product-review/future; successful future password reset/change MUST revoke all sessions.

## Verify and shut down

~~~sh
curl --fail http://127.0.0.1:5080/health/live
curl --fail http://127.0.0.1:5080/health/ready
curl --fail http://127.0.0.1:5080/openapi/v1.json
curl --fail http://localhost:8025/readyz
scripts/test.sh --no-build
# API must be running; actual Mailpit full flow, redacted output only:
python3 scripts/auth-smoke.py
~~~

Live independent of DB; ready real connectivity200/503. Built-in Development OpenAPI
includes all9 endpoints/contracts, Production404. Functional tests use controlled
sender/clock and actual migrations on disposable PG18 with random port; no Compose
DB/volume or SQLite/InMemory. Functional rate overrides10000; default limiter tested.
Only changed C# sources are formatted/verified.

Docker Desktop user-only socket: scripts/test.sh supplies necessary variables.
Equivalent direct command:

~~~sh
export DOCKER_HOST="unix://$HOME/.docker/run/docker.sock"
export TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock
dotnet test --no-build
~~~

API Ctrl-C; services: scripts/dev-down.sh (docker compose down, keeps named volume).
Never down -v, volume removal, DB drop or unrelated container wipe. Smoke users may
remain in dev DB, tests never depend on them. Tooling retains no passwords/codes/tokens.

.env/build outputs/.verification ignored. NEVER track/log DB password, JWT key,
pepper, password, access/refresh/reset token, code or private key. No sensitive EF/
provider error detail or HTTP-body logging. Actual runtime log guards and real-smoke
output scans enforce this; no extra logging framework/provider/cloud deployment.

[Decisions](WHO_BACKEND_CONTEXT.md), [Auth evidence](AUTH_CORE_REPORT.md),
[historical Foundation report](VERIFICATION.md).
Primary guidance: [Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/customize-identity-model?view=aspnetcore-10.0),
[JWT](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0),
[rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).
