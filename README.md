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
- Telegram routes `/api/v0/telegram/webhook` и `/health` реализованы; webhook validates secret-token и dedupes raw updates.
- Admin `GET /api/v0/admin/social-links` и `GET /api/v0/admin/donation-goal` реализованы.
- Остальные listed admin routes зарегистрированы как explicit `501 admin.contract_unpinned` placeholders до pinning request/response bodies.

## Docker image policy

- Alpine и другие libmusl-based Docker images запрещены.
- Non-.NET infrastructure images используют Debian/Ubuntu-based variants, даже если они больше.
- .NET final/runtime images используют Microsoft .NET chiseled variants.
- SDK build stages используют официальные non-Alpine Microsoft SDK images, потому что Microsoft не публикует chiseled SDK images.

Текущие backend runtime images:

- API: `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`
- Migrator: `mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled`
- PostgreSQL smoke database: `postgres:17`

## Backend Compose smoke

`compose.yaml` — текущий infrastructure smoke path для PostgreSQL + migrator + API. Это еще не полный v0 stack: frontend, stream-node и optional observability collector остаются в следующих checklist phases.

Запуск из repository root:

```sh
docker compose build
docker compose up -d api
docker compose ps -a
```

Ожидаемое состояние services:

- `postgres` — `healthy`.
- `migrator` — exited with code `0`.
- `api` — `Up` на `http://localhost:8080`.

Проверка API health:

```sh
curl -fsS http://localhost:8080/health/live
curl -fsS http://localhost:8080/health/ready
```

`/health/ready` сейчас возвращает HTTP 200 с overall `Degraded`, пока не реализован stream-node heartbeat. Для этого smoke ожидаемые healthy checks: `api`, `postgresql`, `storage`, `telegram-adapter`.

Проверка migration state:

```sh
docker compose exec -T postgres \
  psql -U web10 -d web10 -tAc 'SELECT "Version" FROM "VersionInfo" WHERE "Version" = 202607080001;'
```

Ожидаемый output:

```text
202607080001
```

Observed 2026-07-08:

- `docker compose build` succeeded for `web10-radio-api:local` and `web10-radio-migrator:local`.
- `docker compose up -d api` started PostgreSQL, ran migrator to exit code `0`, then started API.
- `curl -fsS http://localhost:8080/health/live` returned `Healthy`.
- `curl -fsS http://localhost:8080/health/ready` returned HTTP 200 JSON with overall `Degraded` only because `stream-node-heartbeat` has not reported yet.
- Migration query returned `202607080001`.
- `docker compose down --volumes --remove-orphans` removed smoke containers, network, and volumes.

Очистка smoke resources:

```sh
docker compose down --volumes --remove-orphans
```

## Backend local checks

```sh
dotnet build src/backend/Web10.Radio.sln
dotnet test src/backend/Web10.Radio.sln --no-restore
```

Observed 2026-07-08:

- `dotnet test src/backend/Web10.Radio.sln --filter ApiContractTests` passed `9/9`.
- `dotnet build src/backend/Web10.Radio.sln` succeeded.
- `dotnet test src/backend/Web10.Radio.sln --no-restore` passed `46/46`.
