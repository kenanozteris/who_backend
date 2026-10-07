# WHO Backend — Foundation Bootstrap V1

Separate ASP.NET Core / PostgreSQL modular-monolith foundation. Flutter lives in
the sibling `who_app` repository and is not changed by this milestone.
This milestone has health/configuration/EF/OpenAPI infrastructure only: no
registration, Identity user, JWT, refresh session, email sending or business entities.

## Prerequisites

- Installed .NET 10 SDK. `global.json` pins the verified **10.0.401** SDK with
  latest-patch roll-forward within its feature band; prereleases are disabled.
- Running Docker Desktop / Docker Engine with Docker Compose v2 or newer.
  Verified locally: macOS 27.0.1 (26A434), arm64, Git 2.54.0, Docker 29.8.2,
  Compose 5.5.1. No SDK/runtime installation is performed by repo scripts.
- Available host ports 5432 (PostgreSQL), 1025 (SMTP), 8025 (Mailpit Web),
  5080 (API). Report conflicts; do not silently move services.
- **No native PostgreSQL, pgAdmin or system PostgreSQL service is used.**

Scripts find `docker` on PATH, or the already-installed Docker Desktop CLI at
`/Applications/Docker.app/Contents/Resources/bin/docker` on macOS. This fallback
does not change shell profiles or install tools. For direct CLI commands on this
Mac, add its directory to the current terminal only:

```sh
export PATH="/Applications/Docker.app/Contents/Resources/bin:$PATH"
```

## Architecture

```text
Who.slnx
src/Who.Domain             pure domain; no framework/EF/HTTP packages
src/Who.Application        references Domain; future use cases
src/Who.Infrastructure     references Domain + Application; EF Core/Npgsql
src/Who.Api                references Application + Infrastructure; composition/HTTP
tests/Who.UnitTests        configuration and project-boundary checks
tests/Who.IntegrationTests WebApplicationFactory + disposable PostgreSQL 18
scripts/                  local development helpers
```

All projects target net10.0 with nullable and implicit usings. There are no
circular references, mediator/mapping/validation/CQRS/repository frameworks,
message brokers or event buses. Domain/Application are intentionally minimal.
Future business API routes follow `/api/v1/...`; health routes are operational
endpoints outside business API versioning.

## Local environment and services

Run from the repo root:

```sh
cp .env.example .env
# Edit .env and set WHO_POSTGRES_PASSWORD to your own local password.
# For shell-sourcing, quote values containing whitespace/shell metacharacters.
chmod 600 .env
scripts/dev-up.sh
```

An ignored .env with a generated local password may already exist from bootstrap
verification. Preserve it to keep access to the existing development volume.
Never put its password in tracked appsettings, scripts, examples or reports.

Equivalent commands with a working docker PATH:

```sh
docker compose config --quiet
docker compose up -d --wait --wait-timeout 120
docker compose ps
```

`config --quiet` validates without printing resolved passwords. PostgreSQL uses
`postgres:18-alpine` and pg_isready healthcheck. The named volume
`who_postgres_data` mounts at `/var/lib/postgresql`, the correct parent path for
the [official PostgreSQL 18 image](https://hub.docker.com/_/postgres).
Mailpit uses `axllent/mailpit:v1.31.4` and its readyz healthcheck. No downgrade to
an older PostgreSQL major is used. All published ports bind to 127.0.0.1.

| Service | Address |
| --- | --- |
| PostgreSQL | 127.0.0.1:5432 (inside container: 5432) |
| Mailpit SMTP | localhost:1025 (development, no authentication/TLS) |
| Mailpit inbox | http://localhost:8025 |
| API | http://127.0.0.1:5080 |

Mailpit is a dependency only; no real email provider or application email logic.

## API configuration and startup

```sh
dotnet restore --locked-mode
dotnet build --no-restore
scripts/dev-api.sh
```

The script exports the ignored .env into its own child process and starts the
API in Development; ASP.NET does not load .env automatically. Required DB values
are WHO_POSTGRES_DB, WHO_POSTGRES_USER, WHO_POSTGRES_PASSWORD. Host defaults to
127.0.0.1 and port to 5432. An externally supplied `ConnectionStrings__Who`
overrides separate DB fields (used by isolated integration tests/deployments).
Connection strings are constructed with NpgsqlConnectionStringBuilder, preserving
password delimiters. Required config is validated at startup, before requests;
missing/invalid config names are reported without secret values. DB uptime is
checked by readiness, so DB downtime does not prevent an otherwise configured
process from serving liveness.

```sh
curl --fail http://127.0.0.1:5080/health/live
curl --fail http://127.0.0.1:5080/health/ready
curl --fail http://127.0.0.1:5080/openapi/v1.json
curl --fail http://localhost:8025/readyz
```

- GET /health/live: 200 while the process is running, without a DB check.
- GET /health/ready: real EF/Npgsql connectivity, 200 when available, 503 on outage.
- GET /openapi/v1.json: built-in OpenAPI JSON in Development only; no extra UI
  package, Swagger CDN or auth endpoints. Operational health middleware is not
  a business API contract; the document can have empty business paths initially.
- ProblemDetails handles exceptions/status errors. Production 500 responses
  omit exception details. A small `code` extension is provided; future WHO error
  and validation catalogs can extend this, but are not implemented now.

See [ASP.NET health checks](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0),
[OpenAPI](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/overview?view=aspnetcore-10.0)
and [ProblemDetails](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0).

## Tests

```sh
scripts/test.sh
```

Equivalent commands on Docker Desktop with a user-only socket:

```sh
export DOCKER_HOST="unix://$HOME/.docker/run/docker.sock"
export TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock
dotnet test
```

On a standard Linux/default Docker socket, ordinary `dotnet test` works. Docker
must be running. Testcontainers uses a random host port and disposable PostgreSQL
18 with a runtime-generated password; it has no reference to .env or the Compose
database/volume. The resource reaper and fixture disposal clean only test-owned
resources. No SQLite/InMemory EF substitution, reusable shared test database or
fixed test port. Integration tests check real EF connectivity, empty business
model, healthy probes, outage/recovery, Development OpenAPI, Production
ProblemDetails and non-disclosure of exception detail. Unit tests cover required
configuration, password delimiters and architecture boundaries.
See [Testcontainers PostgreSQL](https://dotnet.testcontainers.org/modules/postgres/)
and [WebApplicationFactory guidance](https://dotnet.testcontainers.org/examples/aspnet/).

## EF tools and future migrations

```sh
dotnet tool restore
set -a
source .env
set +a
dotnet ef dbcontext info --project src/Who.Infrastructure --startup-project src/Who.Api
```

WhoDbContext is empty deliberately. No schema/table/empty migration or
EnsureCreated/Migrate startup side effect is added. The first real migration
belongs to the next **WHO — Auth Core V1** milestone. When actual entities exist:

```sh
dotnet ef migrations add InitialAuthCore --project src/Who.Infrastructure \
  --startup-project src/Who.Api --output-dir Persistence/Migrations
dotnet ef database update --project src/Who.Infrastructure --startup-project src/Who.Api
```

These migration commands are documented only; do not create a meaningless empty
bootstrap migration. EF tooling version 10.0.12 is repo-local.

## Shutdown and volume preservation

Stop the API with Ctrl-C. Stop development services with:

```sh
scripts/dev-down.sh
# equivalent: docker compose down
```

Normal down removes services/network but **preserves who_postgres_data**. Never
use down -v/--volumes or delete that volume during normal verification. Keep .env
along with the volume; changing its password does not update an initialized DB.
Compose services may remain running after bootstrap verification.

## Secrets and logging

.env, build output, test results and .verification evidence are gitignored.
No real passwords/connection strings are tracked. Production config comes from
environment/secret providers; the examples contain no real credentials.
Use default structured ASP.NET logging. NEVER log passwords, access tokens,
refresh tokens, email verification codes, reset tokens, private keys or secrets.
Sensitive EF logging and provider error-detail expansion are disabled. Do not
dump process environments or resolved Compose config into public logs. Synthetic
test values are not real secrets. No additional logging framework is used.

Backend decisions are in [WHO_BACKEND_CONTEXT.md](WHO_BACKEND_CONTEXT.md).
Bootstrap evidence/results are summarized in [VERIFICATION.md](VERIFICATION.md).
