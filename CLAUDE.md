# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

Web10.Radio is a 24/7 radio station for the Telegram channel `@netscapedidnothingwrong`, with a Web 1.0 / Aero visual identity (fullscreen 3D scene, retro windows, live overlay widgets). Target v0 is a containerized system: an F# backend owns library scanning, playback program, payments, moderation, metadata, queue state, and stream-node coordination; a React frontend renders the public stage and admin cabinet; a `stream-node` captures the stage + audio and pushes RTMP to Telegram.

## Current state — read before assuming anything exists

The repository has the backend, frontend, stream-node, migrations, Dockerfiles, and Compose runtime implemented. The canonical source of truth remains `docs/SPEC.md`; implementation checklists are `docs/PLAN-FRONTEND.md` and `docs/PLAN-BACKEND.md`.

- `src/backend/` contains independent F# API, Application, Database, Migrator, Telegram, and NUnit/Testcontainers projects.
- `src/frontend/` contains Bun workspaces `shared`, `web-stage`, and `admin`.
- `src/stream-node/` contains the F# runtime, typed smoke/control Tools project, LiquidSoap script, and Debian container packaging.
- `compose.yaml` runs PostgreSQL, migrator, API, standalone Telegram, frontend, RTMP sink, and stream-node.
- `docs/getting_started.md` and `README.md` contain executable local configuration and smoke commands. Do not describe the repository as an unscaffolded planning stage.

## Division of labor

- Frontend and backend are both implemented; preserve the `/api/v0/*` integration boundary. Frontend consumes `/api/v0/player/*` and `/api/v0/admin/*`; Telegram routes are served by the standalone Telegram process and exposed through the reverse proxy.

## Architecture (from SPEC.md §4)

- **Backend has separate deployables.** `Web10.Radio.API` owns player/admin/library/playback/stream-node HTTP routes and API workers; `Web10.Radio.Telegram` is a standalone Funogram service owning Telegram ingress, Stars workflows, and its Telegram outbox relay.
- `Web10.Radio.Application` is the shared event/relay/health kernel; `Web10.Radio.Database` owns SQL migrations, ADO.NET repositories, and transaction helpers.
- `src/stream-node/` is a separate F# container (Xvfb + kiosk Chromium + LiquidSoap + FFmpeg/x11grab → Telegram RTMP) because it needs OS-level deps and process supervision.
- Frontend is a **Bun monorepo** with workspaces `web-stage`, `admin`, `shared`.
- **Durable side effects are audience-partitioned domain events.** API and Telegram relays claim only their audience from `OutboxEvents`; there is no API-hosted Telegram worker.

## Non-negotiable invariants

These are enforced constraints from the SPEC, not preferences. Violating them is a bug.

**Frontend (SPEC.md §10, PLAN-FRONTEND.md):**
- TypeScript `.ts`/`.tsx` only — **no `.js` source files**.
- `strict: true`, plus `noImplicitAny`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`. **No `any`, no `unknown`, no untyped API payloads, no type assertions that erase domain types.** Add lint rules that fail the build on authored `any`/`unknown`.
- All domain types/DTOs live in `src/frontend/shared`, copied verbatim (field + enum-literal names) from SPEC.md. `shared` imports no project domains.
- **Feature-Sliced Design** layers only: `app`, `pages`, `widgets`, `features`, `entities`, `shared`. Imports flow high→low only. Do **not** use the deprecated `processes` layer.
- Three.js scene must be **recreated** in React, not embedded. Every effect that starts listeners/animation frames/textures/renderers must return cleanup; handle `webglcontextlost`/`webglcontextrestored`; dispose renderer/geometries/materials/textures on unmount.
- SSE client consumes `/api/v0/player/events`; **fallback**: after two SSE disconnects within 30s, poll `/api/v0/player/state` every 5s. Stage must still render when state returns empty arrays and stream status `offline`.

**Backend (SPEC.md §8–9, PLAN-BACKEND.md):**
- F# for all backend projects. Telegram stays on **Funogram**.
- **ADO.NET only** — no ORM, no EF Core, no Dapper, no object mapper.
- **Soft delete only**: every mutable table has `IsDeleted`, `CreatedAtUtc`, `UpdatedAtUtc`. Application code never issues `DELETE` on domain data — deletion is `UPDATE ... SET IsDeleted = true`. Reads filter `WHERE "IsDeleted" = false` (except explicit admin audit reads).
- Queue/work claiming uses `SELECT ... FOR UPDATE SKIP LOCKED` (exact pattern in SPEC.md §8).
- Domain IDs are RFC9562 **UUIDv7** via Dodo.Primitives `Uuid`, stored as PostgreSQL `uuid`.
- **Fail fast at startup** on missing/invalid config; parse URLs/config at startup, not first request. All config keys are `WEB10_*` (SPEC.md §9). Telegram token and RTMP key are config/Docker secrets, not DB rows.

**Payments (Telegram Stars, SPEC.md §7):** v0 uses Stars only (currency `XTR`, `provider_token = ""`). Amounts are stored as **integer Stars, not cents**. Answer `pre_checkout_query` within 10s; deliver paid effects **only after** `successful_payment`; store `telegram_payment_charge_id` for refunds. USDT/card are roadmap-only — do not implement in v0. Dedupe Telegram updates by `(telegramUpdateId, eventType)`.

## The design mock

`src/frontend/web-stage/mocks/project/Web 1.0 Radio Scene.dc.html` is the pixel-perfect visual/behavioral reference for the public stage (Three.js r128, gradient sky, water shader, checker floor, temple, rotating CD jewel case, overlay widgets: NOW PLAYING / DONATION GOAL / SUPER CHAT / FOLLOW US, donation toast, `aero`/`win9x` themes, `corners`/`sidebar`/`bottombar` layouts).

- Treat mock files as **read-only reference assets**. Recreate the visual output; do not copy the prototype's internal structure.
- **Do not** port `support.js` or `image-slot.js` into production runtime — they are Claude Design's `<x-dc>`/`<sc-if>`/`<x-import>` template runtime, not app code.
- Replace the mock's random timers with real API/SSE state.

## Implemented toolchain

- Frontend: **Bun** workspaces; React + Three.js; per-app + workspace scripts for typecheck / build / test.
- Backend: **.NET / F#** (`dotnet` CLI), NUnit/Testcontainers integration tests. Integration tests are preferred over unit tests — v0 risk is in contracts, DB concurrency, Telegram payment state, and process boundaries (SPEC.md §12).
- Stream-node: F# runtime and Tools project in a Debian container with Xvfb, Chromium, LiquidSoap, FFmpeg/x11grab, and RTMP.
- Deploy: Docker containers for API, standalone Telegram, frontend, stream-node, plus Docker Compose with PostgreSQL and migrator.
