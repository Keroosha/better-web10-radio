// Admin cabinet DTOs for the Web10.Radio `/api/v0/admin/*` routes.
//
// Each schema below represents the camelCase wire shape pinned by SPEC §5/§6.
// Zod is the single source of truth: public TypeScript types are inferred with
// `z.infer`, never asserted from unvalidated JSON.
import { z } from 'zod';

import { SocialKindSchema, StreamStatusSchema, SuperChatStatusSchema } from './enums';

const UuidSchema = z.string();
const NonNegativeIntegerSchema = z.number().int().nonnegative();

export const AdminSessionSchema = z.strictObject({
  username: z.string(),
  csrfToken: z.string(),
  developmentFixturesEnabled: z.boolean(),
});
export type AdminSession = z.infer<typeof AdminSessionSchema>;

export const LoginAdminRequestSchema = z.strictObject({
  username: z.string(),
  password: z.string(),
});
export type LoginAdminRequest = z.infer<typeof LoginAdminRequestSchema>;

export const EmptyAdminRequestSchema = z.strictObject({});
export type EmptyAdminRequest = z.infer<typeof EmptyAdminRequestSchema>;

export const LibraryScanRequestSchema = z.strictObject({
  storageBackendId: UuidSchema.optional(),
});
export type LibraryScanRequest = z.infer<typeof LibraryScanRequestSchema>;

export const LibraryScanAcceptedSchema = z.strictObject({
  scanJobId: UuidSchema,
});
export type LibraryScanAccepted = z.infer<typeof LibraryScanAcceptedSchema>;

export const LibraryScanStatusSchema = z.strictObject({
  scanJobId: UuidSchema,
  status: z.enum(['queued', 'running', 'completed', 'failed']),
  discoveredCount: NonNegativeIntegerSchema,
  requestedAtUtc: z.string(),
  startedAtUtc: z.string().nullable(),
  finishedAtUtc: z.string().nullable(),
  failureReason: z.string().nullable(),
});
export type LibraryScanStatus = z.infer<typeof LibraryScanStatusSchema>;

export const AdminTrackSchema = z.strictObject({
  id: UuidSchema,
  title: z.string(),
  artist: z.string(),
  album: z.string(),
  durationMs: NonNegativeIntegerSchema,
  hasCachedFile: z.boolean(),
});
export type AdminTrack = z.infer<typeof AdminTrackSchema>;

export const QueueTrackRequestSchema = z.strictObject({
  trackId: UuidSchema,
});
export type QueueTrackRequest = z.infer<typeof QueueTrackRequestSchema>;

export const QueueTrackAcceptedSchema = z.strictObject({
  queueItemId: UuidSchema,
});
export type QueueTrackAccepted = z.infer<typeof QueueTrackAcceptedSchema>;

export const DonationGoalUpdateRequestSchema = z.strictObject({
  title: z.string(),
  goalStars: z.number().int(),
});
export type DonationGoalUpdateRequest = z.infer<typeof DonationGoalUpdateRequestSchema>;

export const SocialLinkReplaceItemSchema = z.strictObject({
  id: UuidSchema.nullable(),
  kind: SocialKindSchema,
  name: z.string(),
  handle: z.string().nullable(),
  url: z.string(),
  glyph: z.string().nullable(),
  color: z.string().nullable(),
  qrImageUrl: z.string().nullable(),
  isFeatured: z.boolean(),
});
export type SocialLinkReplaceItem = z.infer<typeof SocialLinkReplaceItemSchema>;

export const SocialLinksReplaceRequestSchema = z.array(SocialLinkReplaceItemSchema);
export type SocialLinksReplaceRequest = z.infer<typeof SocialLinksReplaceRequestSchema>;

export const PlaylistSchema = z.strictObject({
  id: UuidSchema,
  name: z.string(),
  description: z.string().nullable(),
  isActive: z.boolean(),
  itemCount: NonNegativeIntegerSchema,
});
export type Playlist = z.infer<typeof PlaylistSchema>;

export const PlaylistMutationRequestSchema = z.strictObject({
  name: z.string(),
  description: z.string().nullable(),
  isActive: z.boolean(),
});
export type PlaylistMutationRequest = z.infer<typeof PlaylistMutationRequestSchema>;

export const PlaylistItemSchema = z.strictObject({
  id: UuidSchema,
  trackId: UuidSchema,
  title: z.string(),
  artist: z.string(),
  position: NonNegativeIntegerSchema,
});
export type PlaylistItem = z.infer<typeof PlaylistItemSchema>;

export const PlaylistItemCreateRequestSchema = z.strictObject({
  trackId: UuidSchema,
});
export type PlaylistItemCreateRequest = z.infer<typeof PlaylistItemCreateRequestSchema>;

export const PlaylistItemReplaceRowSchema = z.strictObject({
  id: UuidSchema.nullable(),
  trackId: UuidSchema,
});
export type PlaylistItemReplaceRow = z.infer<typeof PlaylistItemReplaceRowSchema>;

export const PlaylistItemsReplaceRequestSchema = z.strictObject({
  items: z.array(PlaylistItemReplaceRowSchema),
});
export type PlaylistItemsReplaceRequest = z.infer<typeof PlaylistItemsReplaceRequestSchema>;

export const StorageDefaultBackendSchema = z.strictObject({
  type: z.enum(['local', 's3']),
  localRoot: z.string().nullable(),
  s3Bucket: z.string().nullable(),
  s3Region: z.string().nullable(),
  s3ServiceUrl: z.string().nullable(),
  s3ForcePathStyle: z.boolean(),
});
export type StorageDefaultBackend = z.infer<typeof StorageDefaultBackendSchema>;

export const StorageAdditionalBackendSchema = z.strictObject({
  id: UuidSchema,
  name: z.string(),
  type: z.enum(['local', 's3']),
  localRoot: z.string().nullable(),
  s3Bucket: z.string().nullable(),
  isEnabled: z.boolean(),
});
export type StorageAdditionalBackend = z.infer<typeof StorageAdditionalBackendSchema>;

export const StorageSchema = z.strictObject({
  defaultBackend: StorageDefaultBackendSchema,
  additionalBackends: z.array(StorageAdditionalBackendSchema),
});
export type Storage = z.infer<typeof StorageSchema>;

export const StorageAdditionalBackendReplaceSchema = z.strictObject({
  id: UuidSchema.nullable(),
  name: z.string(),
  type: z.enum(['local', 's3']),
  localRoot: z.string().nullable(),
  s3Bucket: z.string().nullable(),
  isEnabled: z.boolean(),
});
export type StorageAdditionalBackendReplace = z.infer<typeof StorageAdditionalBackendReplaceSchema>;

export const StorageReplaceRequestSchema = z.strictObject({
  additionalBackends: z.array(StorageAdditionalBackendReplaceSchema),
});
export type StorageReplaceRequest = z.infer<typeof StorageReplaceRequestSchema>;

export const StreamNodeControlSchema = z.strictObject({
  desiredState: z.enum(['running', 'stopped']),
  restartGeneration: NonNegativeIntegerSchema,
});
export type StreamNodeControl = z.infer<typeof StreamNodeControlSchema>;

export const StreamNodeStatusSchema = z.strictObject({
  status: StreamStatusSchema,
  desiredState: z.enum(['running', 'stopped']),
  lastHeartbeatUtc: z.string().nullable(),
  failureReason: z.string().nullable(),
  bitrateKbps: NonNegativeIntegerSchema,
  restartGeneration: NonNegativeIntegerSchema,
});
export type StreamNodeStatus = z.infer<typeof StreamNodeStatusSchema>;

export const PaidVerticalSliceFixtureRequestSchema = z.strictObject({
  fixtureKey: z.string(),
});
export type PaidVerticalSliceFixtureRequest = z.infer<typeof PaidVerticalSliceFixtureRequestSchema>;

export const PaidVerticalSliceFixtureSchema = z.strictObject({
  donationPaymentId: UuidSchema,
  sayPaymentId: UuidSchema,
  sayMessageId: UuidSchema,
});
export type PaidVerticalSliceFixture = z.infer<typeof PaidVerticalSliceFixtureSchema>;

/** `AdminSayMessageDto` — paid `/say` moderation row. */
export const AdminSayMessageSchema = z.strictObject({
  id: UuidSchema,
  telegramUserId: z.number().nullable(),
  displayName: z.string(),
  text: z.string(),
  amountStars: z.number().int(),
  color: z.string().nullable(),
  status: SuperChatStatusSchema,
  submittedAtUtc: z.string().nullable(),
  paidAtUtc: z.string().nullable(),
  moderatedAtUtc: z.string().nullable(),
  moderationReason: z.string().nullable(),
});
export type AdminSayMessage = z.infer<typeof AdminSayMessageSchema>;
