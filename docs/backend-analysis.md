# Backend Readiness Analysis

**Дата исходного аудита:** 2026-07-09  
**Дата remediation verification:** 2026-07-10  
**Изолированный final Compose project:** `web10-radio-final-0710`

# Вердикт: **GO**

Все зафиксированные ниже **1 Blocker**, **6 High** и **15 Medium** findings устранены. Независимый повторный source-level audit не нашёл текущих blocker/high/medium production defects; обязательные boundary/concurrency/schema regressions и чистый Compose startup подтверждены исполняемыми проверками.

Текущее evidence:

- Release build: `0` warnings, `0` errors.
- Full backend suite: `63/63` passed, `0` failed, `0` skipped.
- Focused suites: API `19/19`, schema/policy `11/11`, workflow `9/9`.
- Isolated `docker compose up --build --wait --wait-timeout 120 api`: API стал `healthy` без ad hoc delay, migrations `202607080001`, `202607100001`, `202607100002` применились, immediate liveness/admin probes прошли.
- Operational readiness вернул ожидаемый `503 Unhealthy` только для intentionally invalid smoke Telegram token; PostgreSQL/storage/API были `Healthy`, отсутствующий stream-node heartbeat — `Degraded`.
- После smoke не осталось project-owned containers, volumes или network.

Ниже сохранены исходные findings, ledger и mandatory gates как исторический baseline. Их формулировки описывают состояние на 2026-07-09, а не текущую реализацию; актуальное закрытие перечислено в `Remediation verification` в конце документа.

---

# Original findings — historical baseline

## Blocker

### B-1. Успешное воспроизведение навсегда блокирует очередь после первого cached track

- **Evidence:** `src/backend/Web10.Radio.API/BackgroundWorkers.fs:840-903`; `src/backend/Web10.Radio.Database/Repositories/PlaybackQueueRepository.fs:59-80,93-109`
- **Нарушенный контракт:** checked B2 playback-program claim; `docs/SPEC.md` §6 `PlaybackEnded`; §8 queue progression.
- **Сценарий:** worker атомарно переводит cached item в `Playing` и публикует `PlaybackStarted`, но production path для `Playing → Played` и successful `PlaybackEnded` отсутствует. Следующий claim содержит `NOT EXISTS` guard против любого `Claimed`/`Playing` item и возвращает `None`. Crash после claim/start даёт тот же permanent stall: lease/reclaim отсутствуют.
- **Текущее покрытие:** `BackgroundWorkerTests.fs:591-758` прямо подтверждает, что первый item остаётся `Playing`, а второй остаётся `Queued` и отказывается обрабатываться. Теста `Playing → Played/Failed`, crash/restart или stale-active reclaim нет.
- **Remediation gate:** реализовать authoritative completion/failure callback, `Playing → Played|Failed`, успешный `PlaybackEnded`, lease/startup recovery для `Claimed`/`Playing`; доказать тестами crash до/после commit и дальнейшее продвижение очереди.

---

## High

### H-1. Admin API полностью неаутентифицирован

- **Evidence:** `src/backend/Web10.Radio.API/ApiEndpoints.fs:130-132,393-419`; `src/backend/Web10.Radio.API/Program.fs:46-49`
- **Нарушенный контракт:** `docs/SPEC.md:84` — все admin routes требуют authentication; checked Global claim `PLAN-BACKEND.md:8`.
- **Фактическое воспроизведение:** settled Compose smoke:
  - `GET /api/v0/admin/social-links`
  - status: **`200`**
  - body: **`[]`**
  - ожидаемый контрактом status: `401` или `403`.
- **Сценарий:** любой клиент достигает admin GET и всех mutating route patterns без challenge. Сейчас mutations возвращают `501`, но добавление реального body немедленно сделает mutation публичной.
- **Текущее покрытие:** `ApiContractTests.fs:332-387` закрепляет неправильное поведение — вызывает admin GET без auth и ожидает `200`, placeholder без auth и ожидает `501`.
- **Remediation gate:** выбрать и закрепить token/session auth, зарегистрировать authentication/authorization middleware, повесить policy на весь `/api/v0/admin/*` group, добавить unauthenticated/unauthorized tests для каждого admin method.

### H-2. Webhook подтверждает Telegram updates кодом 204, но не обрабатывает их

- **Evidence:** `src/backend/Web10.Radio.API/ApiEndpoints.fs:295-338`; `src/backend/Web10.Radio.API/BackgroundWorkers.fs:528-570`
- **Нарушенный контракт:** checked B3 webhook claim; `docs/SPEC.md` §5 Telegram route purpose.
- **Сценарий:** handler проверяет secret, записывает raw inbox row с `EventType = "telegram.webhook"` и возвращает `204`. Он не:
  - парсит Funogram `Update`;
  - вызывает `ITelegramUpdateEventIngestor.TryIngestAsync`;
  - создаёт соответствующий domain event/outbox row;
  - обновляет adapter state.

  После `204` Telegram не повторит update. Будущие commands/payment updates будут признаны принятыми и функционально потеряны.
- **Текущее покрытие:** `ApiContractTests.fs:315-330` проверяет только одну raw inbox row при повторной отправке. Реальный ingestor тестируется отдельно, без HTTP boundary.
- **Remediation gate:** провести webhook через typed Funogram update parsing и domain-event ingestion до возврата `204`; атомарно dedupe каждый emitted `(telegramUpdateId,eventType)`; добавить end-to-end accepted/duplicate/concurrent/malformed tests.

### H-3. Outbox не имеет ownership/fencing и допускает duplicate либо неверно зафиксированные durable effects

- **Evidence:**  
  `src/backend/Web10.Radio.Database/Repositories/OutboxEventRepository.fs:43-78,132-207`;  
  `src/backend/Web10.Radio.API/BackgroundWorkers.fs:418-443,471-512`
- **Нарушенный контракт:** checked B2 durable outbox claim.
- **Сценарии:**
  1. `PublishDurableAsync` commit-ит `Pending`, затем сам вызывает dispatcher. Между commit и direct dispatch relay может claim-нуть тот же row. Оба выполняют handler.
  2. Через 30 секунд второй relay reclaim-ит `Processing`, пока первый ещё работает.
  3. `markProcessed`/`markFailed` обновляют row только по `Id`, без attempt/owner token и без `Status='Processing'` fence. Stale attempt может перезаписать состояние нового attempt.
- **Текущее покрытие:** тестируются single-row concurrent claim, lease expiry, обычный relay success и dispatcher failure. Direct-vs-relay overlap и stale-worker completion/failure отсутствуют.
- **Remediation gate:** возвращать claim ownership token/attempt; обновлять terminal status только по `(Id, owner, attempt, Processing)`; исключить direct-vs-relay race — claim before direct dispatch либо оставить единственный relay dispatch path; добавить deterministic concurrent tests.

### H-4. Library scan jobs навсегда остаются `Running` после crash, cancellation или per-file dispatch failure

- **Evidence:**  
  `src/backend/Web10.Radio.Database/Repositories/LibraryScanRepository.fs:37-69,128-218`;  
  `src/backend/Web10.Radio.API/BackgroundWorkers.fs:665-776`
- **Нарушенный контракт:** checked B2 restart-surviving scanner claim.
- **Сценарий:** claim переводит `Queued → Running`, но:
  - lease/owner/attempt отсутствуют;
  - `Running` никогда не reclaim-ится;
  - process death и cancellation оставляют job навсегда;
  - `FileInfo`/filesystem exception вне обработанных веток оставляет job `Running`;
  - `TrackDiscovered` уже commit-нут в outbox, но post-commit dispatch error прекращает scan и не переводит job в retryable/Failed.
- **Текущее покрытие:** только local happy path `Queued → Running → Completed`; stale `Running`, cancellation, crash boundary и dispatch failure не проверяются.
- **Remediation gate:** добавить scan claim lease/attempt/owner, stale `Running` reclaim, гарантированный post-claim terminal/retry transition и restart tests на каждой commit boundary.

### H-5. Concurrent library scans могут создать duplicate active `Tracks` и `TrackFiles`

- **Evidence:**  
  `src/backend/Web10.Radio.Database/Repositories/LibraryScanRepository.fs:80-103,266-299`;  
  `src/backend/Web10.Radio.Database/Migrations/InitialSchema.fs:232-233`
- **Нарушенный контракт:** checked B2 scanner correctness и persistence active-row invariants.
- **Сценарий:** два worker/API replica одновременно:
  1. выполняют `SELECT EXISTS` по `(StorageBackendId, StoragePath)`;
  2. оба получают `false`;
  3. оба вставляют отдельные `Tracks`, `TrackFiles` и `TrackDiscovered`.

  Matching unique index отсутствует. Обычный unique index также недостаточен для `StorageBackendId IS NULL`, поскольку PostgreSQL считает `NULL` distinct.
- **Текущее покрытие:** `LibraryScanRepositoryTests.fs:100-155` проверяет только последовательный duplicate; concurrent test отсутствует.
- **Remediation gate:** добавить database-enforced active uniqueness с явной NULL semantics — например, отдельные partial unique indexes для `StorageBackendId IS NULL` и `IS NOT NULL`; использовать conflict-safe insert/handling; добавить concurrent integration test.

### H-6. Checked scanner не поддерживает S3

- **Evidence:** `src/backend/Web10.Radio.API/BackgroundWorkers.fs:586-594,739-763`
- **Нарушенный контракт:** checked `PLAN-BACKEND.md:50`; `docs/SPEC.md` поддерживает `Local|S3`.
- **Сценарий:** валидный `WEB10_STORAGE__TYPE=S3` или S3 `StorageBackends` row всегда переводит scan job в failure: `S3 library scan requires credentials/config not defined in SPEC v0`. Object enumeration и `TrackDiscovered` flow отсутствуют.
- **Текущее покрытие:** только Local filesystem.
- **Remediation gate:** либо реализовать S3 scan path и явно закрепить credential/config contract, либо убрать S3 из v0 canonical contract и снять checked status до реализации.

---

## Medium

### M-1. Checked DI registration order не реализован в заявленном виде

- **Evidence:** `src/backend/Web10.Radio.API/Program.fs:37-48`
- **Нарушенный контракт:** checked `PLAN-BACKEND.md:28`.
- **Сценарий:** checklist заявляет DI stages `Database → Application → Telegram → Background → API endpoints → Observability`. В коде:
  - `Health` вставлен как незаявленный registration stage;
  - API endpoint registration function отсутствует;
  - endpoint mapping выполняется только после `builder.Build()` и после Observability.
- **Текущее покрытие:** тесты только resolve-ят selected worker services; sequence не проверяется.
- **Remediation gate:** либо исправить checklist под реальный ASP.NET lifecycle, либо ввести явные service-registration/mapping modules и тестировать заявленный relative order.

### M-2. Compose объявляет API запущенным раньше, чем тот готов отвечать на probes

- **Evidence:** `compose.yaml:31-60`; `README.md:41-62`
- **Нарушенный контракт:** checked B6 `PLAN-BACKEND.md:101-103`.
- **Фактическое воспроизведение:** сразу после успешного `docker compose up -d api` и `ps`:
  - `/health/live`: curl exit **56**, `Recv failure: Connection reset by peer`;
  - `/health/ready`: curl exit **56**.

  После ожидания `1.5s` те же точные curls прошли.
- **Текущее покрытие:** container/Compose test отсутствует; README “Observed” — исторический текст, не executable evidence.
- **Remediation gate:** добавить API healthcheck и documented `docker compose up --wait` либо явный bounded retry/wait contract; повторить smoke без ad hoc delay.

### M-3. Concurrent relays не сохраняют event order

- **Evidence:**  
  `src/backend/Web10.Radio.Database/Repositories/OutboxEventRepository.fs:44-62`;  
  `src/backend/Web10.Radio.API/BackgroundWorkers.fs:489-508`
- **Нарушенный контракт:** causal event model `SPEC.md` §6; checked B2 dispatch.
- **Сценарий:** SQL сортирует по `OccurredAtUtc`, но несколько relay instances claim-ят разные batches и dispatch-ят независимо. Поздний causal event может завершиться раньше раннего.
- **Текущее покрытие:** single relay batch и concurrent claim одной row; multi-event/multi-relay ordering отсутствует.
- **Remediation gate:** выбрать ordering scope — global/correlation/aggregate — и сериализовать либо partition-ить claims; альтернатива — документировать order-independent handlers и доказать их idempotency.

### M-4. Exported `claimNext` обходится без one-active-item invariant

- **Evidence:** `src/backend/Web10.Radio.Database/Repositories/PlaybackQueueRepository.fs:16-52`
- **Нарушенный контракт:** checked queue claiming плюс установленный advisory-lock/one-active invariant.
- **Сценарий:** helper использует правильный `SKIP LOCKED` ordering, но без advisory transaction lock и без проверки активного `Claimed|Playing`. При production reuse несколько callers создадут несколько active items.
- **Текущее покрытие:** `PlaybackQueueRepositoryTests.fs:11-69` намеренно ожидает два concurrent claims; production сейчас использует detailed variant, поэтому дефект latent.
- **Remediation gate:** удалить неиспользуемый helper/test либо делегировать его fenced one-active implementation.

### M-5. Mailbox cancellation не отменяет queued work и не завершает agent lifetime

- **Evidence:** `src/backend/Web10.Radio.API/BackgroundWorkers.fs:331-401`
- **Нарушенный контракт:** checked B2 MailboxProcessor handling.
- **Сценарий:** agents запускаются без host cancellation token и бесконечно ждут `inbox.Receive()`. `PostAndAsyncReply` публикует message до того, как `Async.StartAsTask` замечает cancellation. Caller может считать attempt canceled/failed, а queued handler позже всё равно выполнит heartbeat/payment write.
- **Текущее покрытие:** shutdown, cancellation-after-post и late side effect не проверяются.
- **Remediation gate:** связать agents с host lifetime; определить semantics already-posted dispatch; добавить cancellation-race tests совместно с outbox fencing.

### M-6. Public player health не считает persisted heartbeat устаревшим

- **Evidence:** `src/backend/Web10.Radio.API/ApiReadModels.fs:100-104,228-258,484-500`
- **Нарушенный контракт:** checked B3 player health/stream claims.
- **Сценарий:** последний row со статусом `Live` остаётся `live` навсегда независимо от `HeartbeatAtUtc`. `/player/health` сообщает live, а `/player/stream` может продолжить отдавать stale cache после смерти stream-node. При этом `/health/ready` отдельно применяет 30-second freshness.
- **Текущее покрытие:** старый heartbeat и `/player/health` не тестируются.
- **Remediation gate:** использовать единый heartbeat freshness policy для state, health, stream gating и readiness; протестировать boundary ages.

### M-7. Queue и library MailboxProcessor agents являются validators, а не state handlers

- **Evidence:** `src/backend/Web10.Radio.API/BackgroundWorkers.fs:285-329,366-401`
- **Нарушенный контракт:** checked `PLAN-BACKEND.md:47`.
- **Сценарий:** `PlaybackQueueAgent` и `LibraryScanAgent` только проверяют payload и возвращают `Ok`; state transitions выполняются напрямую hosted loops. Публикация domain event через заявленный agent не запускает соответствующий workflow.
- **Текущее покрытие:** validation/DI registration, но не event-driven state change.
- **Remediation gate:** либо сузить architecture/checklist до validator agents, либо переместить заявленные state transitions за MailboxProcessor contract.

### M-8. Readiness считает сконфигурированные зависимости рабочими без фактической проверки

- **Evidence:** `src/backend/Web10.Radio.API/Health.fs:36-62`
- **Нарушенный контракт:** checked B0 health claim; ASP.NET readiness semantics.
- **Сценарии:**
  - revoked Telegram token остаётся `Healthy`;
  - S3 всегда `Degraded` без connectivity probe;
  - существующий, но unwritable Local root считается `Healthy`.
- **Текущее покрытие:** registration/shape и configured state; Telegram/S3 connectivity и local writeability отсутствуют.
- **Remediation gate:** проверять реальные dependency semantics либо явно сузить и переименовать claim/check.

### M-9. SSE реализует только два из пяти обязательных event types

- **Evidence:** `src/backend/Web10.Radio.API/ApiEndpoints.fs:46-98`
- **Нарушенный контракт:** checked B3 SSE claim; `SPEC.md:175-180`.
- **Сценарий:** достижимы только:
  - `player.state`;
  - `player.health`.

  Недостижимы:
  - `player.queue`;
  - `player.say`;
  - `player.donation`.

  Queue/donation/chat UI не обновляется через SSE.
- **Текущее покрытие:** `ApiContractTests.fs:248-273` читает только первый `player.state`.
- **Remediation gate:** добавить все пять exact literals с соответствующими state fragments и deterministic SSE tests.

### M-10. Soft-delete и migration tests доказывают только узкую часть checked persistence claims

- **Evidence:**  
  `src/backend/Web10.Radio.Tests/DatabaseMigrationTests.fs:38-107`;  
  `src/backend/Web10.Radio.Tests/MigrationMetadataTests.fs:8-24`
- **Нарушенный контракт:** checked B1 lines 35–37, 42.
- **Сценарий:** тесты продолжат проходить, если будущая migration:
  - потеряет audit column;
  - изменит FK/check constraint;
  - потеряет partial predicate;
  - сломает Down;
  - пропустит active parent join.
- **Текущее покрытие:** table test проверяет наличие ожидаемых имён, но допускает лишние; soft-delete покрывает только `TrackRepository.listActive`.
- **Remediation gate:** schema introspection для всех mutable tables/index predicates/constraints, migration Down test и representative parent-join tests.

### M-11. Startup validation принимает operationally invalid configuration

- **Evidence:** `src/backend/Web10.Radio.API/Configuration.fs:60-112`
- **Нарушенный контракт:** `SPEC.md` §9; checked Global 14, B0 27, B6 106.
- **Сценарии:** startup проходит для:
  - malformed PostgreSQL connection string;
  - absolute URI с неверной scheme;
  - syntactically invalid Telegram token/channel;
  - unusable S3 bucket;
  - unwritable key-ring/local path;
  - неверной Local/S3 field combination.
- **Текущее покрытие:** missing keys, storage enum и один relative stage URI.
- **Remediation gate:** parse Npgsql connection string; ограничить URI schemes; валидировать token/key/channel/bucket/path и selected-storage semantics до build host.

### M-12. Telegram health state никогда не записывает update/error

- **Evidence:**  
  `src/backend/Web10.Radio.Telegram/TelegramAdapter.fs:5-20`;  
  `src/backend/Web10.Radio.API/ApiEndpoints.fs:340-357`
- **Нарушенный контракт:** checked B3 Telegram health claim; SPEC route purpose.
- **Сценарий:** `/api/v0/telegram/health` всегда возвращает:
  - `lastUpdateId: null`;
  - `lastError: null`,

  даже после accepted или failed webhook.
- **Текущее покрытие:** state transition и endpoint assertions отсутствуют.
- **Remediation gate:** thread-safe mutable state, обновляемое на accepted/failed processing; тесты monotonic update id и error lifecycle.

### M-13. Набор из 46 тестов не защищает несколько checked runtime contracts

- **Evidence:** `src/backend/Web10.Radio.Tests/*.fs`
- **Нарушенный контракт:** `SPEC.md` §12 и checked B1/B3/B6 verification claims.
- **Inventory:**
  - всего `46`;
  - `34` Testcontainers PostgreSQL integration tests;
  - `12` pure tests;
  - `0` `Ignore`;
  - `0` `Explicit`;
  - `0` conditional tests.
- **Не покрыто:** process-level semantic config failure, admin auth/full matrix, четыре later SSE kinds, live/range stream, heartbeat freshness, webhook malformed/concurrent/multi-header/oversized/domain dispatch, UUIDv7 persistence, stale outbox fencing, scan/playback recovery, concurrent library dedupe, forbidden SQL/image policy и Compose timing.
- **Remediation gate:** добавить targeted integration/process/container tests на каждый mandatory remediation gate; не подменять их registration/happy-path assertions.

### M-14. Unhandled route exceptions теряют exception evidence

- **Evidence:** `src/backend/Web10.Radio.API/ApiEndpoints.fs:102-127`
- **Нарушенный контракт:** structured diagnostics `SPEC.md` §9; checked field set line 64 формально присутствует.
- **Сценарий:** catch-all пишет generic problem и completion log, но исключение, stack, type и message не логируются. При response-started failure logged status также может отличаться от wire status.
- **Текущее покрытие:** exception injection/log capture отсутствует.
- **Remediation gate:** логировать exception object с route/trace/correlation fields; проверить failures до и после response start.

### M-15. Webhook body не ограничен, а cardinality secret header не проверяется строго

- **Evidence:** `src/backend/Web10.Radio.API/ApiEndpoints.fs:249-312`
- **Нарушенный контракт:** checked B3 webhook validation и approved boundary review.
- **Сценарии:**
  - valid-secret request вызывает неограниченный `ReadToEndAsync` allocation;
  - несколько header values flatten-ятся через `StringValues.ToString()`;
  - при comma-containing configured secret split values могут сравняться вместо malformed rejection.
- **Текущее покрытие:** один правильный/неправильный header и маленький valid body.
- **Remediation gate:** требовать ровно одно header value, ограничить request bytes до allocation, возвращать `413`, добавить missing/multi-value/oversized/malformed matrix.

---

# Original completion ledger — historical baseline

Статусы взаимоисключающие:

- `confirmed` — implementation и evidence удовлетворяют claim;
- `partial` — реализация существенно уже checked claim;
- `contradicted` — observable behavior нарушает checked/canonical claim;
- `process-only` — только historical action;
- `environment-blocked` — executable check невозможно выполнить из-за внешней среды.

## Global

| PLAN line | Status | Claim / evidence |
|---:|---|---|
| 7 | `process-only` | Read SPEC §§5–12 — historical action, не влияет на verdict. |
| 8 | `contradicted` | Все route patterns mapped, но большинство admin methods — `501`, auth отсутствует, webhook не dispatch-ит updates. |
| 9 | `confirmed` | `Web10.Radio.Telegram` использует Funogram packages/config. |
| 10 | `confirmed` | Все backend projects и authored backend sources — F#. |
| 11 | `confirmed` | Persistence — Npgsql/ADO.NET; ORM/Dapper markers отсутствуют. |
| 12 | `confirmed` | Normal reads/updates используют `IsDeleted`; application `DELETE FROM` отсутствует. |
| 13 | `confirmed` | Queue/work claiming использует `FOR UPDATE SKIP LOCKED`. |
| 14 | `partial` | Missing/basic URI/enum validation есть; semantic config validation неполна. |
| 17 | `confirmed` | Dockerfiles/Compose не содержат Alpine/libmusl; stream-node — Debian. |
| 18 | `confirmed` | API/migrator final images — `10.0-noble-chiseled`. |

## B0

| PLAN line | Status | Claim / evidence |
|---:|---|---|
| 22 | `confirmed` | `src/backend/Web10.Radio.sln` существует и собирается. |
| 23 | `confirmed` | API, Telegram, Database F# projects существуют. |
| 24 | `confirmed` | API references Database и Telegram. |
| 25 | `confirmed` | API и stream-node container packaging существует; оба image build прошли. |
| 26 | `confirmed` | Все 12 `WEB10_*` bindings присутствуют. |
| 27 | `partial` | Aggregated actionable missing-key errors есть; semantic invalid values проходят. |
| 28 | `partial` | Реальный registration/mapping lifecycle не совпадает с checked sequence. |
| 29 | `partial` | Все named checks есть; Telegram/S3/Local semantics проверяются неполно. |

## B1

| PLAN line | Status | Claim / evidence |
|---:|---|---|
| 33 | `confirmed` | FluentMigrator migration `202607080001L`, 12-digit Int64. |
| 34 | `confirmed` | Separate migrator app/image; Compose требует successful completion. |
| 35 | `confirmed` | Все 16 first-version tables созданы. |
| 36 | `confirmed` | Все 16 mutable tables имеют `IsDeleted`, `CreatedAtUtc`, `UpdatedAtUtc`. |
| 37 | `confirmed` | Active partial indexes присутствуют. |
| 38 | `confirmed` | Connection/transaction helpers реализованы; `Ok` commit, `Error` rollback. |
| 39 | `confirmed` | Все текущие normal repository/read-model reads фильтруют active roots/joins. |
| 40 | `confirmed` | Basic queue claim повторяет exact SPEC ordering и same-transaction update. |
| 41 | `confirmed` | Concurrent test доказывает отсутствие duplicate claim одной row. |
| 42 | `partial` | Executable soft-delete proof покрывает только Tracks; общая SQL-инварианта подтверждена статически. |

## B2

| PLAN line | Status | Claim / evidence |
|---:|---|---|
| 46 | `confirmed` | Все event types/envelope fields и serialization соответствуют SPEC. |
| 47 | `partial` | MailboxProcessor agents существуют, но queue/library agents — только validators; lifetime cancellation отсутствует. |
| 48 | `partial` | Outbox append предшествует dispatch, но ownership/fencing/single-dispatch semantics отсутствуют. |
| 49 | `confirmed` | Inbox pair и outbox append выполняются в одной `withTransactionResult`; duplicate не append-ится. |
| 50 | `contradicted` | S3 отсутствует; concurrent dedupe race; `Running` jobs не reclaim-ятся. |
| 51 | `contradicted` | Claim/start/failure atomic, но successful item не достигает `Played`, crash recovery отсутствует. |
| 52 | `confirmed` | Cache miss атомарно становится `Failed`; emits `PlaybackEnded` + `StreamNodeFailureDetected`; Degraded доказан test. |

## B3

| PLAN line | Status | Claim / evidence |
|---:|---|---|
| 56 | `confirmed` | Player-state fields, camelCase, enums и UTC `Z` projection реализованы. |
| 57 | `partial` | SSE поддерживает только `player.state` и `player.health`. |
| 58 | `confirmed` | Stream availability gate, RFC7807 error, active cached file, range support и framework disposal присутствуют. |
| 59 | `confirmed` | Song/fallback payload реализован. |
| 60 | `partial` | Health route существует, но stale persisted heartbeat не истекает. |
| 61 | `contradicted` | Secret/raw dedupe есть, но accepted update не dispatch-ится и после 204 теряется. |
| 62 | `partial` | Route существует, но `lastUpdateId`/`lastError` всегда null. |
| 64 | `confirmed` | `route`, `status`, `traceId`, `correlationId`, `elapsedMs` присутствуют. |
| 65 | `partial` | Девять API tests есть, но admin tests закрепляют отсутствие auth; wire/negative coverage существенно неполно. |

## B4

| PLAN line | Status | Claim / evidence |
|---:|---|---|
| 69 | `confirmed` | `WEB10_TELEGRAM__BOT_TOKEN` попадает в `Config.defaultConfig.Token`. |

Unchecked commands/payments не считались regressions.

## B5

| PLAN line | Status | Claim / evidence |
|---:|---|---|
| 84 | `confirmed` | `src/stream-node/Dockerfile` и `scripts/check-runtime.sh` существуют; image/runtime smoke прошёл. |

Unchecked Xvfb/kiosk/x11grab/RTMP/heartbeat behavior не оценивался как completed work.

## B6

| PLAN line | Status | Claim / evidence |
|---:|---|---|
| 101 | `partial` | Compose topology работает, но documented immediate health curls race API readiness. |
| 102 | `confirmed` | Settled smoke: PostgreSQL healthy, migrator exited 0, API Up, migration present, health endpoints respond. |
| 103 | `confirmed` | README документирует image policy, build/up/ps, health, migration и cleanup. |
| 106 | `confirmed` | Stripped release process exits non-zero и перечисляет все 12 keys без secret values. |
| 107 | `confirmed` | Built-in grep: application-domain `DELETE FROM` отсутствует. |

`environment-blocked`: **нет**.

---

# Original exact verification results — historical baseline

## Isolation and cleanup

Derived resources:

```text
Compose project: web10-radio-review-30208d989fa7
API image:       web10-radio-api:30208d989fa7
Migrator image:  web10-radio-migrator:30208d989fa7
Stream image:    web10-radio-stream-node:30208d989fa7
Override:        /tmp/web10-radio-review-30208d989fa7.override.yaml
Admin capture:   /tmp/web10-radio-admin-30208d989fa7.json
```

Перед первым create:

- containers: none;
- images: all three absent;
- network: absent;
- all three volumes: absent;
- both temp files: absent.

После mandatory finalizer повторная проверка дала тот же результат: всё absent.

## Toolchain

```text
dotnet --info
SDK: 10.0.301
Host: 10.0.9
RID: linux-x64
global.json: repository global.json
Exit: 0
```

## Restore

```text
dotnet restore src/backend/Web10.Radio.sln
Determining projects to restore...
All projects are up-to-date for restore.
Exit: 0
```

## Release build

```text
dotnet build src/backend/Web10.Radio.sln -c Release --no-restore

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:12.25
Exit: 0
```

## Full tests

```text
dotnet test src/backend/Web10.Radio.sln -c Release --no-build --no-restore

Passed!  - Failed:     0, Passed:    46, Skipped:     0, Total:    46,
Duration: 1 m 29 s - Web10.Radio.Tests.dll (net10.0)
Exit: 0
```

Test inventory:

| File | Count |
|---|---:|
| `ApiContractTests.fs` | 9 |
| `BackgroundWorkerTests.fs` | 12 |
| `ConfigurationTests.fs` | 3 |
| `DatabaseMigrationTests.fs` | 2 |
| `DatabaseSessionResultTests.fs` | 2 |
| `DomainEventTests.fs` | 7 |
| `LibraryScanRepositoryTests.fs` | 2 |
| `MigrationMetadataTests.fs` | 1 |
| `OutboxEventRepositoryTests.fs` | 2 |
| `PaymentRepositoryTests.fs` | 1 |
| `PlaybackQueueRepositoryB2Tests.fs` | 2 |
| `PlaybackQueueRepositoryTests.fs` | 1 |
| `RepositoryErrorTests.fs` | 1 |
| `TelegramUpdateInboxRepositoryTests.fs` | 1 |

## Stripped process config check

```text
env -i HOME="$HOME" PATH="$PATH" ASPNETCORE_ENVIRONMENT=Production \
  dotnet src/backend/Web10.Radio.API/bin/Release/net10.0/Web10.Radio.API.dll
```

Result:

```text
Exit: 134
Invalid Web10 configuration:
- WEB10_POSTGRES__CONNECTION_STRING ...
- WEB10_TELEGRAM__BOT_TOKEN ...
- WEB10_TELEGRAM__WEBHOOK_SECRET ...
- WEB10_TELEGRAM__CHANNEL_ID_OR_USERNAME ...
- WEB10_STREAM__RTMP_URL ...
- WEB10_STREAM__RTMP_KEY ...
- WEB10_STREAM__STAGE_URL ...
- WEB10_STORAGE__TYPE ...
- WEB10_STORAGE__LOCAL_ROOT ...
- WEB10_STORAGE__S3_BUCKET ...
- WEB10_OTEL__EXPORTER_OTLP_ENDPOINT ...
- WEB10_DATA_PROTECTION__KEY_RING_PATH ...
```

Все 12 keys перечислены. Secret values не выведены. Port до failure не bind-ился.

## Static invariants

Built-in grep results:

```text
(?i)\bDELETE\s+FROM\b
Paths: API, Database/Repositories, Telegram
Result: No matches found
```

```text
EntityFramework|DbContext|Dapper|ExecuteDelete
Path: src/backend
Result: No matches found
```

```text
alpine|musl
Paths: three Dockerfiles + compose.yaml
Result: No matches found
```

Exact image literals подтверждены:

```text
mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled
debian:bookworm-slim
postgres:17
```

## Isolated Compose smoke

Build:

```text
docker compose ... -p web10-radio-review-30208d989fa7 build
Exit: 0
Images built:
- web10-radio-api:30208d989fa7
- web10-radio-migrator:30208d989fa7
```

Startup:

```text
docker compose ... up -d api
Exit: 0
```

`ps -a`:

```text
api        Up
migrator   Exited (0)
postgres   Up (healthy)
```

Первая немедленная проверка:

```text
curl -fsS http://localhost:8080/health/live
Exit: 56
curl: (56) Recv failure: Connection reset by peer
```

```text
curl -fsS http://localhost:8080/health/ready
Exit: 56
curl: (56) Recv failure: Connection reset by peer
```

Settled retry через `1.5s`:

```text
/health/live
Exit: 0
Body: Healthy
```

```json
{
  "status": "Degraded",
  "checks": [
    { "name": "api", "status": "Healthy" },
    { "name": "telegram-adapter", "status": "Healthy" },
    { "name": "postgresql", "status": "Healthy" },
    { "name": "storage", "status": "Healthy" },
    { "name": "stream-node-heartbeat", "status": "Degraded" }
  ]
}
```

Migration:

```text
SELECT "Version" ... WHERE "Version" = 202607080001;
Exit: 0
Output: 202607080001
```

Admin security check:

```text
GET /api/v0/admin/social-links
Exit: 0
HTTP status: 200
Body: []
Expected: 401 or 403
```

SSE:

```text
HTTP/1.1 200 OK
Content-Type: text/event-stream
event: player.state
data: {...}
curl exit: 28
```

Exit `28` ожидаем после deliberate two-second timeout. Static reachable SSE names: только `player.state` и `player.health`; три обязательных имени отсутствуют.

## Stream-node skeleton

Build:

```text
docker build -t web10-radio-stream-node:30208d989fa7 src/stream-node
Exit: 0
```

Run:

```text
docker run --rm web10-radio-stream-node:30208d989fa7
Exit: 0
```

Output:

```text
/usr/bin/Xvfb
/usr/bin/chromium
/usr/bin/ffmpeg
/usr/bin/liquidsoap
Chromium 150.0.7871.46 built on Debian GNU/Linux 12 (bookworm)
ffmpeg version 5.1.9-0+deb12u1
Liquidsoap 2.1.3
```

Это подтверждает только checked skeleton, не unchecked Xvfb/kiosk/x11grab/RTMP/heartbeat pipeline.

Внешняя semantic grounding:

- [ASP.NET Core dependency injection](https://learn.microsoft.com/aspnet/core/fundamentals/dependency-injection?view=aspnetcore-10.0)
- [ASP.NET Core health checks](https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0)
- [`Results.Stream` disposal/range behavior](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.results.stream?view=aspnetcore-10.0)
- [Npgsql basic usage and transactions](https://www.npgsql.org/doc/basic-usage.html)
- [FsToolkit.ErrorHandling](https://github.com/demystifyfp/FsToolkit.ErrorHandling)
- [Funogram](https://github.com/dolfik1/Funogram)

---

# Original mandatory gates до следующей backend-фазы — historical baseline

1. **Playback lifecycle**
   - реализовать `Playing → Played|Failed`;
   - successful `PlaybackEnded`;
   - lease/recovery для stale `Claimed`/`Playing`;
   - доказать продолжение очереди после первого track и после crash.

2. **Admin security**
   - закрепить auth mechanism;
   - policy на весь `/api/v0/admin/*`;
   - unauthenticated/unauthorized tests для каждого method;
   - только затем реализовывать admin mutations.

3. **Telegram webhook**
   - typed Funogram parsing;
   - реальный call в `ITelegramUpdateEventIngestor`;
   - domain outbox before `204`;
   - adapter `lastUpdateId`/`lastError`;
   - strict single secret header;
   - bounded body и `413`;
   - concurrent/duplicate/malformed tests.

4. **Outbox correctness**
   - claim owner/attempt token;
   - fenced `markProcessed`/`markFailed`;
   - исключить direct-dispatch/relay race;
   - определить ordering scope;
   - idempotency tests для side-effect handlers.

5. **Library scanner**
   - stale `Running` reclaim;
   - post-claim failure/cancellation handling;
   - database unique invariant для active storage path с NULL semantics;
   - concurrent scan test;
   - реализовать S3 либо снять его из checked v0 contract.

6. **SSE**
   - добавить `player.queue`, `player.say`, `player.donation`;
   - exact fragment payloads;
   - тестировать initial и subsequent events.

7. **Health/config**
   - единый persisted-heartbeat freshness policy;
   - Telegram/storage readiness проверяет operability, не только config;
   - semantic startup validation connection string, schemes, paths, token/channel/bucket.

8. **Compose**
   - добавить API healthcheck;
   - documented bounded wait/`up --wait`;
   - чистый smoke не должен требовать ручной задержки.

9. **Executable evidence**
   - добавить tests на Blocker/High paths;
   - schema/index/rollback introspection;
   - live/range stream;
   - UUIDv7 persistence;
   - static forbidden-SQL/image-policy checks;
   - clean Compose startup test.

Исторический verdict оставался **NO-GO** до выполнения этих gates; текущий результат их повторной проверки — **GO**.

---

# Remediation verification

## Blocker

- **B-1 fixed:** queue claims имеют owner/attempt/lease fencing; stale `Claimed|Playing` reclaim-ятся; authenticated stream-node callbacks renew lease и atomically commit `Played|Failed` вместе с `PlaybackEnded`; crash/terminal/next-item regressions покрыты.

## High

- **H-1 fixed:** весь `/api/v0/admin/*` group требует exact bearer policy; missing, malformed, wrong, comma/multiple credentials получают `401` до handler execution.
- **H-2 fixed:** webhook делает bounded typed Funogram parsing, transactional `(updateId,eventType)` inbox/outbox dedupe и только затем `204`; `/request`, `/say` и `successful_payment` проверены до idempotent domain-row effect.
- **H-3 fixed:** durable publisher только append-ит; единственный relay dispatch path держит global ordered session lease и owner/attempt-fenced terminal updates.
- **H-4 fixed:** library jobs имеют reclaimable lease, renewal и fenced terminal transitions; cancellation/exception не оставляют вечный `Running`.
- **H-5 fixed:** active storage path защищён двумя partial unique indexes с explicit NULL semantics; conflict-safe insert удаляет losing orphan, а forward migration `202607100002` детерминированно rewires legacy references/links и soft-deletes loser tracks.
- **H-6 fixed:** scanner page-streams `ListObjectsV2`, renews lease per page, persists exact key/size metadata и оставляет S3 discovery uncached; default и non-default database S3 backends используют documented AWS provider chains.

## Medium

- **M-1/M-2 fixed:** DI registration/mapping contract совпадает с host lifecycle; Compose waits for PostgreSQL, migrator и managed API liveness healthcheck.
- **M-3/M-4/M-5 fixed:** causal outbox scope globally serialized; unsafe claim helper удалён; mailbox queued work и lifetime связаны с host cancellation без late effects.
- **M-6/M-8/M-12 fixed:** единая 30-second persisted-heartbeat policy применяется в state/health/stream/readiness; Telegram, PostgreSQL и storage readiness проверяют operability; Telegram state monotonic records accepted update/error lifecycle.
- **M-7 fixed:** queue/library/Telegram agents выполняют реальные declared workflows, а не validation-only no-op.
- **M-9 fixed:** SSE deterministically emits all exact `player.state|queue|say|donation|health` event names with matching fragments.
- **M-10 fixed:** tests assert exact audit columns, FK/check definitions, partial predicates, Down→Up, parent soft-delete joins, UUIDv7 persistence и legacy duplicate migration behavior.
- **M-11 fixed:** startup rejects malformed Npgsql/URI/Telegram/S3/storage/path/bearer configuration before binding a port; Local explicit `S3_FORCE_PATH_STYLE=false` remains valid.
- **M-13 fixed:** executable regression matrix now covers every remediation boundary, including callbacks, typed downstream effects, publisher-vs-relay, pre-start recovery, S3 metadata/lease, host stop, health routes, static SQL/image policy and clean Compose timing.
- **M-14/M-15 fixed:** route failures retain exception/wire-status/request identity evidence; webhook enforces exact secret cardinality and 1 MiB pre-allocation bound with `413`.

Следующие unchecked B4/B5/B6 checklist items остаются запланированной функциональностью следующих фаз, а не незакрытыми defects этого readiness gate.
