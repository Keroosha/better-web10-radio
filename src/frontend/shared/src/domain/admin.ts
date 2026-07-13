// Admin cabinet DTOs for the Web10.Radio `/api/v0/admin/*` routes.
//
// Each schema below represents the camelCase wire shape pinned by SPEC §5/§6.
// Zod is the single source of truth: public TypeScript types are inferred with
// `z.infer`, never asserted from unvalidated JSON.
import { z } from 'zod';

import {
  BannerPositionSchema,
  BannerStyleSchema,
  BannerTypeSchema,
  SocialKindSchema,
  StreamStatusSchema,
} from './enums';

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
  coverImageUrl: z.string(),
  metadataSource: z.enum(['filename', 'embedded', 'manual']),
  storageBackendId: z.string(),
});
export type AdminTrack = z.infer<typeof AdminTrackSchema>;
export const AdminTrackPageSchema = z.strictObject({
  items: z.array(AdminTrackSchema),
  nextCursor: z.string().max(512).nullable(),
});
export type AdminTrackPage = z.infer<typeof AdminTrackPageSchema>;


export const TrackMetadataUpdateRequestSchema = z.strictObject({
  title: z.string().min(1).max(200).refine((value) => value.trim().length > 0),
  artist: z.string().min(1).max(200).refine((value) => value.trim().length > 0),
  album: z.string().min(1).max(200).nullable().refine((value) => value === null || value.trim().length > 0),
});
export type TrackMetadataUpdateRequest = z.infer<typeof TrackMetadataUpdateRequestSchema>;

export const QueueTrackRequestSchema = z.strictObject({
  trackId: UuidSchema,
});
export type QueueTrackRequest = z.infer<typeof QueueTrackRequestSchema>;

export const QueueTrackAcceptedSchema = z.strictObject({
  queueItemId: UuidSchema,
});
export type QueueTrackAccepted = z.infer<typeof QueueTrackAcceptedSchema>;

export const PlaybackQueueAcceptedSchema = QueueTrackAcceptedSchema;
export type PlaybackQueueAccepted = z.infer<typeof PlaybackQueueAcceptedSchema>;

export const QueueReorderRequestSchema = z
  .strictObject({
    queueItemIds: z.array(UuidSchema),
  })
  .superRefine((request, context) => {
    if (new Set(request.queueItemIds).size !== request.queueItemIds.length) {
      context.addIssue({ code: 'custom', path: ['queueItemIds'], message: 'queueItemIds must be unique' });
    }
  });
export type QueueReorderRequest = z.infer<typeof QueueReorderRequestSchema>;

export const PlayNowRequestSchema = QueueTrackRequestSchema;
export type PlayNowRequest = QueueTrackRequest;

export const ReorderQueueRequestSchema = QueueReorderRequestSchema;
export type ReorderQueueRequest = QueueReorderRequest;

export const QueueControlRequestSchema = z.strictObject({});
export type QueueControlRequest = z.infer<typeof QueueControlRequestSchema>;

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

export const BannerReplaceItemSchema = z.strictObject({
  id: UuidSchema.nullable(),
  type: BannerTypeSchema,
  title: z.string(),
  subtitle: z.string().nullable(),
  text: z.string().nullable(),
  style: BannerStyleSchema,
  screenPosition: BannerPositionSchema,
  accent: z.string().nullable(),
  enabled: z.boolean(),
  rotationSeconds: z.number().int().min(2).max(120).nullable(),
});
export type BannerReplaceItem = z.infer<typeof BannerReplaceItemSchema>;

export const BannersReplaceRequestSchema = z.array(BannerReplaceItemSchema);
export type BannersReplaceRequest = z.infer<typeof BannersReplaceRequestSchema>;

export const PlaylistTypeSchema = z.enum(['general', 'oncePerSongs', 'oncePerMinutes', 'oncePerHour']);
export type PlaylistType = z.infer<typeof PlaylistTypeSchema>;
export const PlaylistSourceSchema = z.enum(['manual', 'allStorage']);
export type PlaylistSource = z.infer<typeof PlaylistSourceSchema>;
export const PlaylistOrderSchema = z.enum(['sequential', 'shuffle', 'random']);
export type PlaylistOrder = z.infer<typeof PlaylistOrderSchema>;
const PlaylistTimeSchema = z.string().regex(/^(?:[01]\d|2[0-3]):[0-5]\d$/);
const PlaylistDateSchema = z.string().regex(/^\d{4}-\d{2}-\d{2}$/);
function isCalendarDate(value: string): boolean {
  const parsed = new Date(`${value}T00:00:00.000Z`);
  return !Number.isNaN(parsed.valueOf()) && parsed.toISOString().slice(0, 10) === value;
}

export const PlaylistScheduleSchema = z
  .strictObject({
    id: UuidSchema.nullable(),
    daysOfWeek: z.array(z.number().int().min(1).max(7)),
    startTime: PlaylistTimeSchema,
    endTime: PlaylistTimeSchema,
    startDate: PlaylistDateSchema.nullable(),
    endDate: PlaylistDateSchema.nullable(),
    timeZoneId: z.string().min(1).max(100),
  })
  .superRefine((schedule, context) => {
    if (new Set(schedule.daysOfWeek).size !== schedule.daysOfWeek.length) {
      context.addIssue({ code: 'custom', path: ['daysOfWeek'], message: 'daysOfWeek must be unique' });
    }
    if (schedule.startDate !== null && !isCalendarDate(schedule.startDate)) {
      context.addIssue({ code: 'custom', path: ['startDate'], message: 'startDate must be a calendar date' });
    }
    if (schedule.endDate !== null && !isCalendarDate(schedule.endDate)) {
      context.addIssue({ code: 'custom', path: ['endDate'], message: 'endDate must be a calendar date' });
    }
    if (schedule.startDate !== null && schedule.endDate !== null && schedule.startDate > schedule.endDate) {
      context.addIssue({ code: 'custom', path: ['endDate'], message: 'endDate must not precede startDate' });
    }
  });
export type PlaylistSchedule = z.infer<typeof PlaylistScheduleSchema>;

const PlaylistPolicyFields = {
  type: PlaylistTypeSchema,
  source: PlaylistSourceSchema,
  order: PlaylistOrderSchema,
  weight: z.number().int().min(1).max(25),
  isJingle: z.boolean(),
  interrupt: z.boolean(),
  avoidDuplicates: z.boolean(),
  playEverySongs: z.number().int().min(1).max(1000).nullable(),
  playEveryMinutes: z.number().int().min(1).max(10080).nullable(),
  playAtMinute: z.number().int().min(0).max(59).nullable(),
  schedules: z.array(PlaylistScheduleSchema).max(32),
} as const;

function validatePlaylistCadence(
  policy: {
    readonly type: 'general' | 'oncePerSongs' | 'oncePerMinutes' | 'oncePerHour';
    readonly playEverySongs: number | null;
    readonly playEveryMinutes: number | null;
    readonly playAtMinute: number | null;
  },
  context: z.RefinementCtx,
): void {
  const activeCadenceCount = [policy.playEverySongs, policy.playEveryMinutes, policy.playAtMinute].filter(
    (value) => value !== null,
  ).length;
  const expectedCount = policy.type === 'general' ? 0 : 1;
  if (activeCadenceCount !== expectedCount) {
    context.addIssue({ code: 'custom', path: ['type'], message: 'cadence does not match playlist type' });
    return;
  }
  if (policy.type === 'oncePerSongs' && policy.playEverySongs === null) {
    context.addIssue({ code: 'custom', path: ['playEverySongs'], message: 'songs cadence is required' });
  }
  if (policy.type === 'oncePerMinutes' && policy.playEveryMinutes === null) {
    context.addIssue({ code: 'custom', path: ['playEveryMinutes'], message: 'minutes cadence is required' });
  }
  if (policy.type === 'oncePerHour' && policy.playAtMinute === null) {
    context.addIssue({ code: 'custom', path: ['playAtMinute'], message: 'hour cadence is required' });
  }
}

export const PlaylistSchema = z
  .strictObject({
    id: UuidSchema,
    name: z.string(),
    description: z.string().nullable(),
    isActive: z.boolean(),
    ...PlaylistPolicyFields,
    isSystem: z.boolean(),
    itemCount: NonNegativeIntegerSchema,
  })
  .superRefine((playlist, context) => validatePlaylistCadence(playlist, context));
export type Playlist = z.infer<typeof PlaylistSchema>;
export type PlaylistPolicy = Pick<
  Playlist,
  | 'type'
  | 'source'
  | 'order'
  | 'weight'
  | 'isJingle'
  | 'interrupt'
  | 'avoidDuplicates'
  | 'playEverySongs'
  | 'playEveryMinutes'
  | 'playAtMinute'
  | 'schedules'
>;

export const PlaylistMutationRequestSchema = z
  .strictObject({
    name: z.string(),
    description: z.string().nullable(),
    isActive: z.boolean(),
    ...PlaylistPolicyFields,
  })
  .superRefine((playlist, context) => validatePlaylistCadence(playlist, context));
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

export const StorageCacheSettingsSchema = z.strictObject({
  s3CacheMaxBytes: z.number().int().nonnegative(),
  presignTtlSeconds: z.number().int().positive(),
});
export type StorageCacheSettings = z.infer<typeof StorageCacheSettingsSchema>;

export const StorageCacheSettingsUpdateSchema = z.strictObject({
  s3CacheMaxBytes: z.number().int().positive(),
  presignTtlSeconds: z.number().int().positive(),
});
export type StorageCacheSettingsUpdate = z.infer<typeof StorageCacheSettingsUpdateSchema>;

export const StorageEntryKindSchema = z.enum(['file', 'folder']);
export type StorageEntryKind = z.infer<typeof StorageEntryKindSchema>;

const StorageUuidSchema = z.string().uuid();
const StorageTimestampSchema = z.string().datetime({ offset: true });

export const StorageEntrySchema = z.strictObject({
  path: z.string(),
  name: z.string(),
  kind: StorageEntryKindSchema,
  sizeBytes: NonNegativeIntegerSchema.nullable(),
  lastModifiedUtc: StorageTimestampSchema.nullable(),
  contentType: z.string().nullable(),
});
export type StorageEntry = z.infer<typeof StorageEntrySchema>;

export const StorageEntryPageSchema = z.strictObject({
  path: z.string(),
  items: z.array(StorageEntrySchema),
  nextCursor: z.string().nullable(),
});
export type StorageEntryPage = z.infer<typeof StorageEntryPageSchema>;

export const StorageDeleteSelectionSchema = z.strictObject({
  path: z.string().min(1),
  kind: StorageEntryKindSchema,
});
export type StorageDeleteSelection = z.infer<typeof StorageDeleteSelectionSchema>;

export const StorageDeleteRequestSchema = z.strictObject({
  storageBackendId: StorageUuidSchema.nullable(),
  entries: z.array(StorageDeleteSelectionSchema).min(1).max(100),
});
export type StorageDeleteRequest = z.infer<typeof StorageDeleteRequestSchema>;

export const StorageDeleteConfirmRequestSchema = StorageDeleteRequestSchema.extend({
  impactToken: z.string().min(1),
});
export type StorageDeleteConfirmRequest = z.infer<typeof StorageDeleteConfirmRequestSchema>;

export const StoragePlaylistMembershipSchema = z.strictObject({
  playlistId: StorageUuidSchema,
  playlistName: z.string(),
  trackCount: NonNegativeIntegerSchema,
});
export type StoragePlaylistMembership = z.infer<typeof StoragePlaylistMembershipSchema>;

export const StorageImpactTrackSchema = z.strictObject({
  trackId: StorageUuidSchema,
  title: z.string(),
  artist: z.string(),
  playlistNames: z.array(z.string()),
});
export type StorageImpactTrack = z.infer<typeof StorageImpactTrackSchema>;

export const StorageCurrentTrackSchema = z.strictObject({
  trackId: StorageUuidSchema,
  title: z.string(),
  artist: z.string(),
});
export type StorageCurrentTrack = z.infer<typeof StorageCurrentTrackSchema>;

export const StorageDeleteImpactSchema = z.strictObject({
  fileCount: NonNegativeIntegerSchema,
  folderCount: NonNegativeIntegerSchema,
  totalBytes: NonNegativeIntegerSchema,
  trackedFileCount: NonNegativeIntegerSchema,
  tracksToDeleteCount: NonNegativeIntegerSchema,
  playlistMemberships: z.array(StoragePlaylistMembershipSchema),
  sampleTracks: z.array(StorageImpactTrackSchema),
  sampleTracksTruncated: z.boolean(),
  currentTrack: StorageCurrentTrackSchema.nullable(),
  impactToken: z.string().min(1),
});
export type StorageDeleteImpact = z.infer<typeof StorageDeleteImpactSchema>;

export const StorageDeleteResultSchema = z.strictObject({
  deletedFileCount: NonNegativeIntegerSchema,
  deletedFolderCount: NonNegativeIntegerSchema,
  detachedPlaylistItemCount: NonNegativeIntegerSchema,
  deletedTrackCount: NonNegativeIntegerSchema,
  playbackAdvanced: z.boolean(),
});
export type StorageDeleteResult = z.infer<typeof StorageDeleteResultSchema>;

export const PlaybackCommandSchema = z.strictObject({
  generation: NonNegativeIntegerSchema,
  action: z.enum(['skip', 'restart']),
  queueItemId: UuidSchema,
  claimOwner: UuidSchema,
  claimAttempt: z.number().int().positive(),
});
export type PlaybackCommand = z.infer<typeof PlaybackCommandSchema>;

// The admin control response reuses the stream-node control DTO, which also carries
// the batched playback commands + next generation. Those are irrelevant to the admin
// UI, so they are optional here while `desiredState`/`restartGeneration` are required.
export const StreamNodeControlSchema = z.strictObject({
  desiredState: z.enum(['running', 'paused', 'stopped']),
  restartGeneration: NonNegativeIntegerSchema,
  playbackCommands: z.array(PlaybackCommandSchema).optional(),
  nextPlaybackGeneration: NonNegativeIntegerSchema.optional(),
});
export type StreamNodeControl = z.infer<typeof StreamNodeControlSchema>;

export const StreamNodePlaybackControlSchema = z.strictObject({
  desiredState: z.enum(['running', 'paused', 'stopped']),
  restartGeneration: NonNegativeIntegerSchema,
  playbackCommands: z.array(PlaybackCommandSchema),
  nextPlaybackGeneration: NonNegativeIntegerSchema,
});
export type StreamNodePlaybackControl = z.infer<typeof StreamNodePlaybackControlSchema>;

export const PlaybackControlQuerySchema = z.strictObject({
  afterPlaybackGeneration: NonNegativeIntegerSchema,
  limit: z.number().int().min(1).max(100),
});
export type PlaybackControlQuery = z.infer<typeof PlaybackControlQuerySchema>;

export const StreamNodeStatusSchema = z.strictObject({
  status: StreamStatusSchema,
  desiredState: z.enum(['running', 'paused', 'stopped']),
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
