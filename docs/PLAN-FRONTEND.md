# Web10.Radio — PLAN-FRONTEND

Этот план предназначен для Claude, чтобы реализовать `src/frontend/*` параллельно с backend work. Он реализует Milestone FRONTEND из `docs/SPEC.md`, потребляет API/domain contracts из `docs/SPEC.md` и использует `src/frontend/web-stage/mocks/project/Web 1.0 Radio Scene.dc.html` как visual/behavioral source для public stage. Backend routes здесь не реализуются: frontend только consumes `/api/v0/player/*` и `/api/v0/admin/*` contracts.

## 1. Контракты и ограничения

- [ ] Read `docs/SPEC.md` sections 5, 10, and 12 before coding.
- [ ] Copy the exact DTO names and enum literals from `SPEC.md` into `src/frontend/shared` domain types.
- [ ] Use `/api/v0/player/state`, `/api/v0/player/events`, `/api/v0/player/stream`, and `/api/v0/admin/*` only; do not invent alternate route names.
- [ ] Keep prototype files under `src/frontend/web-stage/mocks/` as read-only reference assets.
- [ ] Do not copy prototype `support.js` or `image-slot.js` into production runtime.
- [ ] Use TypeScript `.ts`/`.tsx` only; no JavaScript source files.
- [ ] Enforce `strict: true`, `noImplicitAny`, `noUncheckedIndexedAccess`, and `exactOptionalPropertyTypes`.
- [ ] Add linter/type rules that fail on authored `any` and `unknown` in `src/frontend`.

### Phase F0 — Workspace foundation

- [ ] Create Bun workspace root at `src/frontend/package.json` with workspaces `web-stage`, `admin`, and `shared`.
- [ ] Create `src/frontend/tsconfig.base.json` with strict TypeScript settings.
- [ ] Create `src/frontend/shared` for domain types, API client primitives, formatting helpers, and design tokens.
- [ ] Create `src/frontend/web-stage` and `src/frontend/admin` as separate React apps.
- [ ] Configure both apps to import shared domain code only through `src/frontend/shared` public exports.
- [ ] Add scripts for typecheck/build/test at workspace and per-app level.

### Phase F1 — Shared frontend domain and API client

- [ ] Define domain types for `PlayerState`, `StreamState`, `NowPlaying`, `QueueState`, `DonationGoal`, `SuperChatMessage`, `SocialLink`, and `OverlaySettings` using exact fields from `SPEC.md`.
- [ ] Define enum/domain literal types for stream status, queue status, social kind, overlay style, and layout.
- [ ] Implement typed API client functions for `/api/v0/player/state`, `/api/v0/player/events`, `/api/v0/player/stream`, and admin routes.
- [ ] Implement SSE client for `player.state`, `player.queue`, `player.say`, `player.donation`, `player.health`.
- [ ] Implement polling fallback: after two SSE disconnects in 30 seconds, poll `/api/v0/player/state` every 5 seconds.
- [ ] Implement shared formatters for Stars amounts, UTC time, track duration/progress, and fallback `artist — title` strings.

### Phase F2 — web-stage scene port

- [ ] Recreate fullscreen canvas scene from `Web 1.0 Radio Scene.dc.html` in React + Three.js, not by embedding the prototype runtime.
- [ ] Implement loading overlay matching `web1radio.exe` behavior until Three.js scene reports ready.
- [ ] Implement scene elements from the mock: gradient sky, water shader, checker floor, temple, rotating CD jewel case, album texture/nameplate, clouds, lighting.
- [ ] Use React effects only to synchronize with external systems; every effect that starts listeners, animation frames, textures, or renderers must return cleanup.
- [ ] Handle `webglcontextlost` by preventing default and stopping the frame loop.
- [ ] Handle `webglcontextrestored` by rebuilding renderer resources.
- [ ] Cap renderer pixel ratio as in the mock and resize renderer/camera on container changes.
- [ ] Dispose renderer, geometries, materials, textures, event listeners, and animation frame on unmount.
- [ ] Preserve mouse parallax behavior and make it no-op when the stream-node disables pointer input.

### Phase F3 — web-stage overlays and live data

- [ ] Implement NOW PLAYING widget using `nowPlaying.title`, `nowPlaying.artist`, live pill, and equalizer bars.
- [ ] Implement DONATION GOAL widget using Stars amounts, top donator, raised/goal progress, and recent donations.
- [ ] Implement SUPER CHAT widget using approved `/say` messages only.
- [ ] Implement FOLLOW US widget using social links, QR image URL, featured social rotation, glyphs, colors, and handles.
- [ ] Implement donation toast when `player.donation` SSE event arrives.
- [ ] Implement overlay style variants `aero` and `win9x`.
- [ ] Implement layout variants `corners`, `sidebar`, and `bottombar`.
- [ ] Replace mock random timers with API/SSE state. Use empty states when queue, donations, messages, or socials are empty.
- [ ] Play audio from `/api/v0/player/stream`; when stream is `offline|degraded`, show the stream status and keep the visual stage alive.

### Phase F4 — Admin app

- [ ] Scaffold Feature-Sliced Design (FSD) layers for `src/frontend/admin`: `app`, `pages`, `widgets`, `features`, `entities`, `shared` imports only from `src/frontend/shared`.
- [ ] Implement dashboard page with stream status, current track, queue summary, and stream-node heartbeat.
- [ ] Implement social links management page backed by `/api/v0/admin/social-links`.
- [ ] Implement donation goal management page backed by `/api/v0/admin/donation-goal`.
- [ ] Implement playlists page backed by `/api/v0/admin/playlists` and playlist item routes.
- [ ] Implement storage settings page for `Local` and `S3` values.
- [ ] Implement `/say` moderation queue with approve/reject actions.
- [ ] Implement library scan trigger backed by `/api/v0/admin/library/scan`.
- [ ] Add auth guard placeholder that consumes backend admin auth result; do not invent OAuth/provider UX in this milestone.

### Phase F5 — Frontend verification and handoff

- [ ] Run strict TypeScript check for all frontend workspaces.
- [ ] Run frontend tests for API client fallback, SSE event handling, empty states, and formatter edge cases.
- [ ] Run a visual/manual checklist for both themes and all three layouts.
- [ ] Verify authored source contains no `.js`, no `any`, and no `unknown`.
- [ ] Verify scene unmount cleanup by mounting/unmounting the stage in a test harness or development page.
- [ ] Verify web-stage still renders when `/api/v0/player/state` returns empty arrays and stream status `offline`.
