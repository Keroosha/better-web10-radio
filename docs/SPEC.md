# Web10.Radio — SPEC

Web10.Radio — это 24/7 радио для Telegram-канала `https://t.me/netscapedidnothingwrong`. Целевое состояние v0: контейнеризованная система, где backend владеет сканированием библиотеки, программой воспроизведения, платежами, модерацией, metadata, состоянием очереди и координацией stream-node; frontend рендерит публичную сцену и admin cabinet; stream-node захватывает stage и audio pipeline и отправляет RTMP в Telegram.

## 1. Репозиторий сейчас

- Текущий репозиторий содержит только `README.md`, `.idea/`, `Web 1.0-radio-scene.zip` и `src/frontend/web-stage/mocks/`.
- Сейчас отсутствуют `docs/`, `src/backend/`, `src/frontend/admin/`, `src/stream-node/`.
- `src/frontend/web-stage/mocks/README.md` описывает HTML/CSS/JS как design handoff: визуал нужно faithfully recreate, но не копировать prototype structure в production runtime.
- `Web 1.0-radio-scene.zip` — duplicate wrapper вокруг тех же mock assets, а не отдельный источник требований.

## 2. Продуктовая цель

Web10.Radio должен работать как круглосуточная Telegram channel radio station с визуальной идентичностью Web 1.0 / Aero: полноэкранная 3D-сцена, ретро-окна, live overlay widgets, музыка из управляемой библиотеки и связь с аудиторией через Telegram bot. v0 покрывает track requests, paid screen messages, current-song lookup, donation/goal state, social links, playlists, metadata, storage configuration, moderation и stream health.

Метод оплаты v0 — Telegram Stars. Суммы в API и базе хранятся как integer Telegram Stars. USDT и card terminal фиксируются только как заметки для дорожной карты в этом SPEC; они не входят в v0 implementation checklists в `PLAN-FRONTEND.md` и `PLAN-BACKEND.md`.

## 3. Milestones для параллельной разработки

### Milestone FRONTEND — Claude

- [ ] Create Bun workspace under `src/frontend/` with workspaces `web-stage`, `admin`, and `shared`.
- [ ] Recreate the mock stage in `src/frontend/web-stage` using React + Three.js + strict TypeScript.
- [ ] Build `src/frontend/admin` as a React admin cabinet.
- [ ] Consume only `/api/v0/player/*` and `/api/v0/admin/*` contracts defined in this SPEC.
- [ ] Keep all domain contracts in `src/frontend/shared`; no JavaScript files, no `any`, no `unknown`, no untyped API payloads in authored source.
- [ ] Use Feature-Sliced Design layers `app`, `pages`, `widgets`, `features`, `entities`, `shared`; do not use the deprecated `processes` layer.

### Milestone BACKEND — ChatGPT/OMP

- [ ] Create F# solution `src/backend/Web10.Radio.sln`.
- [ ] Create projects `Web10.Radio.API`, `Web10.Radio.Telegram`, and `Web10.Radio.Database`.
- [ ] Implement ASP.NET API mounts `/api/v0/player/*`, `/api/v0/telegram/*`, `/api/v0/admin/*` as a modular monolith.
- [ ] Implement Funogram bot flows for Stars payments, `/request`, `/say`, `/song`, `/terms`, and `/paysupport`.
- [ ] Implement PostgreSQL persistence with ADO.NET only, SQL migrations, soft delete via `IsDeleted`, and pessimistic queue concurrency using `SELECT ... FOR UPDATE SKIP LOCKED`.
- [ ] Create `src/stream-node/` infrastructure for Xvfb + Chromium + LiquidSoap + FFmpeg pipeline that sends RTMP to Telegram.
- [ ] Package all runtime apps in Docker containers.

Frontend can start from the mock + SPEC DTOs immediately. Backend can start from SPEC contracts immediately. Integration begins when `/api/v0/player/state`, `/api/v0/player/events`, and admin auth assumptions are documented and kept stable.

### Phase S0 — Готовность контрактного пакета

- [ ] Keep `docs/SPEC.md` as the canonical source for product, architecture, contracts, and milestones.
- [ ] Keep `docs/PLAN-FRONTEND.md` consuming section names from this SPEC instead of duplicating private decisions.
- [ ] Keep `docs/PLAN-BACKEND.md` implementing the same `/api/v0/*` contracts without route drift.
- [ ] Validate that every payment, database, frontend, and stream-node invariant has one canonical home in this SPEC.

## 4. Архитектура системы

```mermaid
flowchart LR
  TG[Telegram channel + users] --> Bot[Web10.Radio.Telegram / Funogram]
  Admin[admin React app] --> API[Web10.Radio.API]
  Stage[web-stage React + Three.js] --> API
  Bot --> API
  API --> DB[(PostgreSQL)]
  API --> Storage[(S3 or Local Filesystem)]
  API --> StreamNode[src/stream-node]
  StreamNode --> Stage
  StreamNode --> LS[LiquidSoap]
  StreamNode --> FFmpeg[FFmpeg + x11grab]
  StreamNode --> RTMP[Telegram RTMP]
  LS --> RTMP
```

Архитектурные решения v0:

- Backend — modular monolith, not microservices: первая версия выигрывает от one deployable unit, in-process module boundaries и простых транзакций.
- `Web10.Radio.API` — ASP.NET host. Он владеет HTTP routes, background workers, configuration validation, DI composition, OTEL и health checks.
- `Web10.Radio.Telegram` — project/module для Telegram adapter logic на Funogram. В v0 он hosted by API process как module/hosted service или webhook handler; это не отдельный service, пока реализация не докажет необходимость.
- `Web10.Radio.Database` владеет migrations, SQL helpers, ADO.NET repositories, transaction helpers и database invariants.
- `src/stream-node/` — отдельный container/process group, потому что Chromium/Xvfb/LiquidSoap/FFmpeg требуют OS-level dependencies и process supervision.
- `src/frontend/web-stage` и `src/frontend/admin` — frontend workspaces внутри одного Bun monorepo.

## 5. Backend contract: HTTP API v0

Общие правила API:

- JSON content type: `application/json; charset=utf-8`.
- Все frontend-facing routes (`/api/v0/player/*`, `/api/v0/admin/*`) сериализуют JSON в camelCase — и имена полей, и enum-значения. Это фиксированный контракт для frontend (там принят camelCase). Внутренние PascalCase доменные состояния из БД (например `PlaybackQueue.Status`, `SayMessages.Status`, `StreamNodeHeartbeats.Status`) проецируются в camelCase на API-границе; frontend никогда не видит PascalCase и не видит внутренних состояний, которых нет в enum'ах ниже.
- Все timestamps — UTC ISO-8601 strings ending with `Z`.
- `amountStars`, `raisedStars`, `goalStars` — integer Telegram Stars, not cents.
- Public player routes read-only и unauthenticated, если deployment later не поставит их behind CDN/internal network.
- Admin routes require admin authentication. В v0 auth documented as token/session-based without selecting OAuth yet.
- Telegram webhook route validates Telegram secret token before accepting updates.
- REST errors use RFC 7807-style problem details with `traceId`, `code`, and `message` fields. Example shape: `{ "type": "https://web10.radio/problems/stream-unavailable", "title": "Stream unavailable", "status": 503, "traceId": "...", "code": "stream.unavailable", "message": "Stream is offline" }`.

### Player routes

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/v0/player/state` | Full stage state snapshot for `web-stage`. |
| `GET` | `/api/v0/player/events` | SSE stream for state deltas; frontend falls back to polling `/state`. |
| `GET` | `/api/v0/player/stream` | Public audio stream for web-stage playback; returns `503` with problem details when unavailable. |
| `GET` | `/api/v0/player/song` | Current track link payload used by `/song` and optional frontend display. |
| `GET` | `/api/v0/player/health` | Public/deploy health summary for stream state. |

`GET /api/v0/player/state` response shape:

```json
{
  "serverTimeUtc": "2026-07-07T00:00:00Z",
  "stream": {
    "status": "offline|starting|live|degraded",
    "publicAudioUrl": "/api/v0/player/stream",
    "rtmpRelay": "telegram",
    "bitrateKbps": 192,
    "startedAtUtc": "2026-07-07T00:00:00Z",
    "offlineReason": null
  },
  "nowPlaying": {
    "trackId": "uuid-v7",
    "title": "リサフランク420 / 現代のコンピュー",
    "artist": "Macintosh Plus",
    "album": "FLORAL SHOPPE",
    "source": "library|request|fallback",
    "externalUrl": "https://bandcamp.com/...",
    "coverImageUrl": "/api/v0/player/assets/cover/uuid-v7",
    "durationMs": 240000,
    "positionMs": 42000,
    "startedAtUtc": "2026-07-07T00:00:00Z"
  },
  "queue": {
    "currentQueueItemId": "uuid-v7",
    "items": [
      {
        "queueItemId": "uuid-v7",
        "trackId": "uuid-v7",
        "title": "Track title",
        "artist": "Artist",
        "source": "playlist|request|admin|fallback",
        "status": "queued|claimed|playing|played|failed"
      }
    ]
  },
  "donationGoal": {
    "title": "Цель сбора",
    "raisedStars": 3820,
    "goalStars": 5000,
    "topDonator": { "displayName": "CyberDove", "amountStars": 500 },
    "recent": [
      { "id": "uuid-v7", "displayName": "neonghost", "amountStars": 25, "paidAtUtc": "2026-07-07T00:00:00Z" }
    ]
  },
  "superChat": {
    "messages": [
      {
        "id": "uuid-v7",
        "displayName": "vhs_wanderer",
        "text": "this station literally saved my night shift",
        "amountStars": 100,
        "color": "#e0439a",
        "submittedAtUtc": "2026-07-07T00:00:00Z",
        "status": "approved"
      }
    ]
  },
  "socials": [
    {
      "id": "uuid-v7",
      "kind": "telegram|youtube|instagram|discord|external",
      "name": "Telegram",
      "handle": "@netscapedidnothingwrong",
      "url": "https://t.me/netscapedidnothingwrong",
      "glyph": "T",
      "color": "#2aabee",
      "qrImageUrl": "/api/v0/player/assets/social-qr",
      "isFeatured": true
    }
  ],
  "overlay": { "style": "aero|win9x", "layout": "corners|sidebar|bottombar" }
}
```

SSE route contract:

- Route: `GET /api/v0/player/events`.
- Event names: `player.state`, `player.queue`, `player.say`, `player.donation`, `player.health`.
- Data payload is the same object fragments as `/api/v0/player/state`.
- Client fallback: poll `/api/v0/player/state` every 5 seconds if SSE disconnects twice in 30 seconds.

### Telegram routes

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/v0/telegram/webhook` | Accept Telegram Bot API update webhook. |
| `GET` | `/api/v0/telegram/health` | Bot adapter health and last update id. |

### Admin routes

| Method | Route | Purpose |
| --- | --- | --- |
| `GET/PUT` | `/api/v0/admin/social-links` | Manage social links and QR/source metadata. |
| `GET/PUT` | `/api/v0/admin/donation-goal` | Manage donation goal title and target Stars. |
| `GET/POST` | `/api/v0/admin/playlists` | List/create playlists. |
| `GET/POST/PUT` | `/api/v0/admin/playlists/{playlistId}/items` | Manage playlist items and ordering. |
| `GET` | `/api/v0/admin/say-messages?status=pending|approved|rejected` | Moderate `/say` messages. |
| `POST` | `/api/v0/admin/say-messages/{messageId}/approve` | Approve a paid message for screen display. |
| `POST` | `/api/v0/admin/say-messages/{messageId}/reject` | Reject a paid message with moderation reason. |
| `GET/PUT` | `/api/v0/admin/storage` | Configure S3 or local filesystem library storage. |
| `POST` | `/api/v0/admin/library/scan` | Enqueue a library scan job. |
| `GET` | `/api/v0/admin/stream-node/status` | View stream-node process/heartbeat state. |
| `POST` | `/api/v0/admin/stream-node/restart` | Request stream-node restart through backend command event. |

## 6. Event model вместо процедурных действий

Backend side effects model as events handled by in-process agents/queues, not as direct procedural chains. В v0 F# `MailboxProcessor` — in-process event handler primitive: он serializes mutable state updates through a message queue, упрощает reasoning about queue/payment/stream state и сохраняет transactional boundary в modular monolith.

Event envelope:

```json
{
  "eventId": "uuid-v7",
  "eventType": "TrackRequested",
  "occurredAtUtc": "2026-07-07T00:00:00Z",
  "producer": "Web10.Radio.Telegram",
  "correlationId": "uuid-v7",
  "causationId": "uuid-v7|null",
  "payload": {}
}
```

| Event type | Purpose |
| --- | --- |
| `TrackRequested` | Пользователь запросил трек через Telegram bot или admin action. |
| `TrackRequestMatched` | Запрос сопоставлен с track record или отправлен на admin review. |
| `SayMessageSubmitted` | `/say` message создана до оплаты или модерации. |
| `SayMessageModerated` | Admin approved/rejected paid screen message. |
| `DonationInvoiceCreated` | Backend создал Stars invoice для donation/request/say flow. |
| `DonationPaid` | Telegram прислал `successful_payment`; paid effect can proceed. |
| `PaymentRefunded` | Refund выполнен через Telegram Bot API. |
| `LibraryScanRequested` | Admin или system enqueue library scan job. |
| `TrackDiscovered` | Library scanner нашел audio file/metadata. |
| `PlaybackQueueItemClaimed` | Worker pessimistically claimed queue item. |
| `PlaybackStarted` | Playback state moved to current item. |
| `PlaybackEnded` | Track завершен или failed, queue advances. |
| `StreamNodeHeartbeatReceived` | Backend получил heartbeat от stream-node. |
| `StreamNodeFailureDetected` | Heartbeat/process state сигнализирует degradation/failure. |
| `AdminGoalChanged` | Donation goal changed by admin route. |
| `SocialLinkChanged` | Social link metadata changed by admin route. |

Duplicate Telegram updates are deduped by `(telegramUpdateId, eventType)` before event emission.

## 7. Telegram bot features

Command contracts:

- `/start` — greeting and command list.
- `/help` — command help and payment/support links.
- `/request <query>` — searches the library. If 2-5 matches are found, reply with inline keyboard suggestions. If exactly one confident match is found, ask for confirmation. If no match is found, create a `TrackRequest` with status `NeedsReview` for admin mapping. Payment is requested before the item enters the playable request queue.
- `/say <text>` — creates a pending paid screen message. Bot sends a Stars invoice. After `successful_payment`, message status becomes `PaidPendingModeration`; only admin approval moves it into `approved` messages served to web-stage.
- `/song` — with no args returns current track title, artist, and best external link. With args, uses the same search/suggestion flow as `/request` and returns Bandcamp/SoundCloud/other URL if known; fallback is plain `artist — title`.
- `/terms` — payment terms link/text required before live Stars payments.
- `/paysupport` — payment support command required for Stars payment disputes.

Telegram Stars payment rules from official Telegram Bot API behavior:

- Digital goods/services in Telegram use currency `XTR`.
- `sendInvoice` uses `provider_token = ""` for Stars.
- Bot must answer `pre_checkout_query` within 10 seconds.
- Backend must not deliver a paid message/request after pre-checkout approval; it only delivers after a `successful_payment` update.
- Store `successful_payment.telegram_payment_charge_id` for refunds.
- Refunds use Telegram Bot API `refundStarPayment`.
- USDT/card terminal are future providers and must not be implemented in v0 phases.

## 8. Database and persistence invariants

Persistence rules:

- PostgreSQL is the v0 database.
- Use ADO.NET only: no ORM in app persistence code, no EF Core, no Dapper, no object mapper.
- Use SQL migration files owned by `Web10.Radio.Database`.
- Migrations are implemented with FluentMigrator classes owned by `Web10.Radio.Database`.
- Migration versions are 12-digit Int64 values in YYYYMMDDmmss format; the first migration version is 202607080001.
- Schema upgrades run in a separate `Web10.Radio.Migrator` application/container before the API container is started or promoted.
- The API process never applies migrations during request-path startup; failed migration exits the migrator container non-zero.
- Use Dodo.Primitives `Uuid` for backend domain identifiers; generate RFC9562 UUIDv7 IDs for new domain objects and store them as PostgreSQL `uuid`.
- Every mutable table has `IsDeleted BOOLEAN NOT NULL DEFAULT false`, `CreatedAtUtc`, and `UpdatedAtUtc`.
- Application code never uses `DELETE` for domain data. Deletion means `UPDATE ... SET IsDeleted = true`.
- Read queries for mutable tables include `WHERE "IsDeleted" = false` unless the query is an admin audit query that explicitly asks for deleted rows.
- Indexes over active records should use partial predicates where appropriate: `WHERE "IsDeleted" = false`.

First-version tables:

| Table | Purpose |
| --- | --- |
| `Tracks` | Canonical track metadata. |
| `TrackLinks` | External URLs such as Bandcamp, SoundCloud, YouTube, artist pages. |
| `TrackFiles` | Physical/local/S3 audio file metadata and cache paths. |
| `StorageBackends` | Local or S3 library source configuration metadata. |
| `Playlists` | Admin-managed playlists. |
| `PlaylistItems` | Ordered playlist membership. |
| `PlaybackQueue` | Playable queue for playlist, request, and admin items. |
| `TrackRequests` | Telegram/user requests and matching state. |
| `SayMessages` | Paid screen messages and moderation state. |
| `Payments` | Stars invoice/payment/refund records. |
| `DonationGoals` | Active and historical donation goal values. |
| `SocialLinks` | Social widgets, QR URLs, glyph/color metadata. |
| `LibraryScanJobs` | Scan job lifecycle and errors. |
| `StreamNodeHeartbeats` | Stream-node status samples and failure reasons. |
| `OutboxEvents` | Durable event records for side effects that must survive restarts. |
| `TelegramUpdateInbox` | Deduplication records for Telegram update ids and event types. |

Queue-claiming SQL pattern:

```sql
SELECT "Id"
FROM "PlaybackQueue"
WHERE "IsDeleted" = false
  AND "Status" = 'Queued'
ORDER BY "Priority" DESC, "RequestedAtUtc" ASC, "CreatedAtUtc" ASC
FOR UPDATE SKIP LOCKED
LIMIT 1;
```

The selected row is updated to `Claimed` in the same transaction before playback starts.

## 9. Configuration, secrets, DI, logging, OTEL

Required configuration keys:

- `WEB10_POSTGRES__CONNECTION_STRING`
- `WEB10_TELEGRAM__BOT_TOKEN`
- `WEB10_TELEGRAM__WEBHOOK_SECRET`
- `WEB10_TELEGRAM__CHANNEL_ID_OR_USERNAME=@netscapedidnothingwrong`
- `WEB10_STREAM__RTMP_URL`
- `WEB10_STREAM__RTMP_KEY`
- `WEB10_STREAM__STAGE_URL`
- `WEB10_STORAGE__TYPE=Local|S3`
- `WEB10_STORAGE__LOCAL_ROOT`
- `WEB10_STORAGE__S3_BUCKET`
- `WEB10_OTEL__EXPORTER_OTLP_ENDPOINT`
- `WEB10_DATA_PROTECTION__KEY_RING_PATH`

Startup rules:

- API fails fast on missing/invalid required config.
- URL/config parsing happens at startup, not first request.
- Telegram token and RTMP key are config/Docker secrets in v0, not database rows.
- If a later admin feature stores secrets in PostgreSQL, the secret payload is protected with ASP.NET Core Data Protection and the key ring is persisted outside the container filesystem.


Правила Container/Docker Compose:

- Docker images не должны быть Alpine/libmusl based. Для non-.NET infrastructure используются Debian/Ubuntu-based images, даже если они больше.
- .NET final/runtime images используют Microsoft .NET chiseled variants. Текущие backend runtime tags используют `10.0-noble-chiseled`; SDK build stages остаются на официальных non-Alpine SDK images, потому что Microsoft не публикует chiseled SDK images.
- `compose.yaml` — текущий backend infrastructure smoke path: PostgreSQL `postgres:17`, one-shot `Web10.Radio.Migrator`, затем `Web10.Radio.API`.
- Compose startup order: PostgreSQL healthcheck → migrator successful completion → API startup. Migrator обязан применить pending FluentMigrator migrations до старта API.
- Текущий compose smoke намеренно не закрывает full v0 Compose target с frontend, stream-node и observability collector; это остается для later Docker verification phase.

DI rules aligned with ASP.NET guidance:

- Register related module services through module registration functions.
- Use constructor injection, not service locator.
- Use scoped repositories/transactions for request work.
- Do not inject scoped services into singletons without an explicit scope factory.

Logging/OTEL rules:

- Use high-performance logging with `LoggerMessageAttribute`/source-generated logging or equivalent F# wrapper over source-generated partial methods where implemented in C# support code; no string interpolation in hot-path log calls.
- Include `traceId`, `correlationId`, `eventId`, `telegramUpdateId`, and `queueItemId` where applicable.
- Emit OTEL traces and metrics for API requests, Telegram updates, queue claims, library scans, stream-node heartbeats, and payment flow.

## 10. Frontend architecture contract

Frontend paths:

- `src/frontend/package.json` — Bun workspace root with workspaces `web-stage`, `admin`, `shared`.
- `src/frontend/tsconfig.base.json` — strict base config.
- `src/frontend/shared/` — shared domain contracts, API clients, tokens.
- `src/frontend/web-stage/` — public React + Three.js player scene.
- `src/frontend/web-stage/mocks/` — keep existing mock bundle as reference assets.
- `src/frontend/admin/` — React admin cabinet.

TypeScript rules:

- `strict: true` is mandatory.
- Authored source uses `.ts`/`.tsx` only; no `.js`.
- No `any`, no `unknown`, no untyped API payloads, no type assertions that erase domain types.
- Every known payload uses named domain types from `src/frontend/shared`.
- FSD import direction is from higher layers to lower layers only; `shared` imports no project domains.

Web-stage visual invariants from the mock:

- Fullscreen canvas background.
- Loading window `web1radio.exe` until scene ready.
- NOW PLAYING widget.
- DONATION GOAL widget.
- SUPER CHAT widget.
- FOLLOW US widget with QR and featured social.
- Donation toast.
- Themes `aero` and `win9x`.
- Layouts `corners`, `sidebar`, `bottombar`.
- WebGL context loss/restoration, resize handling, mouse parallax, requestAnimationFrame lifecycle, and resource cleanup.

## 11. stream-node contract

`src/stream-node/` is its own runtime area with these responsibilities:

- Start Xvfb display for headless Chromium.
- Start Chromium in kiosk mode pointed at the deployed `web-stage` URL and include `--enable-unsafe-swiftshader` because software WebGL is required in the stream container.
- Run LiquidSoap script that builds the audio/video stream graph, reads backend metadata/queue state, and produces a Telegram-compatible RTMP output.
- Use FFmpeg/x11grab to capture the Chromium/X11 stage video.
- Mix video from Chromium with audio selected from backend metadata/cache.
- Push to Telegram RTMP using `WEB10_STREAM__RTMP_URL` and `WEB10_STREAM__RTMP_KEY`.
- Report heartbeat/failure status to backend.

Failure states: `Starting`, `Live`, `Degraded`, `Restarting`, `Failed`, `Offline`. Restart policy uses bounded retries with surfacing to admin after the retry window, not silent infinite restart.

## 12. Testing and acceptance

Integration tests are preferred over unit tests because v0 risk sits at contracts, database concurrency, Telegram payment state, and process boundaries. Required test/check areas:

- API contract tests for `/api/v0/player/state`, `/api/v0/player/events`, admin moderation routes, and Telegram webhook parsing.
- Database integration tests for migrations, soft delete filtering, and `SELECT ... FOR UPDATE SKIP LOCKED` queue claiming.
- Telegram command tests for `/request`, `/say`, `/song`, Stars pre-checkout, successful payment, duplicate update dedupe, `/terms`, `/paysupport`.
- stream-node smoke checks for Xvfb, Chromium, LiquidSoap, FFmpeg availability, and heartbeat reporting.
- frontend checks for strict TypeScript, no JavaScript, no `any`/`unknown`, scene cleanup, and API fallback behavior.
- Docker smoke path: PostgreSQL + API + frontend + stream-node start and health endpoints become green.
