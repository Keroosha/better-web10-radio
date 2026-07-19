# Web10.Radio — Getting Started: настройка и запуск эфира

Этот документ — практический runbook: как из чистого репозитория поднять весь стек,
собрать эфирный плейлист и вывести живой поток в Telegram-канал
`@netscapedidnothingwrong`. Всё делается через браузер (публичная сцена + админка),
без ручных вызовов API.

Проверено end-to-end 2026-07-11: 33 трека найдено, активный плейлист играет, статус
`live`, звук идёт в браузере, RTMP стабильно публикуется в Telegram
(`web10-rtmp.is_started = true`, 18 подряд `Live`-heartbeat без единого reconnect).

Как выглядит успешный результат — `docs/e2e-stage-live.png`.

---

## 0. Карта сервисов и адресов

`compose.yaml` поднимает вертикальный срез из шести контейнеров:

| Сервис        | Что делает                                                            | Адрес с хоста                     |
|---------------|----------------------------------------------------------------------|-----------------------------------|
| `postgres`    | PostgreSQL 17, вся durable-стейт                                      | `localhost:5432`                  |
| `migrator`    | One-shot FluentMigrator, применяет схему и выходит                    | —                                 |
| `api`         | ASP.NET/F# host: `/api/v0/*`, фоновые воркеры, health                 | `localhost:8080`                  |
| `frontend`    | nginx: публичная сцена на `/`, админка на `/admin/`, проксирует `/api`| **сцена** `localhost:8090/` · **админка** `localhost:8090/admin/` |
| `rtmp-sink`   | Внутренний RTMP-приёмник для оффлайн-смоука (в реальном стриме не участвует) | `localhost:8091/stat`      |
| `stream-node` | Xvfb + kiosk Chromium (снимает сцену) + Liquidsoap (H.264/AAC → RTMP) | публикует в Telegram               |

Поток строится так: `stream-node` открывает публичную сцену в kiosk-Chromium под
`:capture=1`, `x11grab` снимает картинку, Liquidsoap подмешивает аудио текущего трека,
кодирует FLV (H.264 video + AAC audio) и публикует на Telegram RTMP. В браузере зритель
слышит тот же трек через `<audio src="/api/v0/player/stream">`.

---

## 1. Требования

- **Docker** (проверено на 29.6.1) и **Docker Compose v2** (`docker compose ...`).
- ~2–3 GB под образы (`stream-node` тяжёлый: Chromium + Liquidsoap ≈ 1.2 GB).
- Свободные порты: `8080`, `8090`, `5432`, `8091`.
- Для **реального** стрима в Telegram: права администратора канала и **открытая
  Live Stream сессия** в этом канале (см. §2). Без активной сессии RTMP-приёмник
  Telegram не примет публикацию.

---

## 2. Секреты Telegram (RTMP-ключ)

Telegram отдаёт RTMP-эндпоинт только когда в канале **запущен Live Stream**:

1. В Telegram: канал → **Live Stream / Прямой эфир** → **Stream with… / Вести эфир через
   приложение** (external encoder).
2. Скопируйте **Server URL** (вида `rtmps://dc4-1.rtmp.t.me/s/`) и **Stream Key**.
3. Оставьте это окно эфира **открытым** — пока сессия активна, RTMP принимает поток.

Создайте локальный `.env` в корне репозитория (он в `.gitignore`, перекрывает
`.defaults.env`) **только** с нужными ключами:

```dotenv
# Server URL из Telegram (обязательно заканчивается на /s/)
WEB10_STREAM__RTMP_URL=rtmps://dc4-1.rtmp.t.me/s/
# Stream Key из того же окна
WEB10_STREAM__RTMP_KEY=1594396085:XXXXXXXXXXXXXXXXXXXXXX
```

Правила валидации (иначе `stream-node` не стартует): `RTMP_URL` — схема `rtmp`/`rtmps`,
без пробелов; `RTMP_KEY` — ≥16 символов, без пробелов.

**Опционально — бот и Stars.** Команды бота (`/request`, `/say`) и оплату Stars
включают реальный `WEB10_TELEGRAM__BOT_TOKEN` (+ включённые Payments у @BotFather).
Для одного лишь вывода эфира в канал бот-токен не нужен: коммитнутый смоук-токен
оставляет `/health/ready` в `503` (проба `getMe` падает — это ожидаемо), но `api`
всё равно `healthy`, а поток работает.

> Всё остальное (пароль админа `admin`/`admin-password`, цены Stars, connection string
> и т.д.) уже лежит рабочими дефолтами в `.defaults.env` — стек заводится «из коробки».

---

### 2.1 Production server с optional Xray egress

Минимальный production bundle использует один owner-only `.env`. Этот файл одновременно
выбирает Compose project/topology, участвует в interpolation и передаёт application
configuration контейнерам. Одноразовая подготовка:

```sh
cp .env.prod.example .env
cat .env.xray.example deploy/compose.production.env >> .env
${EDITOR:-vi} .env
chmod 0600 .env

mkdir -p .secrets
chmod 0700 .secrets
# Перенесите существующий Xray client outbound в этот файл и задайте ему tag "proxy".
${EDITOR:-vi} .secrets/xray-outbound.json
chmod 0600 .secrets/xray-outbound.json
```

`deploy/compose.production.env` задаёт:

```text
COMPOSE_PROJECT_NAME=web10-radio-prod
COMPOSE_FILE=compose.prod.yaml:compose.xray.yaml
WEB10_PROD_ENV_FILE=.env
WEB10_XRAY_ENV_FILE=.env
```

После этого все команды запускаются непосредственно из deployment directory:

```sh
docker compose config -q
docker compose pull
docker compose up -d --wait --wait-timeout 180 --remove-orphans
docker compose ps

# Перезапуск одного сервиса или всего проекта:
docker compose restart stream-node
docker compose restart

# Диагностика:
docker compose logs -f xray-proxy
docker compose logs -f stream-node
docker compose exec xray-proxy iptables -t nat -L WEB10_XRAY -v -n
```

Глобальные аргументы `-p`, `-f`, `--env-file` и `--profile` больше не нужны.
Если Bot/Stars настроены реальными значениями, добавьте
`COMPOSE_PROFILES=telegram` в `.env`; иначе optional Telegram service не запускается.

Secret имеет exact top-level shape `{ "outbounds": [...] }`, содержит ровно один
`tag: "proxy"` и не содержит protocol `direct`/`freedom`. VLESS/REALITY, Trojan, SOCKS и
дополнительные non-direct helper outbounds остаются Xray-native JSON; endpoint credentials
не попадают в `.env`, Compose command или repository. `inbounds`/`routing` принадлежат
`deploy/xray/base.json`, а не operator secret.

`unhealthy` при initial startup не пропускает Telegram/stream-node. Если tunnel пропал после
успешного старта, direct fallback всё равно нет, но Compose автоматически не остановит уже
запущенные dependents. Восстановите Xray, restart Telegram и выполните admin **Restart** для
stream-node. Terminal `RTMP output failed` — ожидаемая fail-closed ошибка.

Для direct Telegram egress задайте в `.env`
`COMPOSE_FILE=compose.prod.yaml`, верните direct RTMP URL/key и примените topology:

```sh
docker compose up -d --wait --wait-timeout 180 --remove-orphans
```

Тот же `COMPOSE_PROJECT_NAME` сохраняется в `.env`, поэтому `xray-proxy` удаляется как
orphan, а database state и immutable application images остаются прежними.

## 3. Положить музыку в библиотеку

Аудио сканируется из каталога `./.storage` (монтируется в
`/var/lib/web10/storage`). Поддерживаемые форматы — по расширению (`.mp3`, `.wav`, …).
В репозитории уже лежат две папки: `synthwave/` и `locked club/`. Чтобы добавить своё —
просто скопируйте файлы внутрь `./.storage/<любая-папка>/` до сканирования.

Метаданные `artist`/`title` берутся из пути файла (тег-ридер в v0 не используется),
поэтому имена файлов = то, что увидите в админке и на сцене.

---

## 4. Поднять стек

Из корня репозитория:

```sh
docker compose up -d --wait --wait-timeout 180
```

Первый запуск собирает образы (дальше — из кэша). Дождитесь состояния:

- `postgres`, `api`, `frontend`, `rtmp-sink`, `stream-node` — **healthy**;
- `migrator` — **exited (0)** (это one-shot).

Быстрая проверка:

```sh
docker compose ps
```

`stream-node` сразу после старта идёт в статус `starting` (desired state по умолчанию —
`running`): Xvfb, Chromium и Liquidsoap уже подняты, но играть нечего — плейлист пуст.
Это нормально, продолжаем в админке.

---

## 5. Настройка эфира через браузер

Всё ниже — клики в админке, никакого curl.

### 5.1 Вход
Откройте **`http://localhost:8090/admin/`** → логин `admin` / `admin-password`
(значения из `.defaults.env`; пароль обязан быть 12–256 символов). Появится ретро-окно
с вкладками.

### 5.2 Сканирование библиотеки
Вкладка **Library scan** → backend `Default (local)` → **Start scan**. Статус опрашивается
раз в секунду и должен дойти до `completed`, показав число найденных треков (в нашем
прогоне — **33**). Скан кладёт каждый локальный файл как `Track` + `TrackFile` с
`IsCached = true` и `CachePath` = путь на диске (для Local-хранилища материализация не
нужна — файл уже локальный).

### 5.3 Политика плейлистов
Во вкладке **Playlists** создаются policy-driven плейлисты, а не единственный ручной список:

1. Заполните **Playlist name**, описание, `Type`, `Source`, `Order`, `Weight`, `Avoid duplicates` и, при необходимости, `Interrupt`/`Is jingle`.
2. Для cadence выберите `Every N songs`, `Every N minutes` или `At minute`; для временных окон добавьте `daysOfWeek`, `startTime`, `endTime`, даты и `timeZoneId`.
3. `Source = AllStorage` — системная политика всех закэшированных треков; `Source = Manual` использует сохранённый упорядоченный список.
4. Для manual-плейлиста найдите треки, нажмите **Add … to playlist**, расставьте **Move up/down**, удалите лишнее и нажмите **Save playlist items**.
5. Отметьте нужные политики **Active**. Несколько активных политик допускаются: scheduler выбирает их по cadence, schedule, interrupt, order, weight и selection credit.

При простое очереди фоновый `PlaybackProgram` атомарно подбирает следующую активную policy, обновляет её scheduler state и ставит кэшированный трек в `PlaybackQueue`. Вне schedule, без подходящего cached item или при нарушении duplicate-window политика пропускается без разрушения очереди.

### 5.4 Запуск эфира
Вкладка **Stream-node**: показывает `Status`, `Desired state`, `Last heartbeat`,
`Bitrate`, `Restart generation` (обновление раз в секунду) и кнопки **Start / Stop /
Restart**.

- **Start** — выставляет desired state `running` (идемпотентно, живой поток не рвёт).
- **Stop** — глушит захват/публикацию.
- **Restart** — увеличивает `restart generation`; используйте, если нода ушла в
  терминальный `failed` (например, RTMP-цель была недоступна, а теперь восстановлена).

Нажмите **Start**. В течение нескольких секунд `Status` пройдёт `starting → live`,
`Bitrate` станет `192 kbps`, `Failure reason` — `—`.

> `Status = live` достигается только когда одновременно: трек реально начал играть
> (`started`-callback), жив Xvfb+Chromium, жив Liquidsoap, `web10-video.is_ready = true`
> **и** `web10-rtmp.is_started = true`. Последнее означает, что публикация в Telegram
> действительно установлена. То есть **`live` = поток уже уходит в канал**.

---

## 6. Проверка результата

### 6.1 Звук в браузере
Откройте публичную сцену **`http://localhost:8090/`**. Должно быть:

- виджет **NOW PLAYING · 24/7** с названием текущего трека и бейджем **LIVE**;
- 3D-сцена (небо-градиент, вода, шахматный пол, храм, вращающийся CD);
- справа сверху — «пилюля» звука. Аудио стартует автоплеем **в mute**; клик по пилюле
  снимает mute (нужен пользовательский жест браузера).

Технически (то, что подтверждали в прогоне): у `<audio>` элемента
`currentSrc = …/api/v0/player/stream`, `paused = false`, `readyState = 4`,
`currentTime` растёт в реальном времени (≈+2.5 c за 2.5 c), после клика `muted = false`,
`volume = 1`. Запрос `/api/v0/player/stream` отвечает **`206 Partial Content`**
(range-стриминг), `/player/state` и `/player/events` — `200`.

### 6.2 Эфир в Telegram
В канале с открытой Live Stream сессией должна пойти живая картинка сцены со звуком
текущего трека. Признак со стороны ноды — стабильный `live` без reconnect.

### 6.3 Диагностика (по желанию, мимо UI)

```sh
# Свежие heartbeat: ожидаем Live без FailureReason
docker compose exec -T postgres psql -U web10 -d web10 -tAc \
  'SELECT "Status", COALESCE("FailureReason",'"'"'-'"'"'), "HeartbeatAtUtc" \
   FROM "StreamNodeHeartbeats" ORDER BY "HeartbeatAtUtc" DESC LIMIT 5;'

# Текущая очередь: ожидаем один Playing (source=playlist)
docker compose exec -T postgres psql -U web10 -d web10 -tAc \
  'SELECT "Status","Source" FROM "PlaybackQueue" WHERE "IsDeleted"=false \
   ORDER BY "CreatedAtUtc" DESC LIMIT 5;'

# RTMP реально публикует?
docker compose logs stream-node --since 2m | grep -iE "Prepared|output-failed|RTMP output failed"
```

`Prepared "…/synthwave/01. Take On Me.mp3"` в логах = Liquidsoap открыл файл и играет.

---

## 7. Траблшутинг

- **`Status` застрял в `starting`, очередь пустая.** Проверьте, что есть хотя бы одна активная policy с подходящим schedule/cadence и доступным cached item; для `Manual` сохраните items, для `AllStorage` дождитесь завершения library scan.

- **`Playback start callback timed out`, треки уходят в `Failed`, в логах `Nonexistent file or ill-formed URI` или ошибки `curl`.** Проверьте, что `TrackFile.IsCached = true` и `CachePath` на стороне API указывает на существующий файл. F# runtime передаёт Liquidsoap `web10media:`-URI, который Liquidsoap скачивает с роута `GET /api/v0/stream-node/playback/{queueItemId}/media` по HTTP (bearer-токен) — общий том с API больше не нужен, но нода должна видеть `WEB10_API__BASE_URL`, а `WEB10_STREAM__CALLBACK_TOKEN` должен совпадать с API.
- После изменения stream-node пересоберите только ноду: `docker compose build stream-node && docker compose up -d --no-deps --force-recreate stream-node`.

- **`Status` уходит в `degraded`/`failed`, `Failure reason: RTMP output failed`.**
  Telegram не принимает публикацию: Live Stream в канале не запущен/закрыт, либо неверный
  `RTMP_URL`/`RTMP_KEY`. Откройте эфир в Telegram, проверьте `.env`, затем в админке
  **Stream-node → Restart** (терминальный `failed` сам не восстанавливается — нужен
  контролируемый рестарт).

- **`/health/ready` отдаёт `503`, но `api` healthy.** Ожидаемо со смоук-бот-токеном:
  проба `getMe` падает. Для полностью зелёного `ready` задайте реальный
  `WEB10_TELEGRAM__BOT_TOKEN`. На вывод эфира не влияет.

- **`stream-node` не стартует, `invalid configuration: WEB10_STREAM__RTMP_KEY`
  (или `…RTMP_URL`).** Ключ короче 16 символов / с пробелами, либо URL не `rtmp(s)`.
  Поправьте `.env`.

- **Права на `./.storage`.** API работает под UID `1000` (как и вы в системе) и на старте
  проверяет запись в `LOCAL_ROOT`, поэтому каталог `./.storage` должен принадлежать вам —
  в репозитории он уже есть (`.gitkeep`). Просто кладите свои аудиофайлы прямо в него.

---

## 8. Остановка и очистка

```sh
# Остановить, сохранив данные (том Postgres остаётся)
docker compose stop

# Полностью снести контейнеры, сеть и тома (чистый старт)
docker compose down --volumes --remove-orphans
```

Не забудьте закрыть Live Stream сессию в Telegram, когда эфир больше не нужен.
