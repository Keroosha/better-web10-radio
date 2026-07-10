# Frontend → Backend: requirements feedback перед B5

**От:** frontend milestone (Claude)
**Кому:** backend / stream-node milestone (ChatGPT/OMP)
**Дата:** 2026-07-10
**Статус:** мы **не готовы** заходить в B5 (stream-node). Ниже — почему, с трассировкой по коду, и что нужно от бэкенда.

Все *новые* формы контракта помечены **`(PROPOSED — pin in SPEC)`** — это предложения к ратификации в `SPEC.md`,
а не существующий контракт. Фронт не станет заводить эти DTO в `shared`, пока они не запинены в SPEC. Имена
существующих роутов и enum'ов — verbatim из `SPEC.md` §5 (camelCase на API-границе).

---

## 1. TL;DR / вердикт

Бэкенд на самом деле **не пустой** — это заблуждение. Реально написаны и работают: все `/api/v0/player/*`, SSE
`/player/events` с пятью именованными событиями, `/say`-модерация (list + approve + reject), Telegram/Stars
flow, playback-worker и библиотечный сканер. Фронт web-stage подключён к живому API (SSE + polling fallback, без
mock-таймеров).

Но **два непостроенных механизма приёма (intake) заклинили всю систему**:

- **Сцена всегда `offline`** — потому что в бэкенде нет пути, которым статус мог бы стать `live`. Это не баг фронта.
- **Музыка не играет вообще** — потому что треки физически не могут попасть в БД (единственный триггер — заглушка).

Это и есть конкретное содержание «слишком много заглушек» и «не готовы к B5». Плюс отдельный запрос: заменить
статический bearer-токен админки на классический **login/password**.

Ключевая мысль для приоритизации: большинство разрывов — это **отсутствующие *триггеры/эндпоинты приёма***, а не
целые ненаписанные подсистемы. Сами подсистемы (сканер, playback, heartbeat-агент) написаны — их просто **нечем
запустить**.

---

## 2. Диагноз — прямой ответ на вопрос «наши баги или бэкенду не хватает механизмов?»

Вопрос был: *сцена всегда `offline` даже с поднятым бэком, и я не могу настроить через админку откуда брать
музыку — это наши недоделки или бэкенду не хватает механизмов?*

**Ответ: оба симптома — недостающие механизмы бэкенда. Это не баги фронта и не просто ненастроенный конфиг.**

### 2.A. Почему сцена всегда `offline`

`stream.status` вычисляется **только** из самой свежей (≤30с) строки таблицы `StreamNodeHeartbeats`:

- Запрос + маппинг: `ApiReadModels.fs:100-104` (`latestHeartbeatSql`) и `:228-265` (`streamStateFromHeartbeat`).
- Порог свежести 30с: `Health.fs:19-23` (`isFresh`).
- Маппинг статуса: `ApiReadModels.fs:43-51` (`Live→live`, `Starting→starting`, `Degraded|Restarting|Failed→degraded`,
  всё остальное `→offline`).

Дерево решений: **нет строки heartbeat → `offline`**; строка старше 30с → `offline` (stale); свежая строка →
маппится её `Status`.

Теперь — кто вообще может записать `Live`/`Starting` в эту таблицу? Только обработчик события
`StreamNodeHeartbeatReceived` (`BackgroundWorkers.fs:237-263`). И вот разрыв:

> **Событие `StreamNodeHeartbeatReceived` нигде не публикуется, и HTTP-эндпоинта для приёма heartbeat не существует.**

Проверено по всему backend:
- `StreamNodeHeartbeatReceived` встречается только в определении/сериализации (`Events.fs:25,46,67,88`), в
  диспетчере (`BackgroundWorkers.fs:407`), в обработчике (`:382`) и в одном тесте. **Ни одного продюсера.**
- Группа `/api/v0/stream-node/*` содержит **только** `lease` и `completion`
  (`ApiEndpoints.fs:1342-1345`) — **нет** `POST /api/v0/stream-node/heartbeat`.

Единственные heartbeat-строки, которые вообще появляются, — это хардкод `"Degraded"` при неудачном playback-claim
(`BackgroundWorkers.fs:226`). Поэтому максимум, что можно увидеть, — кратковременный `degraded` (≤30с), потом
снова `offline`. **`live` и `starting` структурно недостижимы.**

**Вердикт A:** это разрыв в бэкенде. Даже **полностью собранный stream-node не сдвинет статус с `offline`**, потому
что принимающая половина фичи (эндпоинт + публикация события) не написана. Read-model, порог свежести,
репозиторий и агент-обработчик уже готовы и заработают в момент, когда события пойдут. Фронт при этом честно
отражает состояние бэка — **баг не на фронте.**

### 2.B. Почему не играет музыка

Сначала важное уточнение к твоей формулировке: **источник музыки задаётся конфигом, а не админкой.**
`WEB10_STORAGE__TYPE` + `WEB10_STORAGE__LOCAL_ROOT` (или S3-ключи) уже сообщают сканеру, где искать
(`Configuration.fs:301-307`; SPEC §9 строки 360-371; используется как default-backend в
`BackgroundWorkers.fs:593-601, 638-646`). Таблица `StorageBackends` в БД — только для *дополнительных* не-default
бэкендов (SPEC:371), и в неё вообще ничего не вставляется. Значит `PUT /api/v0/admin/storage` **не требуется** для
базовой работы — «не могу настроить откуда брать музыку через админку» это реальный симптом, но он указывает не на
тот блокер.

Настоящий мёртвый узел — **загрузка треков в БД**:

- Единственный писатель в `Tracks` — библиотечный сканер (`LibraryScanRepository.fs:127-144, 382`).
- Сканер запускается только по строке в `LibraryScanJobs` (`claimNextJob`).
- Единственное, что могло бы создать такую строку, — `POST /api/v0/admin/library/scan`, но это **`501`
  заглушка** (`ApiEndpoints.fs:1363` → `adminPlaceholder`).
- В `LibraryScanRepository.fs` **нет функции `createJob`/`insertJob`**, а событие `LibraryScanRequested`
  **никто не публикует** (продюсера нет). Единственные `INSERT INTO "LibraryScanJobs"` в репозитории — **только в
  тестах** (`Web10.Radio.Tests/…`), не в проде.

Следствие цепочки: `Tracks` всегда пустая → Telegram `/request` умеет только *искать* существующие треки
(`TelegramBotWorkflow.fs:406`), но не может создать новый → нет строк `PlaybackQueue` → полностью написанный
playback-worker (`BackgroundWorkers.fs:1042-1306`) простаивает → `nowPlaying` пустой → `/api/v0/player/stream`
отдаёт `503`.

**Вердикт B:** это разрыв в бэкенде. Сканер и весь playback-конвейер **написаны и корректны** — их просто **нечем
запустить**. Рабочий `PUT /storage` тут не поможет; нужен рабочий **триггер сканирования**.

---

## 3. Reality check — что реально работает vs заглушки

| Область | Статус | Примечание / evidence |
|---|---|---|
| `GET /api/v0/player/state`, `/events` (SSE), `/stream`, `/song`, `/health` | ✅ работает | реальный read-model из Postgres; SSE шлёт `player.state/queue/say/donation/health` |
| `/say`-модерация: list + approve + reject | ✅ работает | единственная полностью рабочая admin-возможность; фронт-страница готова |
| Playback-worker, библиотечный сканер (логика) | ✅ написаны | но **недостижимы** — нет триггера/данных (см. 2.B) |
| Heartbeat-агент, read-model статуса, порог свежести | ✅ написаны | но **нет intake** — нет эндпоинта/продюсера (см. 2.A) |
| web-stage: сцена + оверлеи + тост, подключены к живому state | ✅ работает | без mock-таймеров |
| RFC7807-ошибки, Zod-валидация DTO | ✅ работает | контракт-строгий клиент |
| **Приём heartbeat** (эндпоинт + событие) | ❌ отсутствует | пин статуса на `offline` |
| **Триггер library scan** (`createJob` + не-`501` роут) | ❌ отсутствует | музыка не играет |
| Остальные admin-writes (playlists, storage, stream-node control, PUT'ы) | 🟡 `501` заглушки | `ApiEndpoints.fs:1350-1365` |
| Admin login (UX/сессия) | 🟡 статический токен, in-memory | теряется на refresh, нет identity/logout |
| Dashboard «Stream-node heartbeat» | 🟡 хардкод `"unavailable"` | `DashboardPage.tsx:42` — до пина `stream-node/status` |
| E2E-проверенный донат / оплаченный `/say` | ❌ ни разу не прошли | нет dev-пути инъекции |

Акцент: почти всё «красное» — это **триггеры/эндпоинты приёма**, а не целые подсистемы.

---

## 4. Блокер 1 (высший приоритет) — включить конвейер музыки

Без этого ничего нельзя продемонстрировать: нет треков → нет playback → нечего стримить.

**Задача бэкенду:** сделать `POST /api/v0/admin/library/scan` рабочим (снять `501`) и добавить путь постановки
задачи сканирования:
1. `LibraryScanRepository.createJob` — `INSERT INTO "LibraryScanJobs"` с soft-delete колонками (в проде, не только
   в тестах).
2. Роут кладёт задачу (или публикует `LibraryScanRequested`, у которого сейчас нет продюсера) → уже написанный
   `LibraryScanHostedService` подхватит и наполнит `Tracks`.

**Предлагаемый контракт (PROPOSED — pin in SPEC):**

```
POST /api/v0/admin/library/scan
  body: {}                      // опц. { "storageBackendId": "<uuid-v7>" } для не-default бэкенда
  → 202 { "scanJobId": "<uuid-v7>" }

GET /api/v0/admin/library/scan/{scanJobId}     // чтобы админка показывала прогресс
  → 200 {
      "scanJobId": "<uuid-v7>",
      "status": "queued|running|completed|failed",   // camelCase-проекция LibraryScanJobs.Status
      "discoveredCount": 128,
      "startedAtUtc": "…Z|null",
      "finishedAtUtc": "…Z|null",
      "failureReason": "…|null"
    }
```

Storage-конфиг уже env-driven, так что `GET/PUT /api/v0/admin/storage` — **не блокер**, а только рантайм-добавление
не-default бэкендов (roadmap). Фронт готов сделать страницу «Library scan» сразу, как форма запинена
(`UnpinnedPage.tsx` уже перечисляет этот роут; nav-бейдж `501` снимется автоматически).

---

## 5. Блокер 2 — поднять статус потока (почему мы не готовы к B5)

Порядок важен: **сначала intake heartbeat, потом сам stream-node.** Если собрать stream-node без приёмного
эндпоинта — ему некуда слать heartbeat, и статус останется `offline` (см. 2.A).

**Задача бэкенду (часть 1 — intake, делать первой):**
- Добавить `POST /api/v0/stream-node/heartbeat` под политикой `Web10StreamNode` (тем же
  `WEB10_STREAM__CALLBACK_TOKEN`, что и lease/completion), который публикует `StreamNodeHeartbeatReceived`.
  Обработчик, репозиторий, порог свежести и read-model уже готовы — нужен только приёмник + продюсер события.

**Предлагаемый контракт (PROPOSED — pin in SPEC):**

```
POST /api/v0/stream-node/heartbeat
  Authorization: Bearer <WEB10_STREAM__CALLBACK_TOKEN>
  body: {
    "status": "starting|live|degraded",      // → StreamNodeHeartbeats.Status
    "failureReason": "…|null",
    "metadata": { "bitrateKbps": 192, … }     // опционально
  }
  → 204
```

**Задача бэкенду / stream-node (часть 2 — рантайм, B5 по SPEC §11):**
Xvfb → Chromium kiosk на web-stage `?capture=1` → LiquidSoap (аудио-граф) → FFmpeg/x11grab (захват сцены) → RTMP в
Telegram → **периодический heartbeat в эндпоинт из части 1** + bounded restart. Сейчас `src/stream-node/` — это
только `Dockerfile` + `scripts/check-runtime.sh` (проверка наличия бинарей).

**Что фронт должен увидеть:** переходы `stream.status` `offline→starting→live→degraded`, реальный
`offlineReason`, и живое значение свежести heartbeat для строки Dashboard, которая сейчас хардкод
(`DashboardPage.tsx:42`).

**Минимальный демо-срез:** даже до полного RTMP — процесс (или простой `curl`), шлющий `status:"live"` heartbeat'ы,
переключает всю сцену и dashboard в «live». Просим это как **первую веху**, раньше полного захвата/энкода.

---

## 6. Блокер 3 — Auth: заменить статический bearer-токен на login/password

**Проблема.** Сейчас `AdminAuthGate.tsx` — это поле, куда вставляют сырой `WEB10_ADMIN__TOKEN`; токен уходит в
`setAdminToken` и хранится **только в памяти** модуля (`shared/src/api/client.ts`). Никакого logout, сессии,
identity; **на refresh всё теряется** и токен надо вставлять заново. SPEC §5 (строка 84) и §9 сейчас *предписывают*
статический токен — поэтому это **запрос на изменение SPEC**, а не просто имплементация.

**Решение (обсуждено, роли пока не нужны — достаточно login/password).**

**Предлагаемый контракт (PROPOSED — pin in SPEC):**

```
POST /api/v0/admin/auth/login
  body: { "username": "...", "password": "..." }
  → 200 (ставит сессию)   |  401 admin.auth.invalid_credentials

POST /api/v0/admin/auth/logout
  → 204 (гасит сессию)

GET  /api/v0/admin/auth/session
  → 200 { "username": "..." }   |  401
  // фронт зовёт при загрузке, чтобы восстановить авторизацию после refresh — чинит текущий баг «refresh теряет вход»
```

- **Транспорт сессии:** рекомендуем **httpOnly + Secure + SameSite cookie** вместо JS-читаемого токена
  (админка same-origin; нечего эксфильтрировать из localStorage; переживает refresh). Нужна CSRF-защита
  (SameSite=Strict/Lax или double-submit token).

**Заметки по имплементации (с учётом инвариантов бэка):**
- ASP.NET Core Identity `IPasswordHasher` + `IUserStore`/`IUserPasswordStore`, но **на кастомном ADO.NET-сторе**
  (НЕ EF Core Identity store — инвариант репозитория «только ADO.NET, без ORM/EF»).
- Новая таблица `Users` с обязательными soft-delete колонками (`IsDeleted`/`CreatedAtUtc`/`UpdatedAtUtc`),
  уникальным `Username` и `PasswordHash`.
- Первый админ **сидится из конфига** на старте (напр. `WEB10_ADMIN__USERNAME` + пароль/хеш) — публичной
  регистрации нет.
- `WEB10_STREAM__CALLBACK_TOKEN` (политика stream-node) оставляем как есть; `WEB10_ADMIN__TOKEN` можно сохранить
  как break-glass/bootstrap.

**Что меняет фронт (когда контракт запинен):** форма username/password вместо поля токена в `AdminAuthGate.tsx`;
`client.ts` переходит на `credentials:'include'` и выкидывает `setAdminToken`/`adminToken`; probe
`GET /auth/session` на загрузке; глобальный `401` → назад на логин.

---

## 7. Блокер 4 — запинить остальные admin write-контракты

Все ниже сейчас `501 admin.contract_unpinned` (`ApiEndpoints.fs:1350-1365`). Фронт готов превратить
`UnpinnedPage.tsx`-заглушки в реальные страницы, как только формы запинены. Заголовок `shared/src/api/admin.ts`
уже перечисляет их как unpinned. Предлагаемые формы **(PROPOSED — pin in SPEC)**, camelCase:

- **`PUT /api/v0/admin/donation-goal`** — `{ "title": "...", "goalStars": 5000 }` (integer Stars). Разблокирует
  редактирование цели, которую мы уже показываем. Читается парным `GET` (уже работает).
- **`GET/PUT /api/v0/admin/social-links`** — массив существующей формы `socials`-элемента:
  `{ id, kind: "telegram|youtube|instagram|discord|external", name, handle, url, glyph, color, qrImageUrl, isFeatured }`.
- **`GET /api/v0/admin/stream-node/status`** — проекция `StreamNodeHeartbeats`:
  `{ "status": "offline|starting|live|degraded", "lastHeartbeatUtc": "…Z|null", "failureReason": "…|null" }`.
  Заменит хардкод в `DashboardPage.tsx:42`.
- **`POST /api/v0/admin/stream-node/restart`** (в SPEC есть) **+ предлагаем `POST .../start` и `.../stop`** — в SPEC
  сейчас только restart, а для «запустить/остановить поток» из админки нужны явные start/stop.
- **`GET/POST /api/v0/admin/playlists`**, **`GET/POST/PUT /api/v0/admin/playlists/{playlistId}/items`** — формы
  плейлистов и порядка.
- **`GET/PUT /api/v0/admin/storage`** — конфиг доп. (не-default) бэкендов; roadmap, не блокер (см. 2.B).

**Отдельно — чего в SPEC нет вообще, но админке реально нужно (просим бэкенд решить v0 vs roadmap):**
- **Управление воспроизведением**: skip / pause / requeue текущего трека. Сейчас нет ни роута, ни события.
- **Финансовый обзор / платежи**: список Payments, суммы, статусы. `/say`-модерация уже есть; **рефанды —
  вне v0** по SPEC §7 (charge id хранится, rejected `/say` остаётся `Paid` и уводит в `/paysupport`).

---

## 8. Блокер 5 — донат end-to-end с бэкенда

Виджет `DonationGoalWidget` + `DonationToast` уже подключены к `player.state.donationGoal` и SSE-событию
`player.donation` (реальный SQL read-model над `DonationGoals`/`Payments`). Разрыв: **ни один донат ни разу не
прошёл end-to-end** — данных нет, проверить нечем.

**Просим бэкенд:**
1. Засидить активную `DonationGoals` (title + goalStars), чтобы виджет показывал прогресс-бар.
2. Дать **dev-only** способ инъекции тестового `DonationPaid` (или прогнать Stars sandbox-платёж), чтобы фронт
   проверил прогресс-бар и тост.
3. Подтвердить: как агрегируется `raisedStars`, наполняются ли `topDonator`/`recent`, и что `player.donation`
   реально эмитится на `DonationPaid`.

---

## 9. Блокер 6 — Super Chat E2E («чат»)

Уточнили: «чат» = платный **SUPER CHAT** (Telegram `/say`), нового свободного чата в v0 не делаем. Цепочка написана
по кускам: Telegram `/say` → оплата → `PaidPendingModeration` → admin approve → `superChat.messages[]` + SSE
`player.say`. Approved-only фильтр на сцене уже реализован.

**Просим бэкенд:** проверить всю цепочку против реального/тестового Stars-платежа и дать **dev-путь инъекции**
оплаченного `/say`, чтобы фронт подтвердил, что одобренные сообщения доходят до сцены. Модерация в админке уже
рабочая — не хватает именно сквозной проверки от оплаты до экрана.

---

## 10. Приоритеты / определение «готовы к B5»

Порядок по разблокировке демонстрируемого «живого» среза:

1. **Триггер library scan** (Блокер 1) → музыка может играть. *Наивысший рычаг.*
2. **Приём heartbeat** (Блокер 2, часть 1) → сцена может стать `live`. Затем рантайм stream-node (часть 2).
3. **login/password** (Блокер 3).
4. **E2E-проверка доната и super-chat** (Блокеры 5, 6) — сид + dev-инъекция.
5. **Остальные admin write-DTO** (Блокер 4).

**Минимальный демо-срез (vertical slice):** просканировать локальную папку → трек играет (`nowPlaying` наполнен)
→ heartbeat переключает сцену в `live` → один засиженный донат на виджете цели → один оплаченный `/say`,
промодерированный на сцену. **Только после этого** полноценный B5 (RTMP-захват/энкод/пуш) имеет смысл.

---

## 11. Приложение — точки изменения во фронте (готовность фронта по каждому блокеру)

| Блокер | Файлы фронта, которые разблокируются |
|---|---|
| 1 — library scan | новая страница на месте `admin/src/pages/unpinned/UnpinnedPage.tsx`; новые функции в `shared/src/api/admin.ts` |
| 2 — stream status | `admin/src/pages/dashboard/DashboardPage.tsx:42` (снять хардкод); web-stage уже реагирует на `stream.status` |
| 3 — auth | `admin/src/features/admin-auth/AdminAuthGate.tsx` (форма); `shared/src/api/client.ts` (`setAdminToken`/`adminToken` → `credentials:'include'`) |
| 4 — admin writes | `shared/src/api/admin.ts` + `shared/src/domain/admin.ts` (новые DTO); соответствующие `pages/*` |
| 5 — донат | изменений на фронте не требуется — только данные/сид/эмиссия на бэке |
| 6 — super chat | изменений на фронте не требуется — только сквозная проверка на бэке |

Фронт заводит новые DTO в `shared` **только после** пина форм в `SPEC.md`. До этого страницы остаются
`UnpinnedPage`-заглушками с честным `501`-бейджем — чтобы админка не притворялась рабочей там, где контракт ещё
не зафиксирован.
