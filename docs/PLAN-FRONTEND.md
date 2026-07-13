# Web10.Radio — PLAN-FRONTEND

Этот план предназначен для Claude, чтобы реализовать `src/frontend/*` параллельно с backend work. Он реализует Milestone FRONTEND из `docs/SPEC.md`, потребляет API/domain contracts из `docs/SPEC.md` и использует `src/frontend/web-stage/mocks/project/Web 1.0 Radio Scene.dc.html` как visual/behavioral source для public stage. Backend routes здесь не реализуются: frontend только consumes `/api/v0/player/*` и `/api/v0/admin/*` contracts.

## 1. Контракты и ограничения

- [x] Read `docs/SPEC.md` sections 5, 10, and 12 before coding.
- [x] Copy the exact DTO names and enum literals from `SPEC.md` into `src/frontend/shared` domain types.
- [x] Use `/api/v0/player/state`, `/api/v0/player/events`, `/api/v0/player/stream`, and `/api/v0/admin/*` only; do not invent alternate route names.
- [x] Keep prototype files under `src/frontend/web-stage/mocks/` as read-only reference assets.
- [x] Do not copy prototype `support.js` or `image-slot.js` into production runtime.
- [x] Use TypeScript `.ts`/`.tsx` only; no JavaScript source files.
- [x] Enforce `strict: true`, `noImplicitAny`, `noUncheckedIndexedAccess`, and `exactOptionalPropertyTypes`.
- [x] Add linter/type rules that fail on authored `any` and `unknown` in `src/frontend`.

### Phase F0 — Workspace foundation

- [x] Create Bun workspace root at `src/frontend/package.json` with workspaces `web-stage`, `admin`, and `shared`.
- [x] Create `src/frontend/tsconfig.base.json` with strict TypeScript settings.
- [x] Create `src/frontend/shared` for domain types, API client primitives, formatting helpers, and design tokens.
- [x] Create `src/frontend/web-stage` and `src/frontend/admin` as separate React apps.
- [x] Configure both apps to import shared domain code only through `src/frontend/shared` public exports.
- [x] Add scripts for typecheck/build/test at workspace and per-app level.

### Phase F1 — Shared frontend domain and API client

> **Admin scope note:** all admin DTOs and write contracts used by the current cabinet are now pinned in `docs/SPEC.md` and implemented by the backend. Payload validation uses Zod (`4.4.3`); domain types are inferred from the schemas.

- [x] Define domain types for `PlayerState`, `StreamState`, `NowPlaying`, `QueueState`, `DonationGoal`, `SuperChatMessage`, `SocialLink`, and `OverlaySettings` using exact fields from `SPEC.md`.
- [x] Define enum/domain literal types for stream status, queue status, social kind, overlay style, and layout.
- [x] Implement typed API client functions for `/api/v0/player/state`, `/api/v0/player/events`, `/api/v0/player/stream`, and admin routes.
- [x] Implement SSE client for `player.state`, `player.queue`, `player.say`, `player.donation`, `player.health`.
- [x] Implement polling fallback: after two SSE disconnects in 30 seconds, poll `/api/v0/player/state` every 5 seconds.
- [x] Implement shared formatters for Stars amounts, UTC time, track duration/progress, and fallback `artist — title` strings.

### Phase F2 — web-stage scene port

- [x] Recreate fullscreen canvas scene from `Web 1.0 Radio Scene.dc.html` in React + Three.js, not by embedding the prototype runtime.
- [x] Implement loading overlay matching `web1radio.exe` behavior until Three.js scene reports ready.
- [x] Implement scene elements from the mock: gradient sky, water shader, checker floor, temple, rotating CD jewel case, album texture/nameplate, clouds, lighting.
- [x] Use React effects only to synchronize with external systems; every effect that starts listeners, animation frames, textures, or renderers must return cleanup.
- [x] Handle `webglcontextlost` by preventing default and stopping the frame loop.
- [x] Handle `webglcontextrestored` by rebuilding renderer resources.
- [x] Cap renderer pixel ratio as in the mock and resize renderer/camera on container changes.
- [x] Dispose renderer, geometries, materials, textures, event listeners, and animation frame on unmount.
- [x] Preserve mouse parallax behavior and make it no-op when the stream-node disables pointer input (via the `pointerEnabled` seam; wiring to the stream-node's real signal lands with stream-node integration).

### Phase F3 — web-stage overlays and live data ✅ (2026-07-10)

> Live data flows through `entities/player-state` (empty default, reducer, selectors) →
> `features/stage-state/useStageState` (seed + SSE deltas + 2-disconnect→poll fallback +
> donation-toast signal). Verified against the running B3 backend: the real
> `/api/v0/player/state` validates through the Zod schemas with zero drift.

- [x] Implement NOW PLAYING widget using `nowPlaying.title`, `nowPlaying.artist`, live pill, and equalizer bars.
- [x] Implement DONATION GOAL widget using Stars amounts, top donator, raised/goal progress, and recent donations.
- [x] Implement SUPER CHAT widget using approved `/say` messages only.
- [x] Implement FOLLOW US widget using social links, QR image URL, featured social rotation, glyphs, colors, and handles.
- [x] Implement donation toast when `player.donation` SSE event arrives.
- [x] Implement overlay style variants `aero` and `win9x`.
- [x] Implement layout variants `corners`, `sidebar`, and `bottombar`.
- [x] Replace mock random timers with API/SSE state. Use empty states when queue, donations, messages, or socials are empty.
- [x] Play audio from `/api/v0/player/stream`; when stream is `offline|degraded`, show the stream status and keep the visual stage alive.
      Capture mode (`?capture=1` / `captureEnabled` prop) mutes/hides browser audio for the stream-node kiosk; viewer mode uses muted-autoplay + an unmute pill.

### Phase F4 — Admin app (playback, metadata, policies, storage, and moderation landed 2026-07-11)

> The FSD shell, auth guard, playback controls, metadata/artwork controls, playlist policy editor, storage settings, library scan, and `/say` moderation are backed by the real API routes. The shared client carries cookie/CSRF auth, JSON bodies, and `204 No Content` responses via `apiSend`.

- [x] Scaffold Feature-Sliced Design (FSD) layers for `src/frontend/admin`: `app`, `pages`, `widgets`, `features`, `entities`, `shared` imports only from `src/frontend/shared`.
- [x] Implement dashboard page with stream status, current track, queue summary, and stream-node heartbeat.
- [x] Implement social links management page backed by `/api/v0/admin/social-links`, including replacement writes.
- [x] Implement donation goal management page backed by `/api/v0/admin/donation-goal`, including update writes.
- [x] Implement playlists page backed by `/api/v0/admin/playlists` and playlist item routes, including policy/schedule editing and system All tracks handling.
- [x] Implement storage settings page for `Local` and `S3` values.
- [x] Implement `/say` moderation queue with approve/reject actions. ✅ (2026-07-10) — `SayModerationPage` (status tabs + approve `{}` / reject `{reason}` 1–500 chars), backed by the pinned SPEC §5 contract and the real backend routes; `AdminSayMessageDto` + `getSayMessages`/`approveSayMessage`/`rejectSayMessage` in `shared`.
- [x] Implement library scan trigger backed by `/api/v0/admin/library/scan`.
- [x] Add auth guard placeholder that consumes backend admin auth result; do not invent OAuth/provider UX in this milestone.
- [x] Add the Storage File Manager under `features/storage-file-manager`: typed folder/file listing, breadcrumbs, safe media/text previews, authenticated downloads, create-only streamed file and directory uploads, scan polling, recursive impact preview, CSRF confirmation, stale-token retry, abort cleanup, and empty/offline states.
- [x] Verify Storage File Manager against SeaweedFS in visible Chrome DevTools: upload and folder-relative navigation, scan terminal `completed`, inline text/range read, attachment download, recursive folder preview/delete, and no-overwrite conflict.

### Phase F5 — Frontend verification and handoff (2026-07-10)

- [x] Run strict TypeScript check for all frontend workspaces. — `bun run typecheck` → all three workspaces exit 0.
- [x] Run frontend tests for API client fallback, SSE event handling, empty states, and formatter edge cases. — `bun run test` → 144 passing (shared 80, web-stage 60, admin 4), including playback controls, stage cleanup, storage contracts, and API clients.
- [ ] Run a visual/manual checklist for both themes and all three layouts. — **manual step remaining:** `bun run --filter web-stage dev`, then open `?overlayStyle=aero|win9x` × `?overlayLayout=corners|sidebar|bottombar` (dev QA params on `StagePage`).
- [x] Verify authored source contains no `.js`, no `any`, and no `unknown`. — `bun run lint` clean (ESLint bans `TSAnyKeyword`/`TSUnknownKeyword`/`no-explicit-any`) + no `.js`/`.jsx` authored sources found.
- [x] Verify scene unmount cleanup by mounting/unmounting the stage in a test harness or development page. — covered by `StageScene.test.tsx` ("unmounting disposes the scene exactly once" + StrictMode build/dispose parity).
- [x] Verify web-stage still renders when `/api/v0/player/state` returns empty arrays and stream status `offline`. — covered by `StagePage.test.tsx` ("renders from the empty/offline default (stage stays alive)").
