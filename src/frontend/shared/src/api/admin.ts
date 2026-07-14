// Typed clients for every pinned `/api/v0/admin/*` contract.
import { z } from 'zod';
import {
  type BannersReplaceRequest,
  AdminSessionSchema,
  AdminTrackPageSchema,
  type AdminTrackPage,
  AdminTrackSchema,
  type AdminTrack,
  type AdminSession,
  type DonationGoalUpdateRequest,
  EmptyAdminRequestSchema,
  type LibraryScanAccepted,
  LibraryScanAcceptedSchema,
  type LibraryScanRequest,
  type LibraryScanStatus,
  LibraryScanStatusSchema,
  type LoginAdminRequest,
  type PaidVerticalSliceFixture,
  PaidVerticalSliceFixtureSchema,
  type PaidVerticalSliceFixtureRequest,
  type Playlist,
  type PlaylistItem,
  type PlaylistItemCreateRequest,
  PlaylistItemSchema,
  type PlaylistItemsReplaceRequest,
  type PlaylistMutationRequest,
  PlaylistSchema,
  type QueueReorderRequest,
  QueueTrackAcceptedSchema,
  type QueueTrackAccepted,
  type QueueTrackRequest,
  PlaybackQueueAcceptedSchema,
  type PlaybackQueueAccepted,
  type TrackMetadataUpdateRequest,
  type SocialLinksReplaceRequest,
  type Storage,
  type StorageReplaceRequest,
  StorageSchema,
  type StorageCacheSettings,
  type StorageCacheSettingsUpdate,
  StorageCacheSettingsSchema,
  type StorageEntry,
  type StorageEntryPage,
  type StorageFolderCreateRequest,
  StorageEntrySchema,
  StorageEntryPageSchema,
  type StorageDeleteRequest,
  type StorageDeleteConfirmRequest,
  type StorageDeleteImpact,
  StorageDeleteImpactSchema,
  type StorageDeleteResult,
  StorageDeleteResultSchema,
  type StreamNodeControl,
  StreamNodeControlSchema,
  type StreamNodeStatus,
  StreamNodeStatusSchema,
} from '../domain/admin';
import {
  BannerSchema,
  type Banner,
  DonationGoalSchema,
  type DonationGoal,
  QueueStateSchema,
  type QueueState,
  SocialLinkSchema,
  type SocialLink,
} from '../domain/player-state';
import {
  API_V0_PREFIX,
  apiFetch,
  apiSend,
  apiUpload,
  clearAdminSession,
  setAdminSession,
  type RequestOptions,
} from './client';

const SocialLinkListSchema = z.array(SocialLinkSchema);
const BannerListSchema = z.array(BannerSchema);
const PlaylistListSchema = z.array(PlaylistSchema);
const PlaylistItemListSchema = z.array(PlaylistItemSchema);
const emptyAdminRequest = EmptyAdminRequestSchema.parse({});

/** POST login credentials and retain the validated session/CSRF DTO. */
export async function loginAdmin(
  body: LoginAdminRequest,
  opts: RequestOptions = {},
): Promise<AdminSession> {
  const session = await apiFetch<AdminSession, LoginAdminRequest>(`${API_V0_PREFIX}/admin/auth/login`, {
    schema: AdminSessionSchema,
    method: 'POST',
    body,
    admin: true,
    authProbe: true,
    ...opts,
  });
  setAdminSession(session);
  return session;
}

/** Restore the cookie-backed session without invalidating state on a 401 probe. */
export async function getAdminSession(opts: RequestOptions = {}): Promise<AdminSession> {
  const session = await apiFetch(`${API_V0_PREFIX}/admin/auth/session`, {
    schema: AdminSessionSchema,
    admin: true,
    authProbe: true,
    ...opts,
  });
  setAdminSession(session);
  return session;
}

/** Revoke the active server session and discard retained CSRF state. */
export async function logoutAdmin(opts: RequestOptions = {}): Promise<void> {
  await apiSend(`${API_V0_PREFIX}/admin/auth/logout`, {
    method: 'POST',
    body: emptyAdminRequest,
    admin: true,
    ...opts,
  });
  clearAdminSession();
}

/** Queue a default or explicit-storage library scan. */
export function createLibraryScan(
  body: LibraryScanRequest,
  opts: RequestOptions = {},
): Promise<LibraryScanAccepted> {
  return apiFetch<LibraryScanAccepted, LibraryScanRequest>(`${API_V0_PREFIX}/admin/library/scan`, {
    schema: LibraryScanAcceptedSchema,
    method: 'POST',
    body,
    admin: true,
    ...opts,
  });
}

/** Read one asynchronous library-scan job. */
export function getLibraryScan(scanJobId: string, opts: RequestOptions = {}): Promise<LibraryScanStatus> {
  return apiFetch(`${API_V0_PREFIX}/admin/library/scan/${encodeURIComponent(scanJobId)}`, {
    schema: LibraryScanStatusSchema,
    admin: true,
    ...opts,
  });
}

export interface TracksQuery {
  readonly query?: string;
  readonly limit?: number;
  readonly cursor?: string | null;
}

/** List active library tracks using the cursor ordered by creation time and ID. */
export function getTracksPage(
  query: TracksQuery = {},
  opts: RequestOptions = {},
): Promise<AdminTrackPage> {
  const parameters = new URLSearchParams({
    query: query.query ?? '',
    limit: String(query.limit ?? 100),
  });
  if (query.cursor !== undefined && query.cursor !== null) {
    parameters.set('cursor', query.cursor);
  }
  return apiFetch(`${API_V0_PREFIX}/admin/tracks?${parameters.toString()}`, {
    schema: AdminTrackPageSchema,
    admin: true,
    ...opts,
  });
}

/** Replace canonical metadata without modifying source-media tags. */
export function updateTrackMetadata(
  trackId: string,
  body: TrackMetadataUpdateRequest,
  opts: RequestOptions = {},
): Promise<AdminTrack> {
  return apiFetch<AdminTrack, TrackMetadataUpdateRequest>(
    `${API_V0_PREFIX}/admin/tracks/${encodeURIComponent(trackId)}`,
    {
      schema: AdminTrackSchema,
      method: 'PUT',
      body,
      admin: true,
      ...opts,
    },
  );
}

export type CoverUploadBody = Blob | FormData;

/** Upload a managed cover image as raw bytes or multipart form data. */
export function replaceTrackCover(
  trackId: string,
  body: CoverUploadBody,
  opts: RequestOptions = {},
): Promise<AdminTrack> {
  const contentType = body instanceof Blob && body.type.length > 0 ? body.type : undefined;
  return apiUpload<AdminTrack>(`${API_V0_PREFIX}/admin/tracks/${encodeURIComponent(trackId)}/cover`, {
    schema: AdminTrackSchema,
    method: 'PUT',
    body,
    ...(contentType === undefined ? {} : { contentType }),
    admin: true,
    ...opts,
  });
}

/** Soft-delete the managed cover and return the updated track projection. */
export function removeTrackCover(trackId: string, opts: RequestOptions = {}): Promise<AdminTrack> {
  return apiFetch(`${API_V0_PREFIX}/admin/tracks/${encodeURIComponent(trackId)}/cover`, {
    schema: AdminTrackSchema,
    method: 'DELETE',
    admin: true,
    ...opts,
  });
}

/** Accept an immediately playable scanned track into the playback queue. */
export function queueTrack(
  body: QueueTrackRequest,
  opts: RequestOptions = {},
): Promise<QueueTrackAccepted> {
  return apiFetch<QueueTrackAccepted, QueueTrackRequest>(`${API_V0_PREFIX}/admin/playback/queue`, {
    schema: QueueTrackAcceptedSchema,
    method: 'POST',
    body,
    admin: true,
    ...opts,
  });
}

/** Replace the complete active queued-ID order atomically. */
export function reorderQueue(
  body: QueueReorderRequest,
  opts: RequestOptions = {},
): Promise<QueueState> {
  return apiFetch<QueueState, QueueReorderRequest>(`${API_V0_PREFIX}/admin/playback/queue/order`, {
    schema: QueueStateSchema,
    method: 'PUT',
    body,
    admin: true,
    ...opts,
  });
}

/** Soft-delete a queued item (only items still in the `Queued` state). */
export function removeQueueItem(queueItemId: string, opts: RequestOptions = {}): Promise<void> {
  return apiSend<Record<string, never>>(`${API_V0_PREFIX}/admin/playback/queue/${queueItemId}`, {
    method: 'DELETE',
    admin: true,
    ...opts,
  });
}

/** Skip the currently playing or claimed item. */
export function skipCurrent(opts: RequestOptions = {}): Promise<void> {
  return apiSend(`${API_V0_PREFIX}/admin/playback/skip`, {
    method: 'POST',
    body: emptyAdminRequest,
    admin: true,
    ...opts,
  });
}

/** Insert a durable restart command for the currently playing item. */
export function restartCurrent(opts: RequestOptions = {}): Promise<void> {
  return apiSend(`${API_V0_PREFIX}/admin/playback/restart-current`, {
    method: 'POST',
    body: emptyAdminRequest,
    admin: true,
    ...opts,
  });
}

/** Queue a track immediately, interrupting the current assignment when present. */
export function playNow(
  body: QueueTrackRequest,
  opts: RequestOptions = {},
): Promise<PlaybackQueueAccepted> {
  return apiFetch<PlaybackQueueAccepted, QueueTrackRequest>(`${API_V0_PREFIX}/admin/playback/play-now`, {
    schema: PlaybackQueueAcceptedSchema,
    method: 'POST',
    body,
    admin: true,
    ...opts,
  });
}

export function getDonationGoal(opts: RequestOptions = {}): Promise<DonationGoal> {
  return apiFetch(`${API_V0_PREFIX}/admin/donation-goal`, {
    schema: DonationGoalSchema,
    admin: true,
    ...opts,
  });
}

export function updateDonationGoal(
  body: DonationGoalUpdateRequest,
  opts: RequestOptions = {},
): Promise<DonationGoal> {
  return apiFetch<DonationGoal, DonationGoalUpdateRequest>(`${API_V0_PREFIX}/admin/donation-goal`, {
    schema: DonationGoalSchema,
    method: 'PUT',
    body,
    admin: true,
    ...opts,
  });
}

export function getSocialLinks(opts: RequestOptions = {}): Promise<SocialLink[]> {
  return apiFetch(`${API_V0_PREFIX}/admin/social-links`, {
    schema: SocialLinkListSchema,
    admin: true,
    ...opts,
  });
}

export function replaceSocialLinks(
  body: SocialLinksReplaceRequest,
  opts: RequestOptions = {},
): Promise<SocialLink[]> {
  return apiFetch<SocialLink[], SocialLinksReplaceRequest>(`${API_V0_PREFIX}/admin/social-links`, {
    schema: SocialLinkListSchema,
    method: 'PUT',
    body,
    admin: true,
    ...opts,
  });
}

export function getBanners(opts: RequestOptions = {}): Promise<Banner[]> {
  return apiFetch(`${API_V0_PREFIX}/admin/banners`, {
    schema: BannerListSchema,
    admin: true,
    ...opts,
  });
}

export function replaceBanners(
  body: BannersReplaceRequest,
  opts: RequestOptions = {},
): Promise<Banner[]> {
  return apiFetch<Banner[], BannersReplaceRequest>(`${API_V0_PREFIX}/admin/banners`, {
    schema: BannerListSchema,
    method: 'PUT',
    body,
    admin: true,
    ...opts,
  });
}

export function getPlaylists(opts: RequestOptions = {}): Promise<Playlist[]> {
  return apiFetch(`${API_V0_PREFIX}/admin/playlists`, {
    schema: PlaylistListSchema,
    admin: true,
    ...opts,
  });
}

export function createPlaylist(
  body: PlaylistMutationRequest,
  opts: RequestOptions = {},
): Promise<Playlist> {
  return apiFetch<Playlist, PlaylistMutationRequest>(`${API_V0_PREFIX}/admin/playlists`, {
    schema: PlaylistSchema,
    method: 'POST',
    body,
    admin: true,
    ...opts,
  });
}

export function replacePlaylist(
  playlistId: string,
  body: PlaylistMutationRequest,
  opts: RequestOptions = {},
): Promise<Playlist> {
  return apiFetch<Playlist, PlaylistMutationRequest>(
    `${API_V0_PREFIX}/admin/playlists/${encodeURIComponent(playlistId)}`,
    {
      schema: PlaylistSchema,
      method: 'PUT',
      body,
      admin: true,
      ...opts,
    },
  );
}

export function getPlaylistItems(playlistId: string, opts: RequestOptions = {}): Promise<PlaylistItem[]> {
  return apiFetch(`${API_V0_PREFIX}/admin/playlists/${encodeURIComponent(playlistId)}/items`, {
    schema: PlaylistItemListSchema,
    admin: true,
    ...opts,
  });
}

export function createPlaylistItem(
  playlistId: string,
  body: PlaylistItemCreateRequest,
  opts: RequestOptions = {},
): Promise<PlaylistItem> {
  return apiFetch<PlaylistItem, PlaylistItemCreateRequest>(
    `${API_V0_PREFIX}/admin/playlists/${encodeURIComponent(playlistId)}/items`,
    {
      schema: PlaylistItemSchema,
      method: 'POST',
      body,
      admin: true,
      ...opts,
    },
  );
}

export function replacePlaylistItems(
  playlistId: string,
  body: PlaylistItemsReplaceRequest,
  opts: RequestOptions = {},
): Promise<PlaylistItem[]> {
  return apiFetch<PlaylistItem[], PlaylistItemsReplaceRequest>(
    `${API_V0_PREFIX}/admin/playlists/${encodeURIComponent(playlistId)}/items`,
    {
      schema: PlaylistItemListSchema,
      method: 'PUT',
      body,
      admin: true,
      ...opts,
    },
  );
}

export function getStorage(opts: RequestOptions = {}): Promise<Storage> {
  return apiFetch(`${API_V0_PREFIX}/admin/storage`, {
    schema: StorageSchema,
    admin: true,
    ...opts,
  });
}

export function replaceStorage(
  body: StorageReplaceRequest,
  opts: RequestOptions = {},
): Promise<Storage> {
  return apiFetch<Storage, StorageReplaceRequest>(`${API_V0_PREFIX}/admin/storage`, {
    schema: StorageSchema,
    method: 'PUT',
    body,
    admin: true,
    ...opts,
  });
}

export function getStorageCacheSettings(opts: RequestOptions = {}): Promise<StorageCacheSettings> {
  return apiFetch(`${API_V0_PREFIX}/admin/storage/cache`, {
    schema: StorageCacheSettingsSchema,
    admin: true,
    ...opts,
  });
}

export function updateStorageCacheSettings(
  body: StorageCacheSettingsUpdate,
  opts: RequestOptions = {},
): Promise<StorageCacheSettings> {
  return apiFetch<StorageCacheSettings, StorageCacheSettingsUpdate>(
    `${API_V0_PREFIX}/admin/storage/cache`,
    {
      schema: StorageCacheSettingsSchema,
      method: 'PUT',
      body,
      admin: true,
      ...opts,
    },
  );
}

export interface StorageEntriesQuery {
  readonly storageBackendId?: string | null;
  readonly path?: string;
  readonly limit?: number;
  readonly cursor?: string | null;
}

function storageQueryString(query: StorageEntriesQuery): string {
  const params = new URLSearchParams();
  if (query.storageBackendId !== undefined && query.storageBackendId !== null) {
    params.set('storageBackendId', query.storageBackendId);
  }
  if (query.path !== undefined) {
    params.set('path', query.path);
  }
  if (query.limit !== undefined) {
    params.set('limit', String(query.limit));
  }
  if (query.cursor !== undefined && query.cursor !== null) {
    params.set('cursor', query.cursor);
  }
  const encoded = params.toString();
  return encoded === '' ? '' : `?${encoded}`;
}

export function getStorageEntries(
  query: StorageEntriesQuery = {},
  opts: RequestOptions = {},
): Promise<StorageEntryPage> {
  return apiFetch(`${API_V0_PREFIX}/admin/storage/files${storageQueryString(query)}`, {
    schema: StorageEntryPageSchema,
    admin: true,
    ...opts,
  });
}

export interface StorageContentQuery {
  readonly storageBackendId?: string | null;
  readonly path: string;
  readonly download?: boolean;
}

export function storageContentUrl(query: StorageContentQuery): string {
  const params = new URLSearchParams();
  if (query.storageBackendId !== undefined && query.storageBackendId !== null) {
    params.set('storageBackendId', query.storageBackendId);
  }
  params.set('path', query.path);
  if (query.download === true) {
    params.set('download', 'true');
  }
  return `${API_V0_PREFIX}/admin/storage/files/content?${params.toString()}`;
}
export function uploadStorageFile(
  query: StorageContentQuery,
  file: File,
  opts: RequestOptions = {},
): Promise<StorageEntry> {
  const queryParams: StorageEntriesQuery = query.storageBackendId === undefined
    ? { path: query.path }
    : { storageBackendId: query.storageBackendId, path: query.path };
  return apiUpload(`${API_V0_PREFIX}/admin/storage/files/content${storageQueryString(queryParams)}`, {
    schema: StorageEntrySchema,
    method: 'PUT',
    body: file,
    contentType: file.type === '' ? 'application/octet-stream' : file.type,
    admin: true,
    ...opts,
  });
}

export function createStorageFolder(
  body: StorageFolderCreateRequest,
  opts: RequestOptions = {},
): Promise<StorageEntry> {
  return apiFetch(`${API_V0_PREFIX}/admin/storage/folders`, {
    schema: StorageEntrySchema,
    method: 'POST',
    body,
    admin: true,
    ...opts,
  });
}

export function previewStorageDelete(
  body: StorageDeleteRequest,
  opts: RequestOptions = {},
): Promise<StorageDeleteImpact> {
  return apiFetch(`${API_V0_PREFIX}/admin/storage/files/delete-preview`, {
    schema: StorageDeleteImpactSchema,
    method: 'POST',
    body,
    admin: true,
    ...opts,
  });
}

export function deleteStorageEntries(
  body: StorageDeleteConfirmRequest,
  opts: RequestOptions = {},
): Promise<StorageDeleteResult> {
  return apiFetch(`${API_V0_PREFIX}/admin/storage/files`, {
    schema: StorageDeleteResultSchema,
    method: 'DELETE',
    body,
    admin: true,
    ...opts,
  });
}

export function getStreamNodeStatus(opts: RequestOptions = {}): Promise<StreamNodeStatus> {
  return apiFetch(`${API_V0_PREFIX}/admin/stream-node/status`, {
    schema: StreamNodeStatusSchema,
    admin: true,
    ...opts,
  });
}

function sendStreamNodeControl(
  action: 'start' | 'pause' | 'stop' | 'restart',
  opts: RequestOptions,
): Promise<StreamNodeControl> {
  return apiFetch(`${API_V0_PREFIX}/admin/stream-node/${action}`, {
    schema: StreamNodeControlSchema,
    method: 'POST',
    body: emptyAdminRequest,
    admin: true,
    ...opts,
  });
}

export function startStreamNode(opts: RequestOptions = {}): Promise<StreamNodeControl> {
  return sendStreamNodeControl('start', opts);
}

export function pauseStreamNode(opts: RequestOptions = {}): Promise<StreamNodeControl> {
  return sendStreamNodeControl('pause', opts);
}

export function stopStreamNode(opts: RequestOptions = {}): Promise<StreamNodeControl> {
  return sendStreamNodeControl('stop', opts);
}

export function restartStreamNode(opts: RequestOptions = {}): Promise<StreamNodeControl> {
  return sendStreamNodeControl('restart', opts);
}

export function createPaidVerticalSliceFixture(
  body: PaidVerticalSliceFixtureRequest,
  opts: RequestOptions = {},
): Promise<PaidVerticalSliceFixture> {
  return apiFetch<PaidVerticalSliceFixture, PaidVerticalSliceFixtureRequest>(
    `${API_V0_PREFIX}/admin/dev/fixtures/paid-vertical-slice`,
    {
      schema: PaidVerticalSliceFixtureSchema,
      method: 'POST',
      body,
      admin: true,
      ...opts,
    },
  );
}

