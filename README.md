# web10-radio

Web10.Radio — планируемая круглосуточная Telegram-channel radio station для `@netscapedidnothingwrong` с Web 1.0 / Aero публичной сценой, Telegram bot interactions, PostgreSQL persistence, Telegram Stars payments и Dockerized runtime services.

## Текущее состояние backend

Backend реализован в `src/backend/` как два независимых deployable service:

- `Web10.Radio.API` — ASP.NET/F# host со startup config validation, player/admin/library/playback/stream-node routes, API workers, health endpoints и shared application kernel reference.
- `Web10.Radio.Application` — общий F# event envelope, outbox audience mapping, relay contracts и health primitives.
- `Web10.Radio.Database` — PostgreSQL options, FluentMigrator migrations through `202607110004`, `pg_trgm` search indexes, ADO.NET transaction/session helpers и repositories.
- `Web10.Radio.Migrator` — отдельное one-shot migration app/container.
- `Web10.Radio.Telegram` — standalone Funogram executable с webhook/long-polling ingress, Stars workflows и Telegram outbox relay.
- `Web10.Radio.Tests` — NUnit + Testcontainers PostgreSQL integration tests для API, Telegram commands/callbacks, Stars payments, migrations, outbox, playback policies и moderation.

Канонические contracts остаются в `docs/SPEC.md`; состояние backend checklist — в `docs/PLAN-BACKEND.md`.

Текущий backend/API state:

- Public player routes `/api/v0/player/state`, `/events`, `/stream`, `/song`, `/health` реализованы.
- Telegram service `/api/v0/telegram/webhook` and `/health` use one typed ingress; webhook and standalone long polling are selected by exact `WEB10_TELEGRAM__UPDATE_MODE=Webhook|LongPolling`, with strict secret/body validation, durable command/callback/payment relay, and synchronous pre-checkout acknowledgement.
- Localized RU/EN `/start`, `/help`, `/request`, `/say`, `/song`, `/terms`, `/paysupport` реализованы. `/request` использует PostgreSQL `pg_trgm`; request invoice стоит configured 100 Stars, `/say` invoice — configured 50 Stars в compose deployment values.
- Stars invoices используют `XTR`, empty provider token и one price item. Request queue and say moderation state change only after `successful_payment`; charge id сохраняется for future refund operations.
- Internal stream-node callbacks `/api/v0/stream-node/playback/{queueItemId}/lease|completion` защищены policy `Web10StreamNode`; lease renewal и authoritative completion fenced по owner/attempt.
- Весь `/api/v0/admin/*` group защищен session+CSRF auth. Реализованы metadata/artwork, queue reorder/skip/restart/play-now, playlist policies/schedules, storage, scan, social/donation writes и paid `/say` moderation.

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

Admin routes используют session auth (не bearer): `WEB10_ADMIN__USERNAME` и `WEB10_ADMIN__PASSWORD` задают bootstrap admin, который создается/обновляется при старте. `POST /api/v0/admin/auth/login` устанавливает `HttpOnly` cookie `web10_admin_session`, а каждый mutating admin request требует `X-CSRF-Token` из ответа login. Отдельный `WEB10_STREAM__CALLBACK_TOKEN` защищает stream-node lease/completion callbacks. `WEB10_TELEGRAM__UPDATE_MODE` принимает exact `Webhook|LongPolling` и defaults to `Webhook`; в LongPolling standalone `Web10.Radio.Telegram` сначала вызывает `deleteWebhook(dropPendingUpdates=false)`, затем получает `getUpdates` с monotonic offset. Оба транспорта проходят через один durable typed ingress.

Telegram request/say prices обязательны и не имеют runtime defaults: `WEB10_TELEGRAM__REQUEST_PRICE_STARS` и `WEB10_TELEGRAM__SAY_PRICE_STARS` должны быть positive invariant `Int32`; `compose.yaml` задает smoke values `100` и `50`.

Для Local storage задаются `WEB10_STORAGE__TYPE=Local` и `WEB10_STORAGE__LOCAL_ROOT`; для S3 задаются `WEB10_STORAGE__TYPE=S3`, bucket и region. Полный startup-validation contract и exact key literals находятся в `docs/SPEC.md` §9.
- `WEB10_STORAGE__MAX_UPLOAD_BYTES` defaults to 512 MiB and is validated as a positive `Int64`; it bounds the streaming upload endpoint without buffering request bodies.
## Backend Compose smoke

`compose.yaml` разворачивает PostgreSQL + migrator + API + `frontend` (nginx со stage на `/` и admin на `/admin/`, проксирует `/api`) + реальный F# `stream-node` + внутренний `rtmp-sink`. Это Development vertical-slice env (`WEB10_DEV__FIXTURES_ENABLED=true`): stream-node владеет Xvfb, kiosk Chromium, Liquidsoap, loopback callbacks и Unix command socket; Liquidsoap кодирует H.264/AAC FLV и публикует в Compose RTMP sink. Telegram service запускается отдельно и получает `/api/v0/telegram/*` через nginx. OTLP collector optional.

Для локального RTMP smoke явно переопределите `WEB10_STREAM__RTMP_URL=rtmp://rtmp-sink:1935/s/` и `WEB10_STREAM__RTMP_KEY=compose-smoke-rtmp-key`; production `.env` может содержать Telegram RTMPS target и секреты. Admin password обязан быть 12–256 символов.

Из repository root выполните bounded smoke:

```sh
set -eu
cleanup() { docker compose down --volumes --remove-orphans; }
trap cleanup EXIT INT TERM

WEB10_STREAM__RTMP_URL=rtmp://rtmp-sink:1935/s/ \
WEB10_STREAM__RTMP_KEY=compose-smoke-rtmp-key \
docker compose up --build --wait --wait-timeout 180

curl -fsS http://localhost:8080/health/live
curl -sS -w '\nHTTP %{http_code}\n' http://localhost:8080/health/ready
curl -fsS -c /tmp/web10-admin.cookie -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"admin-password"}' \
  http://localhost:8080/api/v0/admin/auth/login
curl -fsS -b /tmp/web10-admin.cookie http://localhost:8080/api/v0/admin/social-links

docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U web10 -d web10 -tAc \
  'SELECT "Version" FROM "VersionInfo" ORDER BY "Version";'
docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U web10 -d web10 -tAc \
  "SELECT 1 / ((count(*) = 1)::int) FROM pg_extension WHERE extname = 'pg_trgm';"
docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U web10 -d web10 -tAc \
  "SELECT 1 / ((count(*) = 5)::int) FROM pg_indexes WHERE schemaname='public' AND indexname IN ('IX_Tracks_Active_Title_Trgm','IX_Tracks_Active_ArtistTitle_Trgm','UX_Payments_Active_InvoicePayload','UX_Payments_Active_PurposeEntity','UX_PlaybackQueue_Active_TrackRequest');"

dotnet run --project src/stream-node/Web10.Radio.StreamNode.Tools/Web10.Radio.StreamNode.Tools.fsproj -- smoke-backend --mode play-now --base-url http://localhost:8080 --rtmp-stat-url http://localhost:8091/stat --username admin --password admin-password --timeout-seconds 30
dotnet run --project src/stream-node/Web10.Radio.StreamNode.Tools/Web10.Radio.StreamNode.Tools.fsproj -- smoke-backend --mode reorder --base-url http://localhost:8080 --rtmp-stat-url http://localhost:8091/stat --username admin --password admin-password --timeout-seconds 30
dotnet run --project src/stream-node/Web10.Radio.StreamNode.Tools/Web10.Radio.StreamNode.Tools.fsproj -- smoke-backend --mode skip --base-url http://localhost:8080 --rtmp-stat-url http://localhost:8091/stat --username admin --password admin-password --timeout-seconds 30
```

`docker compose up --wait` ожидает health state всех сервисов. API liveness healthcheck использует managed self-probe `dotnet Web10.Radio.API.dll --health-check ...` в .NET chiseled image и не требует Alpine/libmusl, shell, `curl` или `wget` внутри API container. Smoke-config intentionally uses an invalid Telegram token, поэтому `/health/ready` может быть `503`, пока `/health/live` и Compose service health остаются зелёными.

Ожидаемый migration tail: `202607110001`, `202607110002`, `202607110003`, `202607110004`. Для ручной cleanup вне smoke block: `docker compose down --volumes --remove-orphans`.
## SeaweedFS S3 File Manager smoke

Для изолированного S3 smoke используйте только `compose.s3-smoke.yaml`, не изменяя обычный Compose project и не подменяя SeaweedFS на MinIO:

```sh
docker compose -p web10-radio-s3-smoke -f compose.yaml -f compose.s3-smoke.yaml config --quiet
WEB10_STREAM__RTMP_URL=rtmp://rtmp-sink:1935/s/ \
WEB10_STREAM__RTMP_KEY=compose-smoke-rtmp-key \
docker compose -p web10-radio-s3-smoke -f compose.yaml -f compose.s3-smoke.yaml up --build --wait --wait-timeout 180
```

The override pins `chrislusf/seaweedfs:4.29`, creates `web10-radio` with `seaweedfs-init`, configures path-style S3 at `http://seaweedfs:8333`, and uses `web10-smoke` / `web10-smoke-secret`. The API depends on successful bucket initialization. Use the configured visible Chrome DevTools MCP workflow at `http://localhost:8090/admin/` to upload files and `webkitdirectory` trees, scan once, browse/preview/download, verify `206` ranges, and confirm recursive delete impact before deletion. Finish with:

```sh
docker compose -p web10-radio-s3-smoke -f compose.yaml -f compose.s3-smoke.yaml down -v
```

Observed 2026-07-12/13: SeaweedFS bucket init, authenticated file and folder uploads, scan `completed`, text preview, `206 bytes 0-5/11`, attachment download, recursive folder preview/delete, and physical key cleanup passed in visible Chrome; no MinIO image or request appeared.


## Backend local checks

```sh
dotnet build src/backend/Web10.Radio.sln
dotnet test src/backend/Web10.Radio.sln --no-restore
```

Observed 2026-07-13 after the storage File Manager, SeaweedFS smoke, metadata/artwork, playback-policy, Telegram split, and F# stream-node work:

- `dotnet test src/backend/Web10.Radio.sln --no-restore` passed `102/102` (0 failed).
- `bun run test` passed `144/144` (`shared` 80, `web-stage` 60, `admin` 4).
- `docker compose config --quiet` passed; `docker compose up --build --wait --wait-timeout 180` built and reached healthy PostgreSQL, Telegram, API, frontend, RTMP sink, and F# stream-node containers. The latest applied migration was `202607110004`.
- `/health/live` returned `200 Healthy`; Telegram health returned configured adapter state; webhook smoke returned `204`.
- F# stream-node tool controls `play-now`, `reorder`, and `skip` returned `status=passed` against the local Compose stack.
