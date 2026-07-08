// Public player endpoints (SPEC §5, `/api/v0/player/*`). Read-only, unauthenticated.
import { API_V0_PREFIX, apiFetch, getApiBaseUrl, type RequestOptions } from './client';
import { PlayerStateSchema, type PlayerState } from '../domain/player-state';

/** `GET /api/v0/player/state` — full stage snapshot, validated. */
export function getPlayerState(opts: RequestOptions = {}): Promise<PlayerState> {
  return apiFetch(`${API_V0_PREFIX}/player/state`, { schema: PlayerStateSchema, ...opts });
}

/**
 * Absolute URL of the public audio stream (`GET /api/v0/player/stream`) for use as
 * an `<audio>` source. The endpoint itself returns `503` + problem details when the
 * stream is unavailable; that is handled by the media element, not fetched here.
 *
 * Note: `/api/v0/player/song` and `/api/v0/player/health` are intentionally omitted
 * — their response shapes are not defined in SPEC §5 yet.
 */
export function playerStreamUrl(): string {
  return `${getApiBaseUrl()}${API_V0_PREFIX}/player/stream`;
}
