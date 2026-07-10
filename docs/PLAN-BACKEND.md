# Web10.Radio — PLAN-BACKEND

Этот план предназначен для ChatGPT/OMP, чтобы реализовать `src/backend/*` и `src/stream-node/*` параллельно с frontend work. Он реализует Milestone BACKEND из `docs/SPEC.md`, предоставляет contracts consumed by `docs/PLAN-FRONTEND.md` и обязан удерживать `/api/v0/*` stable после старта frontend implementation. Зона ответственности: backend, Telegram adapter, PostgreSQL persistence, background workers, payments, observability, containers и stream-node runtime.

## 1. Контракты и ограничения

- [x] Read `docs/SPEC.md` sections 5 through 12 before coding.
- [x] Register every `/api/v0/player/*`, `/api/v0/telegram/*`, internal `/api/v0/stream-node/*`, and `/api/v0/admin/*` route pattern listed in `SPEC.md`; admin methods whose request/response contract is not pinned remain explicit `501 admin.contract_unpinned` placeholders.
- [x] Keep `Web10.Radio.Telegram` on Funogram.
- [x] Use F# for backend projects.
- [x] Use ADO.NET only for PostgreSQL access; no ORM and no Dapper.
- [x] Use soft delete with `IsDeleted`; never delete domain data with `DELETE` from application code.
- [x] Use `SELECT ... FOR UPDATE SKIP LOCKED` for queue/work claiming.
- [x] Fail application startup when required configuration is missing or a value violates an implemented validator.
- [ ] Use DI composition and high-performance structured logging.
- [ ] Add OTEL traces/metrics for API, Telegram, queue, payment, and stream-node flows.
- [x] Ban Alpine/libmusl Docker images; use Debian/Ubuntu images for non-.NET infrastructure.
- [x] Use Microsoft .NET chiseled images for final/runtime .NET containers.

### Phase B0 — Solution and runtime foundation

- [x] Create `src/backend/Web10.Radio.sln`.
- [x] Create F# projects `Web10.Radio.API`, `Web10.Radio.Telegram`, and `Web10.Radio.Database`.
- [x] Reference `Web10.Radio.Telegram` and `Web10.Radio.Database` from `Web10.Radio.API`.
- [x] Add container packaging for API and stream-node.
- [x] Add configuration binding types for all `WEB10_*` keys from `SPEC.md`.
- [x] Validate required config before host startup, aggregate actionable errors, and terminate before binding a port when validation fails.
- [x] Before `builder.Build()`, register Database, application services, Telegram adapter, background workers, API authentication/authorization services, health checks, and observability; after `Build()`, add authentication/authorization middleware, then map health and `/api/v0/*` endpoints.
- [x] Configure health endpoints for API, Telegram adapter, PostgreSQL, storage, and stream-node heartbeat.

### Phase B1 — Database, migrations, repositories

- [x] Create FluentMigrator migration layout owned by `Web10.Radio.Database`; migration versions use Int64 YYYYMMDDmmss.
- [x] Create separate `Web10.Radio.Migrator` app/container that applies pending migrations to latest.
- [x] Create first migration for the tables listed in `SPEC.md`.
- [x] Add `IsDeleted BOOLEAN NOT NULL DEFAULT false`, `CreatedAtUtc`, and `UpdatedAtUtc` to mutable tables.
- [x] Create partial indexes for active rows where useful using `WHERE "IsDeleted" = false`.
- [x] Enforce active track-file path uniqueness with explicit NULL semantics and forward-migrate legacy duplicates by rewiring references and soft-deleting orphan tracks.
- [x] Implement ADO.NET connection/transaction helpers.
- [x] Implement repository helpers that make soft-delete filtering the default.
- [x] Implement queue claim transaction using the exact SQL pattern from `SPEC.md`.
- [x] Add integration tests proving concurrent queue workers do not claim the same `PlaybackQueue` row.
- [x] Add integration tests proving soft-deleted rows and parents do not appear in normal reads or cached playback joins.

### Phase B2 — Domain events and background workers

- [x] Define event envelope fields from `SPEC.md` in F# domain types.
- [x] Implement in-process event dispatch with host-lifetime-bound `MailboxProcessor` agents for payment, Telegram command state, queue, library scan, and stream-node event handling.
- [x] Persist outbox events before restart-sensitive effects, with global ordering, claim owner/attempt fencing, and relay-only dispatch.
- [x] Deduplicate Telegram updates using `TelegramUpdateInbox` before emitting domain events; `/request` and `/say` durable events create idempotent domain rows instead of no-op dispatch.
- [x] Implement library scanner discovery for Local files and page-streamed S3 object metadata, renew the fenced scan lease per page, filter supported audio extensions, and emit `TrackDiscovered`; S3 discovery remains uncached until a separate cache path downloads the object.
- [x] Implement playback program claim/start plus 30-second fenced lease renewal and authoritative `Played|Failed` completion callbacks; terminal state and `PlaybackEnded` append commit atomically, and stale attempts recover without blocking the queue.
- [x] Implement cache path for tracks needed for streaming; cache misses are explicit `degraded` state, not silent fallback.

### Phase B3 — HTTP API

- [x] Implement `GET /api/v0/player/state` using the exact JSON fields from `SPEC.md`.
- [x] Implement `GET /api/v0/player/events` as SSE with event names from `SPEC.md`.
- [x] Implement `GET /api/v0/player/stream` returning audio when live and problem details when unavailable.
- [x] Implement `GET /api/v0/player/song` returning current track link/fallback payload.
- [x] Implement `GET /api/v0/player/health` returning stream health summary.
- [x] Implement `POST /api/v0/telegram/webhook` with strict secret/body validation, typed Funogram parsing, transactional update/event dedupe, and durable command-state dispatch.
- [x] Implement `GET /api/v0/telegram/health` with monotonic last update/error state.
- [x] Implement authenticated stream-node playback lease/completion callbacks with owner/attempt fencing and bounded bodies.
- [ ] Implement all admin routes listed in `SPEC.md`.
- [x] Add route-level logging fields: route, status, traceId, correlationId, elapsedMs.
- [x] Add API integration tests for player state/SSE/range streaming, complete admin auth matrix, typed Telegram webhook dedupe and domain effects, stream-node callback fencing/body limits, health routes, and problem-details errors.

### Phase B4 — Telegram bot and Stars payments

- [x] Configure Funogram bot token from `WEB10_TELEGRAM__BOT_TOKEN`.
- [ ] Support webhook mode through `/api/v0/telegram/webhook`; long polling is allowed only as a local development mode documented in config.
- [ ] Implement `/start` and `/help`.
- [ ] Implement `/request <query>` with library search suggestions and admin-review fallback.
- [ ] Implement `/say <text>` as paid message flow: create pending message, send Stars invoice, wait for `successful_payment`, then mark `PaidPendingModeration`.
- [ ] Implement `/song` with no-arg current track response and query-based suggestions.
- [ ] Implement `/terms` and `/paysupport` for Telegram Stars live readiness.
- [ ] Send Stars invoices with `currency = "XTR"`, `provider_token = ""`, and one price item.
- [ ] Answer `pre_checkout_query` within 10 seconds after validating pending order, amount, and currency.
- [ ] Deliver paid effects only after `successful_payment`.
- [ ] Store `telegram_payment_charge_id` for refunds.
- [ ] Add tests for pre-checkout rejection, successful payment idempotency, duplicate update dedupe, and moderation status transitions.

### Phase B5 — Stream-node

- [x] Create `src/stream-node/` with Dockerfile and runtime scripts.
- [ ] Start Xvfb and export a stable display such as `:99`.
- [ ] Start Chromium in kiosk mode pointed at `WEB10_STREAM__STAGE_URL`.
- [ ] Include Chromium flag `--enable-unsafe-swiftshader` for software WebGL.
- [ ] Use FFmpeg/x11grab to capture Chromium/X11 video.
- [ ] Create LiquidSoap script that reads backend metadata/queue state and builds the audio/video stream.
- [ ] Encode Telegram RTMP output with FFmpeg/LiquidSoap settings documented in `SPEC.md`.
- [ ] Push stream to `WEB10_STREAM__RTMP_URL` using `WEB10_STREAM__RTMP_KEY`.
- [ ] Report heartbeat and failure states to backend.
- [ ] Implement bounded restart policy and admin-visible failure reason.
- [ ] Add smoke checks for Xvfb, Chromium launch, LiquidSoap syntax, FFmpeg availability, and backend heartbeat.

### Phase B6 — Observability, Docker, verification

- [ ] Add LoggerMessage source-generated/high-performance logging wrappers or equivalent low-allocation logging pattern.
- [ ] Add OTEL tracing and metrics for API, Telegram updates, payment flow, queue claims, library scans, and stream-node callbacks.
- [ ] Add Docker Compose for PostgreSQL, API, frontend placeholder/service URL, stream-node, and optional observability collector.
- [x] Add Docker Compose smoke path for PostgreSQL, separate migrator, API startup, and a chiseled-compatible managed API liveness healthcheck.
- [x] Verify `docker compose up --build --wait --wait-timeout 120 api` applies migrations `202607080001`, `202607100001`, and `202607100002`, reaches healthy API liveness, and permits immediate `/health/*` requests without `sleep`.
- [x] Document backend Compose smoke commands, migration check, and Docker image policy.
- [ ] Add NUnit integration tests for database, API, Telegram, and stream-node contracts.
- [ ] Verify all apps can run in containers with required config.
- [x] Verify startup fails when required config keys are missing.
- [x] Verify no application repository executes `DELETE` against domain tables.
