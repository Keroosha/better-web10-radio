# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

Web10.Radio is a 24/7 radio station for the Telegram channel `@netscapedidnothingwrong`, with a Web 1.0 / Aero visual identity (fullscreen 3D scene, retro windows, live overlay widgets). Target v0 is a containerized system: an F# backend owns library scanning, playback program, payments, moderation, metadata, queue state, and stream-node coordination; a React frontend renders the public stage and admin cabinet; a `stream-node` captures the stage + audio and pushes RTMP to Telegram.

## Current state — read before assuming anything exists

The repo is at the **contract/planning stage**. Nothing has been built yet. What exists:

- `docs/SPEC.md` — **the canonical source of truth** for product, architecture, HTTP contracts, event model, DB invariants, and milestones. Read it first for any real work.
- `docs/PLAN-FRONTEND.md` — Claude's implementation checklist for `src/frontend/*` (Milestone FRONTEND).
- `docs/PLAN-BACKEND.md` — ChatGPT/OMP's checklist for `src/backend/*` and `src/stream-node/*` (Milestone BACKEND).
- `src/frontend/web-stage/mocks/` — design handoff bundle (HTML/CSS/JS prototype) for the public stage.

There is **no** `src/backend/`, `src/frontend/admin/`, `src/frontend/shared/`, `src/stream-node/`, `package.json`, `tsconfig`, `.sln`, or Dockerfile yet. Do not reference build/test commands as if they exist — scaffold them per the plan when starting a phase. The docs are written in Russian prose with English contract/checklist terms; keep contract names in English exactly as specified.

## Division of labor

- **Frontend is Claude's milestone.** Backend and stream-node are owned by another agent (ChatGPT/OMP).
- The `/api/v0/*` HTTP contract in `SPEC.md` §5 is the fixed integration boundary between them. Frontend only *consumes* `/api/v0/player/*` and `/api/v0/admin/*`; it never invents route names. Backend must keep these routes stable once frontend work starts.

## Architecture (from SPEC.md §4)

- **Backend is a modular monolith, not microservices.** One deployable ASP.NET host (`Web10.Radio.API`) owns HTTP routes, background workers, config validation, DI, OTEL, health checks.
- `Web10.Radio.Telegram` (Funogram adapter) is an in-process module/hosted service inside the API process, not a separate service in v0.
- `Web10.Radio.Database` owns SQL migrations, ADO.NET repositories, and transaction helpers.
- `src/stream-node/` is a separate container (Xvfb + kiosk Chromium + LiquidSoap + FFmpeg/x11grab → Telegram RTMP) because it needs OS-level deps and process supervision.
- Frontend is a **Bun monorepo** with workspaces `web-stage`, `admin`, `shared`.
- **Side effects are modeled as domain events**, not procedural chains. In-process handling uses F# `MailboxProcessor` agents; durable events go through `OutboxEvents`. Event types and the envelope shape are in SPEC.md §6.

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

## Intended toolchain (when scaffolding)

Not yet present — establish per plan when you start a phase:
- Frontend: **Bun** workspaces; React + Three.js; per-app + workspace scripts for typecheck / build / test.
- Backend: **.NET / F#** (`dotnet` CLI), NUnit integration tests. Integration tests are preferred over unit tests — v0 risk is in contracts, DB concurrency, Telegram payment state, and process boundaries (SPEC.md §12).
- Deploy: Docker containers for API, frontend, stream-node, plus Docker Compose with PostgreSQL.
