# WHO — Backend Foundation Bootstrap V1: doğrulama raporu

Tarih: 7 Ekim 2026. Görev tamamlandı; sonuçlar incelemeye hazır.
Backend: `/Users/kenanozteris/Source/Repos/who_backend`.

## Preflight

| Kontrol | Gözlenen sonuç |
| --- | --- |
| Flutter son commit | `78c6896 feat: polish visual identity` |
| Flutter Git durumu | `git status --short` boş; başlangıçta ve tamamlanırken temiz |
| macOS | `27.0.1`, build `26A434` |
| Mimari | `arm64` |
| Git | `2.54.0 (Apple Git-157)` |
| .NET SDK | `10.0.401`; global.json bu kurulu sürümü pinliyor |
| ASP.NET Core runtime | `10.0.12` |
| Docker client / server | `29.8.2 / 29.8.2`; daemon erişilebilir |
| Docker Compose | `v5.5.1` |

Flutter'ın Visual Polish checkpoint'i temiz commit olarak doğrulandı. Bu görevde
Flutter dosyaları değiştirilmedi ve Flutter testleri yeniden çalıştırılmadı;
2111/2111 önceki milestone checkpoint'idir. Backend başlangıcında hedef klasör
boştu ve mevcut Git repository içermiyordu. Portlar uygun bulundu. SDK, Docker,
native PostgreSQL veya pgAdmin kurulumu yapılmadı. Docker CLI PATH'te olmadığı
için kurulu `/Applications/Docker.app/Contents/Resources/bin/docker` kullanıldı;
shell profili değiştirilmedi. NuGet bağımlılıkları ve repo-local EF aracı restore edildi.

## Solution ve proje sınırları

```text
who_backend/
├── Who.slnx
├── global.json
├── docker-compose.yml
├── .env.example
├── .gitignore
├── .config/dotnet-tools.json
├── README.md
├── WHO_BACKEND_CONTEXT.md
├── VERIFICATION.md
├── src/
│   ├── Who.Domain/
│   ├── Who.Application/
│   ├── Who.Infrastructure/
│   └── Who.Api/
├── tests/
│   ├── Who.UnitTests/
│   └── Who.IntegrationTests/
└── scripts/
    ├── common.sh
    ├── dev-up.sh
    ├── dev-down.sh
    ├── dev-api.sh
    └── test.sh
```

Altı proje de `net10.0`, nullable ve implicit usings açık. Domain hiçbir
proje/paket/framework referansı içermez. Application → Domain;
Infrastructure → Domain + Application; API → Application + Infrastructure.
UnitTests → Infrastructure; IntegrationTests → API. Döngüsel bağımlılık yok.
Modular monolith temeli kuruldu; ek CQRS/mediator/repository/event-bus framework yok.

| Ana doğrudan bağımlılık | Sürüm |
| --- | --- |
| Microsoft.EntityFrameworkCore / Design | 10.0.12 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 |
| Microsoft.AspNetCore.OpenApi / Mvc.Testing | 10.0.12 |
| Microsoft.Extensions.Configuration / Abstractions | 10.0.12 |
| Microsoft.NET.Test.Sdk | 18.10.1 |
| xunit | 2.9.3 |
| xunit.runner.visualstudio | 3.1.5 |
| Testcontainers.PostgreSql | 4.15.0 |
| Repo-local dotnet-ef | 10.0.12 |

Her projede NuGet `packages.lock.json` bulunur. Framework ve EF/Npgsql
PostgreSQL 18 uyumluluğu gerçek bağlantı testiyle doğrulandı; downgrade yapılmadı.

## Config ve Docker

Gerçek connection string tracked appsettings içinde değil. API, ortam/config
üzerinden `WHO_POSTGRES_DB`, `WHO_POSTGRES_USER`, `WHO_POSTGRES_PASSWORD` ve
isteğe bağlı host/port alıyor. `ConnectionStrings__Who` harici override olarak
destekleniyor. NpgsqlConnectionStringBuilder parola ayraçlarını koruyor.
Zorunlu config startup'ta doğrulanıyor; DB uptime ayrı readiness kontrolüdür.
Config olmadan başlatma HTTP listener açmadan exit 134 ile durdu:
`Database configuration is required: set WHO_POSTGRES_DB or ConnectionStrings__Who.`
Bu, beklenen fail-fast doğrulamasıdır.

| Servis | İmaj | Host erişimi | Son durum |
| --- | --- | --- | --- |
| PostgreSQL | postgres:18-alpine | 127.0.0.1:5432 | running, healthy |
| Mailpit | axllent/mailpit:v1.31.4 | SMTP 127.0.0.1:1025; Web 127.0.0.1:8025 | running, healthy |
| API | yerel dotnet process | 127.0.0.1:5080 | doğrulama sonrası kapatıldı |

PostgreSQL healthcheck `pg_isready`, Mailpit healthcheck `/mailpit readyz`.
Named volume `who_postgres_data`, PostgreSQL 18 için `/var/lib/postgresql`
parent dizinine mount edilir. Volume oluşturma zamanı
`2026-10-07T09:49:15Z`; stop/start sonrası aynı volume ve tarih korundu.
`dev-down.sh` yalnız `docker compose down` kullanır; volume silme bayrağı yok.
Normal down volume'u korur. Doğrulamada down -v veya volume silme yapılmadı.
Önceden çalışan `welcome-to-docker` container'ına dokunulmadı.

## API ve manuel smoke

| Senaryo | Live | Ready |
| --- | --- | --- |
| PostgreSQL açık | 200, Healthy | 200, Healthy |
| Yalnız WHO PostgreSQL servisi stop edildi | 200, Healthy | 503, Unhealthy |
| Aynı servis tekrar start edildi; API process aynı kaldı | 200 | 200, Healthy |

Development `/openapi/v1.json` HTTP 200. Built-in OpenAPI JSON hazır; ek Swagger
UI paketi yok. Business/auth endpoint henüz olmadığı için business paths boş
olabilir. Production'da OpenAPI 404 olduğu integration testinde doğrulandı.
Future business route convention `/api/v1/...`.

ProblemDetails status ve küçük `code` extension ile yapılandırıldı.
Production 404 → application/problem+json ve http_error;
sentetik 500 → unexpected_error; exception ayrıntısının response body'ye
sızmadığı integration testinde doğrulandı. Full error catalog sonraki iş.

Mailpit Web ana sayfa ve `/readyz` HTTP 200; SMTP portunda `220 ... Mailpit ESMTP
Service ready` banner alındı. Uygulama email gönderme logic'i veya gerçek email
provider eklenmedi.

## Restore, build ve test

| Kontrol | Sonuç |
| --- | --- |
| dotnet restore --use-lock-file | başarılı, exit 0 |
| dotnet build --no-restore | başarılı, exit 0; 0 uyarı, 0 hata |
| scripts/test.sh --no-build | başarılı, exit 0 |
| Unit | 7 geçti, 0 başarısız, 0 atlandı |
| Integration | 4 geçti, 0 başarısız, 0 atlandı |
| Toplam | **11/11 geçti** |
| dotnet tool restore / dotnet ef --version | başarılı; 10.0.12 |
| dotnet ef dbcontext info | Npgsql provider, who_dev, tcp://127.0.0.1:5432 |
| docker compose config --quiet / up / ps | geçerli; iki servis healthy |

Unit testler zorunlu config eksiklikleri, parola ayraçları, harici connection
string override, geçersiz port ve proje mimarisi sınırlarını kapsar. Integration
testler gerçek disposable PostgreSQL 18 ve WebApplicationFactory kullanır:
EF bağlantısı/boş model, health, DB kesintisi/kurtarma, Development OpenAPI,
Production ProblemDetails ve exception detail gizliliği.

Geçici test DB'si rastgele host portu ve runtime-generated password kullanır;
Compose `.env`, who_dev veya who_postgres_data'ya bağımlı değildir. Docker
stop/start rastgele portu değiştirdiği için integration kurtarma kontrolü yeni
endpoint ile test hostunu tekrar oluşturur. Sabit Compose portunda aynı API
process'inin kurtarması ayrıca manuel doğrulandı. Geçici test container'ları
fixture/resource reaper tarafından temizlendi. SQLite/InMemory EF yok.

Son test kanıtları ignored `.verification/test.log` ve aşağıdaki TRX dosyaları:
- `.verification/TestResults/final_net10.0_20261007150139.trx`
- `.verification/TestResults/final_net10.0_20261007150201.trx`
Derleme/restore/EF/fail-fast/API logları da `.verification/` altında tutulur.
Önceki teşhis çalıştırmaları son kabul sonucuna dahil değildir.

## Güvenlik ve kapsam

- `.env` gitignored, chmod 600, Git index'te yok.
- Gerçek local parola nonignored dosyalarda bulunmadı; secret rapora yazılmadı.
- appsettings yalnız logging/host config; parola/token/key içermiyor.
- EF sensitive logging açılmadı; Npgsql IncludeErrorDetail ve PersistSecurityInfo false.
- README/context: password, access/refresh token, email code, reset token,
  private key ve secret ASLA loglanmaz.
- Native PostgreSQL, pgAdmin, sistem PostgreSQL daemon veya gerçek SMTP provider kurulmadı.
- WhoDbContext boş; business/auth entity, Identity table, Auth logic veya migration yok.
- Geliştirme DB'sinde public schema table sayısı 0; startup EnsureCreated/Migrate yok.
- Flutter dosyaları değiştirilmedi.

## Belgeler ve sonraki milestone

README kopyalanabilir `.env`, Compose, API, health, test, shutdown ve gelecekteki
EF migration komutlarını içerir. WHO_BACKEND_CONTEXT source-of-truth olarak
backend mimarisini, server authority/client cache rolünü, Guid/UUID UserId,
registration sırasını, 13 yaş sınırını, Public/Private onboarding değerlerini,
6 haneli verification kurallarını ve tüm session kararlarını kaydeder.
JWT 15 dk; hashed opaque refresh, her refresh'te rotation, suspicious session
reuse revocation, 30 gün idle / 180 gün absolute, multi-device, logout/logout-all
ve reset sonrası tüm session revoke kararları gelecek Auth Core içindir.
Apple/Google, Business, fotoğraf/object storage, sosyal/Daily/WHO Coin alanları
gelecek iş olarak belgelenmiştir. Bunlar bootstrap'ta uygulanmadı.

Önemli implementation dosyaları: src/Who.Api/Program.cs,
src/Who.Api/Health/PostgresReadinessCheck.cs,
src/Who.Infrastructure/Configuration/DatabaseConfiguration.cs,
src/Who.Infrastructure/DependencyInjection.cs,
src/Who.Infrastructure/Persistence/WhoDbContext.cs; ayrıca Compose ve scripts/.

## Son Git ve çalışma durumu

Backend branch: `codex/backend-foundation-bootstrap`. Yeni repo; henüz HEAD
commit'i yok. `git ls-files` boş, staged file yok, remote yok. Stage, commit,
push veya PR yapılmadı. Kaynak/config/belgeler untracked incelemeye hazır:

```text
?? .config/
?? .env.example
?? .gitignore
?? README.md
?? VERIFICATION.md
?? WHO_BACKEND_CONTEXT.md
?? Who.slnx
?? docker-compose.yml
?? global.json
?? scripts/
?? src/
?? tests/
```

PostgreSQL ve Mailpit healthy olarak açık bırakıldı; API kapatıldı.
Yeniden API başlatmak için repo root'ta `scripts/dev-api.sh` çalıştırın.
Sonuçlar kullanıcı incelemesine bırakıldı.
