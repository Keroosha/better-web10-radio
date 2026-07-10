// Typed clients for every pinned `/api/v0/admin/*` contract.
import { z } from 'zod';

import {
  AdminSayMessageSchema,
  AdminSessionSchema,
  AdminTrackSchema,
  type AdminTrack,
  type AdminSayMessage,
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
  type QueueTrackAccepted,
  QueueTrackAcceptedSchema,
  type QueueTrackRequest,
  type SocialLinksReplaceRequest,
  type Storage,
  type StorageReplaceRequest,
  StorageSchema,
  type StreamNodeControl,
  StreamNodeControlSchema,
  type StreamNodeStatus,
  StreamNodeStatusSchema,
} from '../domain/admin';
import { type SuperChatStatus } from '../domain/enums';
import {
  DonationGoalSchema,
  type DonationGoal,
  SocialLinkSchema,
  type SocialLink,
} from '../domain/player-state';
import {
  API_V0_PREFIX,
  apiFetch,
  apiSend,
  clearAdminSession,
  setAdminSession,
  type RequestOptions,
} from './client';

const SocialLinkListSchema = z.array(SocialLinkSchema);
const AdminSayMessageListSchema = z.array(AdminSayMessageSchema);
const AdminTrackListSchema = z.array(AdminTrackSchema);
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
}

/** List active library tracks, defaulting to an empty query and 100 results. */
export function getTracks(query: TracksQuery = {}, opts: RequestOptions = {}): Promise<AdminTrack[]> {
  const parameters = new URLSearchParams({
    query: query.query ?? '',
    limit: String(query.limit ?? 100),
  });
  return apiFetch(`${API_V0_PREFIX}/admin/tracks?${parameters.toString()}`, {
    schema: AdminTrackListSchema,
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

export function getStreamNodeStatus(opts: RequestOptions = {}): Promise<StreamNodeStatus> {
  return apiFetch(`${API_V0_PREFIX}/admin/stream-node/status`, {
    schema: StreamNodeStatusSchema,
    admin: true,
    ...opts,
  });
}

function sendStreamNodeControl(
  action: 'start' | 'stop' | 'restart',
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

export function getSayMessages(
  status: SuperChatStatus,
  opts: RequestOptions = {},
): Promise<AdminSayMessage[]> {
  return apiFetch(`${API_V0_PREFIX}/admin/say-messages?status=${encodeURIComponent(status)}`, {
    schema: AdminSayMessageListSchema,
    admin: true,
    ...opts,
  });
}

export function approveSayMessage(messageId: string, opts: RequestOptions = {}): Promise<void> {
  return apiSend(`${API_V0_PREFIX}/admin/say-messages/${encodeURIComponent(messageId)}/approve`, {
    method: 'POST',
    body: emptyAdminRequest,
    admin: true,
    ...opts,
  });
}

export function rejectSayMessage(
  messageId: string,
  reason: string,
  opts: RequestOptions = {},
): Promise<void> {
  return apiSend(`${API_V0_PREFIX}/admin/say-messages/${encodeURIComponent(messageId)}/reject`, {
    method: 'POST',
    body: { reason },
    admin: true,
    ...opts,
  });
}
