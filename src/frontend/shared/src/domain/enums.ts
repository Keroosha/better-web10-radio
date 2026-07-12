// Domain enums for the Web10.Radio player contract.
//
// Every literal here is copied VERBATIM from docs/SPEC.md §5. Do not rename,
// reorder for meaning, or "tidy" the strings — they are the wire contract with
// the backend. Types are inferred from the Zod schemas so the schema stays the
// single source of truth (SPEC §10: named domain types, no untyped payloads).
import { z } from 'zod';

/** `stream.status` — SPEC §5. */
export const StreamStatusSchema = z.enum(['offline', 'starting', 'live', 'degraded']);
export type StreamStatus = z.infer<typeof StreamStatusSchema>;

/** `nowPlaying.source` — SPEC §5. Distinct from {@link QueueItemSourceSchema}. */
export const NowPlayingSourceSchema = z.enum(['library', 'request', 'fallback']);
export type NowPlayingSource = z.infer<typeof NowPlayingSourceSchema>;

/**
 * `queue.items[].source` — SPEC §5. Distinct from {@link NowPlayingSourceSchema}.
 * `fallback` is included per SPEC §5: the queue exposes the fallback source in the
 * model even though LiquidSoap wires its own fallback locally (contract requirement).
 */
export const QueueItemSourceSchema = z.enum(['playlist', 'request', 'admin', 'fallback']);
export type QueueItemSource = z.infer<typeof QueueItemSourceSchema>;

/** `queue.items[].status` — SPEC §5. */
export const QueueItemStatusSchema = z.enum([
  'queued',
  'claimed',
  'playing',
  'played',
  'failed',
]);
export type QueueItemStatus = z.infer<typeof QueueItemStatusSchema>;

/**
 * `superChat.messages[].status` — SPEC §5 shows `approved`; the admin route
 * `/api/v0/admin/say-messages?status=pending|approved|rejected` (SPEC §5)
 * enumerates the full set.
 */
export const SuperChatStatusSchema = z.enum(['pending', 'approved', 'rejected']);
export type SuperChatStatus = z.infer<typeof SuperChatStatusSchema>;

/** `socials[].kind` — SPEC §5. */
export const SocialKindSchema = z.enum([
  'telegram',
  'youtube',
  'instagram',
  'discord',
  'external',
]);
export type SocialKind = z.infer<typeof SocialKindSchema>;

/** `overlay.style` — SPEC §5. */
export const OverlayStyleSchema = z.enum(['aero', 'win9x']);
export type OverlayStyle = z.infer<typeof OverlayStyleSchema>;

/** `overlay.layout` — SPEC §5. */
export const OverlayLayoutSchema = z.enum(['corners', 'sidebar', 'bottombar']);
export type OverlayLayout = z.infer<typeof OverlayLayoutSchema>;

/** `banners[].type` — the overlay banner kind rendered on the stage. */
export const BannerTypeSchema = z.enum(['nowplaying', 'donation', 'social', 'custom']);
export type BannerType = z.infer<typeof BannerTypeSchema>;

/** `banners[].style` — banner chrome theme; mirrors {@link OverlayStyleSchema}. */
export const BannerStyleSchema = z.enum(['aero', 'win9x']);
export type BannerStyle = z.infer<typeof BannerStyleSchema>;

/** `banners[].screenPosition` — where the banner renders over the 3D scene. */
export const BannerPositionSchema = z.enum([
  'top-left',
  'top-center',
  'top-right',
  'bottom-left',
  'bottom-center',
  'bottom-right',
]);
export type BannerPosition = z.infer<typeof BannerPositionSchema>;

/** `playbackState` — the desired playback transport state surfaced on the player snapshot. */
export const PlaybackStateSchema = z.enum(['playing', 'paused', 'stopped']);
export type PlaybackState = z.infer<typeof PlaybackStateSchema>;
