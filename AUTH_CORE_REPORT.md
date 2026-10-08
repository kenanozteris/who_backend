# WHO — Auth Core V1: tamamlama raporu

Rapor: **8 Ekim 2026**. Kod, migration ve canlı akış doğrulamaları 7 Ekim 2026;
son dosya/Git/secret/model/format kontrolleri 8 Ekim 2026.
Auth Core V1 tamamlandı, kullanıcı incelemesine hazır. Stage/commit/push yok.

## Başlangıç ve güncel çalışma durumu

| Kontrol | Sonuç |
| --- | --- |
| Backend | /Users/kenanozteris/Source/Repos/who_backend |
| Başlangıç | Temiz main, HEAD 32e8d6e chore: bootstrap WHO backend foundation |
| origin/main | Başlangıçta aynı Foundation commit'i; remote değiştirilmedi |
| Çalışma branch'i | codex/auth-core-v1 |
| Son HEAD | 32e8d6e; yeni commit yok |
| Flutter | Temiz 78c6896 feat: polish visual identity; dosyaları/context değiştirilmedi |
| .NET | SDK 10.0.401, net10.0; mevcut global.json pin'i korundu |
| Toolchain (7 Ekim) | macOS 27.0.1 (26A434), arm64; Git 2.54.0; Docker client/server 29.8.2; Compose 5.5.1 |

**Güncel servis durumu:** 8 Ekim kontrolünde Docker daemon çalışmıyor,
docker compose komutu plugin'i bulamıyor. API 5080'de açık değil.
7 Ekim'de PostgreSQL/Mailpit healthy idi ve aşağıdaki gerçek akışlar geçti.
Bu rapor, kapalı servislere bugün tekrar canlı test yapılmış gibi sonuç belirtmez.
Docker/Compose kurulum veya sistem onarımı yapılmadı. Yeniden canlı çalıştırma
için Docker Desktop/Compose kullanılabilir olmalı; README komutları hazır.
Doğrulama sırasında volume silinmedi, DB drop/down -v yapılmadı;
welcome-to-docker veya başka Docker workload değiştirilmedi.

## Mimari ve modeller

Altı proje ve dependency direction korundu:
Domain saf; Application→Domain; Infrastructure→Domain+Application;
API→Application+Infrastructure. Domain'de EF/ASP.NET/Identity/HTTP yok.
Yeni framework, CQRS/mediator/repository/event bus yok.

- Infrastructure ApplicationUser : IdentityUser<Guid>, yalnız authentication identity.
  Identity UserName=email; WHO sosyal username/displayName/birthDate/privacy burada değil.
- Domain UserProfile: UserId PK/FK=Identity.Id, ikinci profile ID yok; ayrı random
  unique RegistrationId; Username/NormalizedUsername, DisplayName, DateOnly BirthDate,
  AccountType.User, AccountStatus, OnboardingCompleted, CreatedAt/UpdatedAt.
- AccountStatus: PendingEmailVerification, Active, Suspended, DeletionRequested, Deleted.
  Register pending; email doğrulama tek başına Active yapmaz.
- UserPrivacyPreferences: UserId PK/FK; visibility, follow permission, discovery,
  recommendations, profile-poll/social-list visibility ve UpdatedAt. Register'da
  fake/default kayıt yok; onboarding'de oluşturulur.
- EmailVerificationChallenge: Guid Id/UserId, CodeHash, CreatedAt/ExpiresAt,
  AttemptCount, ConsumedAt/InvalidatedAt.
- UserSession: Id/UserId/TokenFamilyId, creation/last-used/idle/absolute timestamps,
  revocation/reason, optional bounded device/platform/app/IP/UserAgent.
- SessionRefreshToken: Id/SessionId, TokenHash, CreatedAt/UsedAt/RevokedAt,
  ReplacedByTokenId. Used history saklanır; yalnız current hash tasarımı kullanılmadı.

Başlıca yeni paketler: Identity.EntityFrameworkCore 10.0.12,
Authentication.JwtBearer 10.0.12, System.IdentityModel.Tokens.Jwt 8.14.0,
Microsoft.Extensions.Hosting.Abstractions 10.0.12.
EF Core/Design 10.0.12 ve Npgsql provider 10.0.3 korundu; lock dosyaları güncellendi.

## Kayıt ve doğrulama sırası

POST /api/v1/auth/register:
birthDate ISO yyyy-MM-dd, username, displayName, email, password.

Sıra: tarih formatı → yaş → canonical username/availability → displayName →
email validation/normalization/availability → password → atomic account/profile/
challenge → SMTP. Under-13, Identity CreateAsync/hash/email aşamasına ulaşmaz.
TimeProvider UTC reference date + civil month/day arithmetic; duration/365 hesabı yok.
Tam 13. doğum günü izinli, bir gün eksik reddedilir; Feb29 non-leap March1 kuralı
Flutter ile aynı. Future/130 yıldan eski tarih BIRTH_DATE_INVALID.
Yaş/AgeBand/SafetyClass persist edilmez; BirthDate PostgreSQL DATE'dir.

Flutter Username.normalize/AccountIdentity READ-ONLY eşleşmesi:
outer Unicode whitespace/BOM trim; tek opsiyonel @ çıkar; nonempty ASCII letters,
digits, underscore; lowercase. Min 1, yapay max yok. Turkish/non-ASCII username
reddedilir; Unicode displayName korunur. 10.000 karakterlik yüksek çeşitlilikte
username gerçek PostgreSQL testiyle kabul edildi; duplicate güvenli409.
NormalizedUsername text, DB-generated SHA256 bytea key ve UNIQUE
UX_UserProfiles_NormalizedUsername canonical uniqueness'i sağlar. Bu seçim,
Flutter'ın sınırsız username'ini PostgreSQL B-tree büyük tuple sınırına takmaz.
CHECK lowercase ASCII canonical equality'yi zorlar.
DisplayName aynen korunur; backend açık max120 Unicode scalar. Flutter'da mevcut
displayName max bulunmadığından bu limit birebir kopya değildir, açık V1 politikasıdır.
Email trim + Identity normalization/validation, RequireUniqueEmail=true;
DB UNIQUE NormalizedEmail ve Identity normalized username index'i race'i kapatır.
Password 10–128, spaces dahil, zorunlu character class yok. Length validator özel;
password hashing unmodified ASP.NET Identity.

Identity/profile/challenge aynı DbContext ve transaction'da commit olur.
Unique violation rollback edilir, half account kalmaz; username/email409 stable code.
SMTP çağrısı transaction dışındadır (5sn timeout).
Başarı202: registrationId, verificationRequired=true, expiresInSeconds=600,
resendAvailableInSeconds=60, emailDeliveryStatus=sent.
registrationId != UserId; normal authorization/social access vermez.
Delivery failure DB'yi geri almaz: aynı202 ile registrationId,
emailDeliveryStatus=failed, code=VERIFICATION_DELIVERY_FAILED; cooldown sonrası
resend ile flow devam eder. Network exception/password/code/token response'a sızmaz.

## Email challenge ve onboarding token

CSPRNG decimal 000000–999999, leading zero destekli. HMAC-SHA256 input challengeId,
userId ve email-verification purpose ile scoped; güçlü env pepper, plaintext DB yok.
10dk expiry, tam ExpiresAt expired; 60sn resend; max5 wrong attempts.
İlk dört invalid; beşinci invalidate/VERIFICATION_ATTEMPTS_EXCEEDED; doğru kod da
bu challenge'ı kurtaramaz. Consumed/invalidated tekrar kullanılamaz.
Resend önceki aktif challenge'ı invalidate eder, yeni challenge/mail oluşturur;
aynı pending flow'da eski code value'nun yeniden rastgele üretilmesini de engeller.
Profile FOR UPDATE ve partial UNIQUE active-challenge index'i concurrent attempts/
resend'i serialize eder; concurrency testleri geçti.

Verify success: EmailConfirmed=true, challenge consumed; profile hâlâ pending,
onboarding=false, normal UserSession yok. Response emailVerified=true,
onboardingRequired=true, onboardingToken, expiresIn=900.
Onboarding JWT15dk: sub/jti/typ=onboarding, standard issuer/audience/time claims;
sid, username, birthDate, age, safety/privacy yok. /me için kabul edilmez.

## Privacy preset ve aktivasyon

Client sadece public/private seçebilir; ekstra low-level JSON fields400 REQUEST_INVALID.

| Alan | PUBLIC | PRIVATE |
| --- | --- | --- |
| AccountVisibility | Public | Private |
| FollowPermission | Everyone | RequestRequired |
| Searchable | true | true |
| RecommendationsEnabled | true | true |
| ProfilePollVisibility | everyone | everyone |
| SocialListVisibility | everyone | everyone |

Son iki alan Flutter lib/models/privacy_preferences.dart canonical defaults ile aynı.
Private otomatik discovery kapatmaz. Privacy create + profile Active/completed +
ilk session tek transaction. Stale onboarding replay409 ONBOARDING_ALREADY_COMPLETED;
concurrent iki completion yalnız bir privacy/session oluşturur.
Active flow ve WhoAccess policy privacy record zorunluluğunu kontrol eder.

## Login, JWT ve session güvenliği

Email/password login. Unknown email/wrong password aynı401 INVALID_CREDENTIALS;
unknown email için de Identity hash-verification işi yapılır.
Correct unverified409 EMAIL_VERIFICATION_REQUIRED: registrationId/maskedEmail/cooldown,
mail auto-send ve normal session yok.
Verified incomplete409 ONBOARDING_REQUIRED: yeni onboarding token, normal session yok.
Active+confirmed+completed+privacy→yeni bağımsız session/pair.
Suspended/account unavailable durumları normal token alamaz.

Access JWT15dk payload: sub/sid/jti/typ=access, iss/aud/iat/nbf/exp.
Username/displayName/birthDate/age/privacy/safety claim yok.
Strong base64 env HS256 key, minimum32 decoded bytes; issuer/audience/signature/
algorithm/lifetime validation, zero skew. Production signing/key-management/
asymmetric strategy product/production review konusu.
Eksik pepper startup'ı exit134 ile, secret değerini göstermeden durdurdu.

Refresh: URL-safe opaque32 random bytes (256bit), JWT değil; DB yalnız SHA256 hash.
Her success eski UsedAt/ReplacedByTokenId ve yeni token; family/session aynı.
LastUsed=now, idle=min(now+30days,absolute); absolute creation+180days sabit.
Used-token reuse yalnız suspicious family'yi ve onun replacement/history tokenlarını
revoke eder; diğer cihaz unaffected. Concurrent refresh bir child/success,
loser aynı reuse revocation uygulamasına girer.
Profile→session DB lock ordering mutation race'lerini kapatır.
Logout current; logout-all tüm aktif session/history. IP/UserAgent bounded metadata,
identity değil; multi-device, sabit limit yok.

Reusable WhoAccess policy: typ/sub/sid, Active user, EmailConfirmed,
OnboardingCompleted, privacy, owned unrevoked/unexpired session gerçek DB'den
kontrol edilir. JWT tek başına yeterli değil; revoked/suspended /me anında403.
WhoOnboarding ayrı policy; access JWT onboarding'e403, onboarding JWT /me'ye403.
GET /me own CurrentUserDto; private BirthDate sadece burada.
Token response AuthUserDto summary DOB içermez; public profile endpoint yok.
PasswordHash/SecurityStamp/CodeHash/TokenHash/key/internal security metadata DTO'da yok.
Password recovery/change mekanizması tasarlanmadı; endpoint yok.
Future başarılı reset/change ALL sessions revoke etmeli.

## API / HTTP / rate limits

| Endpoint | Success |
| --- | --- |
|POST /api/v1/auth/register|202|
|POST /api/v1/auth/verify-email|200 onboarding|
|POST /api/v1/auth/resend-verification|202|
|POST /api/v1/onboarding/privacy|200 pair|
|POST /api/v1/auth/login|200 pair|
|POST /api/v1/auth/refresh|200 rotated pair|
|POST /api/v1/auth/logout|204|
|POST /api/v1/auth/logout-all|204|
|GET /api/v1/me|200 own DTO|

CamelCase/explicit string enum DTOs, unknown request fields denied; no EF entities.
ProblemDetails stable codes/status mapping README'de tam listelenir:
validation400, credentials/refresh401, account/policy403, missing handle404,
uniqueness/pending/onboarding replay409, challenge/rate429, unexpected500.
SMTP delivery failure202 resumable code. Production exception details kapalı.

ASP.NET separate source-IP fixed60sn policies, queue0:
register5, login10, verify20, resend5, refresh30.
WHO_AUTH_RATE_* config overrides1..10000;429 RATE_LIMIT_EXCEEDED/Retry-After60.
Challenge5 attempts/cooldown bağımsız authoritative. Proxy headers trusted değil.
Functional tests limiter10000, dedicated default-login limit test10+1 ile429 doğrular.

Development OpenAPI200 ve dokuz endpoint/contracts gerçek API'de doğrulandı.
AuthUserDto schema DOB içermez, CurrentUserDto own schema içerir.
Production OpenAPI404. Live/ready foundation korundu:
DB açık200/200; WHO PostgreSQL stop→200/503; start→ready200 (aynı API process).

## Database / migration

20261007125527_InitialAuthAndUser.
Before dev migration public schema table count0; existing volume kullanıldı.
After: AspNetUsers/Roles/UserClaims/RoleClaims/UserLogins/UserRoles/UserTokens,
UserProfiles, UserPrivacyPreferences, EmailVerificationChallenges,
UserSessions, SessionRefreshTokens + __EFMigrationsHistory.
Social/business table yok; Role/External-login tabloları standard Identity schema,
bu feature'ların implementation'ı değil.

PK/FK shared UserId, privacy PK UserId. UNIQUE registration/canonical username key/
normalized email/active challenge/family/refresh hash. Token replacement FK retained.
WHO profile/challenge/privacy/session/history FKs RESTRICT; Identity internal
cascades unchanged, user delete profile restriction'a takılır. Deletion/audit future.
DATE BirthDate; UTC timestamptz security timestamps.
No startup Migrate/EnsureCreated. Snapshot model check8 Ekim: no pending model changes.

Gerçekte kullanılan komutlar:

~~~sh
set -a
source .env
set +a
dotnet ef migrations add InitialAuthAndUser --project src/Who.Infrastructure \
  --startup-project src/Who.Api --output-dir Persistence/Migrations
dotnet ef database update --project src/Who.Infrastructure --startup-project src/Who.Api
dotnet ef migrations has-pending-model-changes --project src/Who.Infrastructure \
  --startup-project src/Who.Api --no-build
~~~

## Tests ve kanıt

| Kontrol | Sonuç |
| --- | --- |
|dotnet restore --use-lock-file|Başarılı|
|dotnet build --no-restore|0 uyarı,0 hata|
|scripts/test.sh --no-build|Başarılı|
|Unit|39 passed,0 failed,0 skipped|
|Integration|35 passed,0 failed,0 skipped|
|Total|**74/74**|
|git diff --check|Temiz|
|Changed-source dotnet format whitespace --verify-no-changes --no-restore|Başarılı|
|EF pending model check|No model changes since last migration|

Baseline11 retained:7 unit unchanged;4 integration retained.
2 integration case body adjustments: empty model assertion becomes exact auth model/
migration+no-social assertion; test-only failure middleware now precedes terminal
routing and uses configured exception handler. Factory supplies test auth config,
fixture applies actual migration. No coverage deletion/skip.
New32 unit +31 integration =63. Age/leap/username boundaries, crypto/expiry/attempts,
privacy/session rules; real under13 no-row/no-email proof, full pending state, exact13,
password, normalization,10K username uniqueness, expiry/resend/SMTP failure,
public/private onboarding/type confusion/replay/concurrency, login states,
rotation/reuse/device isolation/concurrent refresh/logout/all/revoked/suspended,
session deadlines, uniqueness race, actual migrations, default limiter and log guards.
Tests use independent disposable PG18/random host port; never Compose DB/volume,
SQLite/InMemory or EnsureCreated. Fake sender/control clock for determinism.

Final TRX (7 Ekim) — yalnız bu completed run kabul sonucudur:
- .verification/AuthTestResults/auth-complete_net10.0_20261007165812.trx
- .verification/AuthTestResults/auth-complete_net10.0_20261007165825.trx
Build/restore/test/model/format/migration/redacted smoke logs .verification altında,
ignored. Earlier diagnostic failure records final result'a dahil edilmez.

## Gerçek Mailpit/manual flow ve güvenlik

7 Ekim scripts/auth-smoke.py actual Compose PostgreSQL + Mailpit ile iki full run:
under13→400 AGE_NOT_ELIGIBLE; Identity/profile/challenge counts unchanged, zero email.
Valid202→exactly one WHO? branded email per synthetic account,6-digit code and
TR/EN10min expiry; password/token yok. Actual emailed code→verify200→private
onboarding200→me200→login200→refresh200→old-token reuse401→replacement revoked401/
me403; independent session still200. Current logout204 leaves other session usable;
logout-all204 then both refresh401. No credentials/codes/tokens output/persistence.
Verification/refresh DB storage hash length64 confirmed; no plaintext columns.

Local dev accounts retained (not test dependencies, passwords/codes omitted):
- auth-smoke-574217067df24f1f9125978e71fde776@example.test
- auth-smoke-64144d686a7e4d93b6e11c3fc2fd7d8e@example.test

All smoke sessions revoked at flow end. Tooling retains no login credentials.
Evidence: .verification/auth-manual-smoke.log and auth-manual-smoke-final.log.

Actual formatted ASP.NET/EF log provider guards verify in-memory known passwords,
DOB, codes, issued tokens, test pepper/key stay out of logged messages; failure
reports redact values. Manual tooling scans actual evidence against its runtime
password/code/tokens/DOB before exit.
8 Ekim exact real config-secret scan:64 nonignored files +39 evidence files,
0 source hits,0 log hits,0 JWT-shaped log hits. Rapor dahil son tarama: 65 nonignored dosya + 40 kanıt dosyası; gerçek secret/JWT eşleşmesi 0.
.env ignored/chmod600, no staged secret. Dev-secrets initializer existing values
preserved test passed. EF sensitive/provider error detail and HTTP-body logging yok.
No private security fields in API; no token/code key in report.
No native PG/pgAdmin/SDK installation, real email provider, extra logging framework,
Flutter edits or production deployment.

## Önemli dosyalar / context / Git

Domain: src/Who.Domain/Accounts/AccountModels.cs, AccountRules.cs;
Authentication/EmailVerificationChallenge.cs, UserSession.cs.
Application: src/Who.Application/Authentication/AuthContracts.cs.
Infrastructure: Identity/ApplicationUser.cs; Authentication/AuthService.cs,
AuthCryptography.cs, AuthOptions.cs, JwtTokens.cs, PasswordLengthValidator.cs;
Email/MailpitVerificationEmailSender.cs; Persistence/WhoDbContext.cs +Migrations;
DependencyInjection.cs.
API: Program.cs; Authentication/AuthEndpoints.cs, AuthAuthorization.cs,
AuthExceptionHandler.cs, AuthRateLimits.cs.
Tests: AuthRulesTests.cs, AuthCoreTests.cs, AuthTestSupport.cs;
adjusted ApiFactory/PostgresFixture/FoundationTests.
Tooling/docs: .env.example, scripts/dev-secrets.py, scripts/auth-smoke.py,
README.md, WHO_BACKEND_CONTEXT.md, AUTH_CORE_REPORT.md, package locks.

WHO_BACKEND_CONTEXT now actual source-of-truth for identity/domain separation,
pending handle, canonical username, UTC civil age, privacy timing/presets,
verification/HMAC/Mailpit, JWT types, sessions/history/rotation/reuse/lifetimes,
logout/me/rates/migration/tests. Recovery product-review/future explicitly noted.
Next Flutter Auth Integration V1; later external auth/media/social backend.
No out-of-scope implementation.

Final branch codex/auth-core-v1, HEAD32e8d6e. Working tree modified/new reviewable
files; cached diff empty. Existing origin unchanged. **No stage, commit, push or PR.**
Flutter remains clean78c6896. Kullanıcı incelemesine bırakıldı.
