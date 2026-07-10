# web10-radio

Web10.Radio — планируемая круглосуточная Telegram-channel radio station для `@netscapedidnothingwrong` с Web 1.0 / Aero публичной сценой, Telegram bot interactions, PostgreSQL persistence, Telegram Stars payments и Dockerized runtime services.

## Текущее состояние backend

Backend scaffold уже есть в `src/backend/`:

- `Web10.Radio.API` — ASP.NET/F# host со startup config validation, health endpoints и B3 `/api/v0/*` endpoints.
- `Web10.Radio.Database` — PostgreSQL options, FluentMigrator migrations, ADO.NET session helpers и repository helpers.
- `Web10.Radio.Migrator` — отдельное one-shot migration app/container.
- `Web10.Radio.Telegram` — Funogram adapter module.
- `Web10.Radio.Tests` — NUnit + Testcontainers PostgreSQL integration tests, включая B3 API contract tests.

Канонические contracts остаются в `docs/SPEC.md`; состояние backend checklist — в `docs/PLAN-BACKEND.md`.

Текущий B3 HTTP API state:

- Public player routes `/api/v0/player/state`, `/events`, `/stream`, `/song`, `/health` реализованы.
- Telegram routes `/api/v0/telegram/webhook` и `/health` реализованы; webhook строго валидирует secret/body, парсит typed Funogram `Update`, атомарно dedupes `(updateId,eventType)` с durable outbox и relay-ит `/request`/`/say` в idempotent domain rows.
- Internal stream-node callbacks `/api/v0/stream-node/playback/{queueItemId}/lease|completion` защищены policy `Web10StreamNode`; lease renewal и authoritative completion fenced по owner/attempt.
- Весь `/api/v0/admin/*` group защищен policy `Web10Admin`: request должен передать `Authorization: Bearer <WEB10_ADMIN__TOKEN>`; missing/malformed/multiple/wrong credentials получают `401 admin.auth.required` и `WWW-Authenticate: Bearer`.
- Admin `GET /api/v0/admin/social-links` и `GET /api/v0/admin/donation-goal` реализованы; остальные listed admin routes зарегистрированы как explicit `501 admin.contract_unpinned` placeholders до pinning request/response bodies.

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

`WEB10_ADMIN__TOKEN` обязателен при запуске API и используется в точном заголовке `Authorization: Bearer <WEB10_ADMIN__TOKEN>` для admin routes. Отдельный `WEB10_STREAM__CALLBACK_TOKEN` защищает stream-node lease/completion callbacks; оба bearer secrets допускают только letters, digits, underscore и hyphen и не переиспользуют RTMP key. Compose содержит только тестовые non-production values. Для Local default storage задаются `WEB10_STORAGE__TYPE=Local` и `WEB10_STORAGE__LOCAL_ROOT`; S3 bucket/region/service URL и true force-path-style не задаются, а explicit `S3_FORCE_PATH_STYLE=false` допустим.

Для default S3 задаются `WEB10_STORAGE__TYPE=S3`, `WEB10_STORAGE__S3_BUCKET` и явный регион подписи `WEB10_STORAGE__S3_REGION`; `WEB10_STORAGE__LOCAL_ROOT` должен отсутствовать. Необязательный `WEB10_STORAGE__S3_SERVICE_URL` принимает абсолютный `http`/`https` URL, а `WEB10_STORAGE__S3_FORCE_PATH_STYLE=true|false` по умолчанию равен `false`. AWS SDK использует стандартную цепочку учетных данных; для собственного service URL настроенный регион сохраняется как `AuthenticationRegion`. Non-default S3 backend row использует bucket из PostgreSQL и стандартные AWS credential/region provider chains.

S3 scanner обходит `ListObjectsV2` page-by-page, renews the fenced scan lease перед каждой page, фильтрует поддерживаемые audio extensions и сохраняет metadata key/size через `TrackDiscovered`, не materializing full bucket list. На этапе discovery объект не скачивается: track остается `CachePath=None`, `IsCached=false` до отдельной materialization в cache.

Полный startup-validation contract и exact key literals находятся в `docs/SPEC.md` §9.

## Backend Compose smoke

`compose.yaml` — текущий infrastructure smoke path для PostgreSQL + migrator + API. Это еще не полный v0 stack: frontend, stream-node и optional observability collector остаются в следующих checklist phases.

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
curl -fsS \
  -H 'Authorization: Bearer compose-smoke-admin-token-1234567890' \
  http://localhost:8080/api/v0/admin/social-links

docker compose exec -T postgres \
  psql -U web10 -d web10 -tAc 'SELECT "Version" FROM "VersionInfo" WHERE "Version" IN (202607080001, 202607100001, 202607100002) ORDER BY "Version";'
```

`docker compose up --wait` ожидает Compose health state, а `--wait-timeout 120` ограничивает ожидание. В `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` healthcheck запускает доступный runtime command `dotnet Web10.Radio.API.dll --health-check http://127.0.0.1:8080/health/live`; managed self-probe выходит с code `0` только для successful HTTP response и не требует Alpine/libmusl, shell, `curl` или `wget` внутри container.

До первых `curl` ожидается:

- `postgres` — `healthy`.
- `migrator` — exited with code `0`.
- `api` — `healthy` на `http://localhost:8080`.

`/health/live` возвращает HTTP 200. Compose использует intentionally invalid Telegram token, поэтому operational `getMe` probe в `/health/ready` возвращает HTTP 503 с overall `Unhealthy`; это ожидаемо для smoke-config и доказывает, что configured-only token больше не считается рабочей зависимостью. При валидном production token checks `api`, `postgresql`, `storage`, `telegram-adapter` должны быть `Healthy`, а отсутствующий stream-node heartbeat остается `Degraded`. Authenticated admin request возвращает HTTP 200 и JSON array. Migration query должна вывести три примененные migration:

```text
202607080001
202607100001
202607100002
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

Observed 2026-07-10 after backend-analysis remediation:

- `dotnet build src/backend/Web10.Radio.sln -c Release --no-restore` succeeded with `0` warnings and `0` errors.
- `dotnet test src/backend/Web10.Radio.sln -c Release --no-build` passed `63/63` with `0` failed and `0` skipped.
- Focused `ApiContractTests` passed `19/19`; schema/policy fixtures passed `11/11`; workflow fixtures passed `9/9`.
- Isolated `docker compose -p web10-radio-final-0710 up --build --wait --wait-timeout 120 api` reached healthy API liveness without `sleep`, applied all three migrations, returned authenticated admin JSON, exposed operational readiness details, and left no containers, volumes, or network after cleanup.
