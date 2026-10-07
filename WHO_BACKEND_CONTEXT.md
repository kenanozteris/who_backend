# WHO_BACKEND_CONTEXT.md

Source of truth for WHO backend decisions. Bootstrap date: 7 October 2026.
Separate repository: /Users/kenanozteris/Source/Repos/who_backend.
Flutter repository is unchanged: /Users/kenanozteris/Source/Repos/who_app,
clean checkpoint 78c6896 feat: polish visual identity (2111-test milestone).

## Implemented — Backend Foundation Bootstrap V1

ASP.NET Core, PostgreSQL, modular monolith, net10.0, installed SDK 10.0.401 pinned.
Who.Domain is pure; Who.Application references Domain; Infrastructure references
Domain/Application; API composes Application/Infrastructure. EF Core 10.0.12,
Npgsql provider 10.0.3, empty WhoDbContext, deferred configuration validated at
startup, live/ready health endpoints, Development OpenAPI and ProblemDetails.
Docker PostgreSQL 18-alpine with named persistent volume, Mailpit v1.31.4 for
development email dependency, local-only exposed ports. No native PostgreSQL.
Tests use actual disposable PostgreSQL/Testcontainers + WebApplicationFactory.

No auth/business entities, Identity tables, registration/login, verification
challenge, code generation, JWT, refresh token/session, age policy, username
normalization, privacy-onboarding logic, forgot/reset password or email sending
is implemented. There is no migration until real Auth Core entities exist.
No mediator/CQRS/mapping/generic-repository/event-bus framework is added.

## Locked future architecture

Future UserId: Guid/UUID. Server-authoritative architecture; Flutter's local
persistence becomes a client cache later, not an authority for safety/access.
Transition and Flutter integration are future work. This bootstrap does not
declare production readiness or implement a shared backend/client auth flow.

## Locked registration flow — future Auth Core V1

BirthDate -> reject below 13 BEFORE account creation -> Username -> DisplayName
-> Email -> Password -> unverified account -> 6-digit email code -> privacy
onboarding -> Active WHO account. Username/displayName and birthDate come before
email verification. Below 13 creates no ASP.NET Identity user and sends no
verification email. These are decisions, not bootstrap implementations.

## Locked privacy onboarding

Public:
- AccountVisibility = Public
- FollowPermission = Everyone
- Searchable = true
- RecommendationsEnabled = true

Private:
- AccountVisibility = Private
- FollowPermission = RequestRequired
- Searchable = true
- RecommendationsEnabled = true

## Locked Auth V1

- USER accounts only; ASP.NET Core Identity; UUID UserId; email/password.
- BirthDate, username and displayName before email verification.
- Under 13: no Identity user and no verification email.
- 6-digit email verification code; 10-minute expiry; 60-second resend cooldown.
- Maximum 5 failed attempts; latest code invalidates the older one.
- Verification code is not stored plaintext and is NEVER logged.

## Locked session decisions

- Access JWT: 15 minutes.
- Opaque refresh token; server-side hash storage.
- Rotate on every refresh; detect reuse.
- Reuse revokes the suspicious session only.
- Idle expiration: 30 days; absolute expiration: 180 days.
- Multiple devices allowed; no fixed device limit.
- Logout revokes the current session; logout-all revokes all sessions.
- Password reset revokes all sessions.
- Secure Flutter token storage comes later.

No password, access/refresh token, email verification code, reset token, private
key or secret may be logged. No production email provider in this foundation.

## Future work

Next milestone: WHO — Auth Core V1. After that: Apple Sign In, Google Sign In,
Business account, real profile photo and object storage, Follow, Circle, Poll,
Notifications and Daily backend. WHO Coin/sponsorship come later. Locked flows
above must not be mistaken for already implemented behavior.

## Development and Git boundaries

PostgreSQL runs only via Docker Compose; no brew/system PostgreSQL/pgAdmin.
Mailpit SMTP localhost:1025 / Web localhost:8025, no dev SMTP auth/TLS required.
PostgreSQL localhost:5432; API localhost:5080. Do not silently change conflict
ports. who_postgres_data persists through normal docker compose down; no down -v.
Configuration/secrets via environment and ignored .env; no tracked password.
No SDK/runtime/system installation, Flutter file changes, stage, commit, push or
remote addition was performed for this milestone. See README for reproducible
commands and VERIFICATION for actual observed outcomes.
