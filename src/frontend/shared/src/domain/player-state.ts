// Player-state DTOs for the Web10.Radio public stage.
//
// Shapes and field names are copied VERBATIM from the `GET /api/v0/player/state`
// example in docs/SPEC.md §5. The Zod schemas are the single source of truth;
// domain types are inferred via `z.infer` (SPEC §10: no untyped payloads, no
// domain-erasing casts).
//
// Nullability policy — we mark `.nullable()` in exactly two places, both justified:
//   • `stream.offlineReason`  — SPEC shows `null` in the example.
//   • `donationGoal.topDonator` — no top donator exists before the first donation;
//     the empty-state invariant (SPEC §10/§12: "render with empty arrays") requires
//     representing "no donations yet".
// Every other field is modeled exactly as SPEC presents it. URLs use `z.string()`
// (not `z.url()`) because several are relative paths (e.g. "/api/v0/player/stream").
// Timestamps are `z.string()` (ISO-8601 `…Z`); formatters guard malformed input.
import { z } from 'zod';

import {
  BannerPositionSchema,
  BannerStyleSchema,
  BannerTypeSchema,
  NowPlayingSourceSchema,
  OverlayLayoutSchema,
  OverlayStyleSchema,
  PlaybackStateSchema,
  QueueItemSourceSchema,
  QueueItemStatusSchema,
  SocialKindSchema,
  StreamStatusSchema,
  SuperChatStatusSchema,
} from './enums';

/** `state.stream` — SPEC §5. */
export const StreamStateSchema = z.object({
  status: StreamStatusSchema,
  publicAudioUrl: z.string(),
  rtmpRelay: z.string(),
  bitrateKbps: z.number().int(),
  startedAtUtc: z.string(),
  offlineReason: z.string().nullable(),
});
export type StreamState = z.infer<typeof StreamStateSchema>;

/** `state.nowPlaying` — SPEC §5. */
export const NowPlayingSchema = z.object({
  trackId: z.string(),
  title: z.string(),
  artist: z.string(),
  album: z.string(),
  source: NowPlayingSourceSchema,
  externalUrl: z.string(),
  coverImageUrl: z.string(),
  durationMs: z.number().int(),
  positionMs: z.number().int(),
  startedAtUtc: z.string(),
});
export type NowPlaying = z.infer<typeof NowPlayingSchema>;

/** `state.queue.items[]` — SPEC §5. */
export const QueueItemSchema = z.object({
  queueItemId: z.string(),
  trackId: z.string(),
  title: z.string(),
  artist: z.string(),
  source: QueueItemSourceSchema,
  status: QueueItemStatusSchema,
});
export type QueueItem = z.infer<typeof QueueItemSchema>;

/** `state.queue` — SPEC §5. Carried by the `player.queue` SSE event. */
export const QueueStateSchema = z.object({
  currentQueueItemId: z.string(),
  items: z.array(QueueItemSchema),
});
export type QueueState = z.infer<typeof QueueStateSchema>;

/** `state.donationGoal.topDonator` — SPEC §5. */
export const TopDonatorSchema = z.object({
  displayName: z.string(),
  amountStars: z.number().int(),
});
export type TopDonator = z.infer<typeof TopDonatorSchema>;

/** `state.donationGoal.recent[]` — SPEC §5. */
export const RecentDonationSchema = z.object({
  id: z.string(),
  displayName: z.string(),
  amountStars: z.number().int(),
  paidAtUtc: z.string(),
});
export type RecentDonation = z.infer<typeof RecentDonationSchema>;

/** `state.donationGoal` — SPEC §5. Carried by the `player.donation` SSE event. */
export const DonationGoalSchema = z.object({
  title: z.string(),
  raisedStars: z.number().int(),
  goalStars: z.number().int(),
  topDonator: TopDonatorSchema.nullable(),
  recent: z.array(RecentDonationSchema),
});
export type DonationGoal = z.infer<typeof DonationGoalSchema>;

/** `state.superChat.messages[]` — SPEC §5. */
export const SuperChatMessageSchema = z.object({
  id: z.string(),
  displayName: z.string(),
  text: z.string(),
  amountStars: z.number().int(),
  color: z.string(),
  submittedAtUtc: z.string(),
  status: SuperChatStatusSchema,
});
export type SuperChatMessage = z.infer<typeof SuperChatMessageSchema>;

/** `state.superChat` — SPEC §5. Carried by the `player.say` SSE event. */
export const SuperChatStateSchema = z.object({
  messages: z.array(SuperChatMessageSchema),
});
export type SuperChatState = z.infer<typeof SuperChatStateSchema>;

/** `state.socials[]` — SPEC §5. */
export const SocialLinkSchema = z.object({
  id: z.string(),
  kind: SocialKindSchema,
  name: z.string(),
  handle: z.string(),
  url: z.string(),
  glyph: z.string(),
  color: z.string(),
  qrImageUrl: z.string(),
  isFeatured: z.boolean(),
});
export type SocialLink = z.infer<typeof SocialLinkSchema>;

/** `state.overlay` — SPEC §5. */
export const OverlaySettingsSchema = z.object({
  style: OverlayStyleSchema,
  layout: OverlayLayoutSchema,
});
export type OverlaySettings = z.infer<typeof OverlaySettingsSchema>;

/** `state.banners[]` — admin-managed overlay banners rendered on the stage. */
export const BannerSchema = z.object({
  id: z.string(),
  type: BannerTypeSchema,
  title: z.string(),
  subtitle: z.string(),
  text: z.string(),
  style: BannerStyleSchema,
  screenPosition: BannerPositionSchema,
  accent: z.string(),
  enabled: z.boolean(),
  sortOrder: z.number().int(),
  rotationSeconds: z.number().int(),
});
export type Banner = z.infer<typeof BannerSchema>;

/**
 * `GET /api/v0/player/state` full snapshot — SPEC §5.
 * Also the payload of the `player.state` SSE event.
 */
export const PlayerStateSchema = z.object({
  serverTimeUtc: z.string(),
  stream: StreamStateSchema,
  nowPlaying: NowPlayingSchema,
  queue: QueueStateSchema,
  donationGoal: DonationGoalSchema,
  superChat: SuperChatStateSchema,
  socials: z.array(SocialLinkSchema),
  overlay: OverlaySettingsSchema,
  banners: z.array(BannerSchema),
  playbackState: PlaybackStateSchema,
});
export type PlayerState = z.infer<typeof PlayerStateSchema>;
