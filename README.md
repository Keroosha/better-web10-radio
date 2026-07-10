# web10-radio

Web10.Radio — планируемая круглосуточная Telegram-channel radio station для `@netscapedidnothingwrong` с Web 1.0 / Aero публичной сценой, Telegram bot interactions, PostgreSQL persistence, Telegram Stars payments и Dockerized runtime services.

## Текущее состояние backend

Backend scaffold уже есть в `src/backend/`:

- `Web10.Radio.API` — ASP.NET/F# host со startup config validation, B4 `/api/v0/*` routes, durable Telegram workflows, Stars payment state machine и health endpoints.
- `Web10.Radio.Database` — PostgreSQL options, FluentMigrator migrations through `202607100004` (включая `FrontendBlockers`: `AdminUsers`/`AdminSessions`, stream-node control state), `pg_trgm` search indexes, ADO.NET transaction/session helpers и repositories.
- `Web10.Radio.Migrator` — отдельное one-shot migration app/container.
- `Web10.Radio.Telegram` — Funogram inbound adapter и единственная typed outbound bot boundary.
- `Web10.Radio.Tests` — NUnit + Testcontainers PostgreSQL integration tests для API, Telegram commands/callbacks, Stars payments, migrations, outbox и moderation.

Канонические contracts остаются в `docs/SPEC.md`; состояние backend checklist — в `docs/PLAN-BACKEND.md`.

Текущий B4 backend/API state:

- Public player routes `/api/v0/player/state`, `/events`, `/stream`, `/song`, `/health` реализованы.
- Telegram `/api/v0/telegram/webhook` — webhook-only v0 ingress: strict secret/body validation, typed Funogram mapping, durable command/callback/payment relay и synchronous pre-checkout acknowledgement с 8-second internal deadline.
- Localized RU/EN `/start`, `/help`, `/request`, `/say`, `/song`, `/terms`, `/paysupport` реализованы. `/request` использует PostgreSQL `pg_trgm`; request invoice стоит configured 100 Stars, `/say` invoice — configured 50 Stars в compose deployment values.
- Stars invoices используют `XTR`, empty provider token и один price item. Request queue и say moderation state изменяются только после `successful_payment`; charge id сохраняется для future refund operations.
- Internal stream-node callbacks `/api/v0/stream-node/playback/{queueItemId}/lease|completion` защищены policy `Web10StreamNode`; lease renewal и authoritative completion fenced по owner/attempt.
- Весь `/api/v0/admin/*` group защищен policy `Web10Admin`. Реализованы social-links/donation-goal reads и paid `/say` list/approve/reject moderation routes; остальные listed admin routes остаются explicit `501 admin.contract_unpinned` placeholders.

## Docker image policy

- Alpine и другие libmusl-based Docker images запрещены.
- Non-.NET infrastructure images используют Debian/Ubuntu-based variants, даже если они больше.
- .NET final/runtime images используют Microsoft .NET chiseled variants.
- SDK build stages используют официальные non-Alpine Microsoft SDK images, потому что Microsoft не публикует chiseled SDK images.

Текущие backend runtime images:

- API: `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`
- Migrator: `mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled`
- PostgreSQL smoke database: `postgres:17`

## Backend configuration contract

Admin routes используют session auth (не bearer): `WEB10_ADMIN__USERNAME` и `WEB10_ADMIN__PASSWORD` задают bootstrap admin, который создается/обновляется при старте. `POST /api/v0/admin/auth/login` устанавливает `HttpOnly` cookie `web10_admin_session`, а каждый mutating admin request требует заголовок `X-CSRF-Token` из ответа login. Отдельный `WEB10_STREAM__CALLBACK_TOKEN` защищает stream-node lease/completion callbacks (bearer secret 24+ символов из [A-Za-z0-9_-], не переиспользует RTMP key). `WEB10_TELEGRAM__UPDATE_MODE=Webhook|LongPolling` выбирает способ приема Telegram updates; MVP использует `LongPolling`, чтобы бот принимал `/say` и Stars без публичного webhook. Compose содержит только тестовые non-production values. Для Local default storage задаются `WEB10_STORAGE__TYPE=Local` и `WEB10_STORAGE__LOCAL_ROOT`; S3 bucket/region/service URL и true force-path-style не задаются, а explicit `S3_FORCE_PATH_STYLE=false` допустим.

Telegram request/say prices обязательны и не имеют runtime defaults: `WEB10_TELEGRAM__REQUEST_PRICE_STARS` и `WEB10_TELEGRAM__SAY_PRICE_STARS` должны быть positive invariant `Int32`. `compose.yaml` задает smoke/deployment values `100` и `50`; те же значения управляют localized copy, persisted order/message amount, pre-checkout validation и Telegram `LabeledPrice`.

Для default S3 задаются `WEB10_STORAGE__TYPE=S3`, `WEB10_STORAGE__S3_BUCKET` и явный регион подписи `WEB10_STORAGE__S3_REGION`; `WEB10_STORAGE__LOCAL_ROOT` должен отсутствовать. Необязательный `WEB10_STORAGE__S3_SERVICE_URL` принимает абсолютный `http`/`https` URL, а `WEB10_STORAGE__S3_FORCE_PATH_STYLE=true|false` по умолчанию равен `false`. AWS SDK использует стандартную цепочку учетных данных; для собственного service URL настроенный регион сохраняется как `AuthenticationRegion`. Non-default S3 backend row использует bucket из PostgreSQL и стандартные AWS credential/region provider chains.

S3 scanner обходит `ListObjectsV2` page-by-page, renews the fenced scan lease перед каждой page, фильтрует поддерживаемые audio extensions и сохраняет metadata key/size через `TrackDiscovered`, не materializing full bucket list. На этапе discovery объект не скачивается: track остается `CachePath=None`, `IsCached=false` до отдельной materialization в cache.

Полный startup-validation contract и exact key literals находятся в `docs/SPEC.md` §9.

## Backend Compose smoke

`compose.yaml` разворачивает PostgreSQL + migrator + API + `frontend` (nginx со stage на `/` и admin на `/admin/`, проксирует `/api`). Это Development vertical-slice env (`WEB10_DEV__FIXTURES_ENABLED=true`). Все еще отсутствует реальный `stream-node` (phase B5) и optional observability collector (OTLP export best-effort; без коллектора API остается healthy). Пока B5 не готов, поток можно сымитировать локально скриптом `scripts/fake-stream-node.py`, который шлет `live` heartbeats и продвигает очередь через lease/completion — стадия покажет LIVE.

Вся конфигурация живет в `.defaults.env` (committed рабочие defaults) и грузится через `env_file`, поэтому стек работает «из коробки» без всякой подготовки. Чтобы что-то переопределить (реальный bot token, admin password, RTMP key), создайте локальный `.env` (gitignored) только с нужными ключами — он перекрывает defaults; секреты вынесены в отдельный SECRETS-блок в `.defaults.env`. Admin password обязан быть 12–256 символов (короткие значения вроде `admin` валят startup).

Из repository root выполните bounded smoke. Cleanup trap удаляет containers, network и volumes и при success, и при ошибке; отдельный `sleep` не нужен:

```sh
set -eu
cleanup() {
  docker compose down --volumes --remove-orphans
}
trap cleanup EXIT INT TERM

docker compose up --build --wait --wait-timeout 120 api

curl -fsS http://localhost:8080/health/live
curl -sS -w '\nHTTP %{http_code}\n' http://localhost:8080/health/ready
curl -fsS -c /tmp/web10-admin.cookie -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"admin-password"}' \
  http://localhost:8080/api/v0/admin/auth/login
curl -fsS -b /tmp/web10-admin.cookie \
  http://localhost:8080/api/v0/admin/social-links

docker compose exec -T postgres \
  psql -v ON_ERROR_STOP=1 -U web10 -d web10 -tAc 'SELECT "Version" FROM "VersionInfo" WHERE "Version" IN (202607080001, 202607100001, 202607100002, 202607100003, 202607100004) ORDER BY "Version";'
docker compose exec -T postgres \
  psql -v ON_ERROR_STOP=1 -U web10 -d web10 -tAc "SELECT 1 / ((count(*) = 1)::int) FROM pg_extension WHERE extname = 'pg_trgm';"
docker compose exec -T postgres \
  psql -v ON_ERROR_STOP=1 -U web10 -d web10 -tAc "SELECT 1 / ((count(*) = 5)::int) FROM pg_indexes WHERE schemaname='public' AND indexname IN ('IX_Tracks_Active_Title_Trgm','IX_Tracks_Active_ArtistTitle_Trgm','UX_Payments_Active_InvoicePayload','UX_Payments_Active_PurposeEntity','UX_PlaybackQueue_Active_TrackRequest');"
```

`docker compose up --wait` ожидает Compose health state, а `--wait-timeout 120` ограничивает ожидание. В `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` healthcheck запускает доступный runtime command `dotnet Web10.Radio.API.dll --health-check http://127.0.0.1:8080/health/live`; managed self-probe выходит с code `0` только для successful HTTP response и не требует Alpine/libmusl, shell, `curl` или `wget` внутри container.

До первых `curl` ожидается:

- `postgres` — `healthy`.
- `migrator` — exited with code `0`.
- `api` — `healthy` на `http://localhost:8080`.

`/health/live` возвращает HTTP 200. Compose использует intentionally invalid Telegram token, поэтому operational `getMe` probe в `/health/ready` возвращает HTTP 503 с overall `Unhealthy`; это ожидаемо для smoke-config и доказывает, что configured-only token больше не считается рабочей зависимостью. При валидном production token checks `api`, `postgresql`, `storage`, `telegram-adapter` должны быть `Healthy`, а отсутствующий stream-node heartbeat остается `Degraded`. Authenticated admin request (после login) возвращает HTTP 200 и JSON array. Migration query выводит пять примененных migration, а следующие assertions возвращают `1` только при установленном `pg_trgm` и всех пяти B4 indexes:

```text
202607080001
202607100001
202607100002
202607100003
202607100004
```

API image заранее создает `/var/lib/web10/storage` и `/var/lib/web10/data-protection` с ownership runtime UID `1654`; fresh named volumes наследуют эти права, поэтому startup writeability validation работает без запуска chiseled container от root.

Для ручной cleanup вне smoke block используется та же idempotent команда:

```sh
docker compose down --volumes --remove-orphans
```

## Backend local checks

```sh
dotnet build src/backend/Web10.Radio.sln
dotnet test src/backend/Web10.Radio.sln --no-restore
```

Observed 2026-07-10 after B4 Telegram/Stars implementation:

- `dotnet restore src/backend/Web10.Radio.sln` succeeded; `dotnet build src/backend/Web10.Radio.sln -c Release --no-restore` succeeded with `0` errors.
- Focused modified fixtures passed `75/75`: Telegram bot `6`, payment repository `7`, migrations/metadata `12`, configuration/domain/API `41`, background workers `9`.
- `ApiContractTests` passed `27/27`, including authenticated say moderation, webhook actor mapping, group private-only behavior, and synchronous pre-checkout `204|503` mapping.
- `docker compose up --build --wait --wait-timeout 120 api` reached healthy API liveness without `sleep`; SQL assertions confirmed migration `202607100003`, `pg_trgm`, and all five B4 indexes; cleanup removed containers, network, and volumes.
