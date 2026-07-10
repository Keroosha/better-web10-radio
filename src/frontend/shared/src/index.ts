// Public entry point for @web10/shared — the ONLY module the app workspaces may
// import from. The package `exports` map in package.json enforces this boundary:
// deep paths (e.g. @web10/shared/src/domain/x) do not resolve.
//
// Everything below is a named domain type, validated API/SSE client, or formatter
// derived VERBATIM from docs/SPEC.md §5/§6 (Milestone FRONTEND, Phase F1).

// Domain enums (schemas + inferred literal types).
export {
  StreamStatusSchema,
  NowPlayingSourceSchema,
  QueueItemSourceSchema,
  QueueItemStatusSchema,
  SuperChatStatusSchema,
  SocialKindSchema,
  OverlayStyleSchema,
  OverlayLayoutSchema,
  type StreamStatus,
  type NowPlayingSource,
  type QueueItemSource,
  type QueueItemStatus,
  type SuperChatStatus,
  type SocialKind,
  type OverlayStyle,
  type OverlayLayout,
} from './domain/enums';

// Player-state DTOs (schemas + inferred types).
export {
  StreamStateSchema,
  NowPlayingSchema,
  QueueItemSchema,
  QueueStateSchema,
  TopDonatorSchema,
  RecentDonationSchema,
  DonationGoalSchema,
  SuperChatMessageSchema,
  SuperChatStateSchema,
  SocialLinkSchema,
  OverlaySettingsSchema,
  PlayerStateSchema,
  type StreamState,
  type NowPlaying,
  type QueueItem,
  type QueueState,
  type TopDonator,
  type RecentDonation,
  type DonationGoal,
  type SuperChatMessage,
  type SuperChatState,
  type SocialLink,
  type OverlaySettings,
  type PlayerState,
} from './domain/player-state';

// Admin cabinet DTOs (schemas + inferred types).
export {
  AdminSessionSchema,
  LoginAdminRequestSchema,
  EmptyAdminRequestSchema,
  LibraryScanRequestSchema,
  LibraryScanAcceptedSchema,
  LibraryScanStatusSchema,
  AdminTrackSchema,
  QueueTrackRequestSchema,
  QueueTrackAcceptedSchema,
  DonationGoalUpdateRequestSchema,
  SocialLinkReplaceItemSchema,
  SocialLinksReplaceRequestSchema,
  PlaylistSchema,
  PlaylistMutationRequestSchema,
  PlaylistItemSchema,
  PlaylistItemCreateRequestSchema,
  PlaylistItemReplaceRowSchema,
  PlaylistItemsReplaceRequestSchema,
  StorageDefaultBackendSchema,
  StorageAdditionalBackendSchema,
  StorageSchema,
  StorageAdditionalBackendReplaceSchema,
  StorageReplaceRequestSchema,
  StreamNodeControlSchema,
  StreamNodeStatusSchema,
  PaidVerticalSliceFixtureRequestSchema,
  PaidVerticalSliceFixtureSchema,
  AdminSayMessageSchema,
  type AdminSession,
  type LoginAdminRequest,
  type EmptyAdminRequest,
  type LibraryScanRequest,
  type LibraryScanAccepted,
  type LibraryScanStatus,
  type AdminTrack,
  type QueueTrackRequest,
  type QueueTrackAccepted,
  type DonationGoalUpdateRequest,
  type SocialLinkReplaceItem,
  type SocialLinksReplaceRequest,
  type Playlist,
  type PlaylistMutationRequest,
  type PlaylistItem,
  type PlaylistItemCreateRequest,
  type PlaylistItemReplaceRow,
  type PlaylistItemsReplaceRequest,
  type StorageDefaultBackend,
  type StorageAdditionalBackend,
  type Storage,
  type StorageAdditionalBackendReplace,
  type StorageReplaceRequest,
  type StreamNodeControl,
  type StreamNodeStatus,
  type PaidVerticalSliceFixtureRequest,
  type PaidVerticalSliceFixture,
  type AdminSayMessage,
} from './domain/admin';

// Error contract.
export { ProblemDetailsSchema, type ProblemDetails } from './domain/problem-details';

// HTTP API client.
export {
  API_V0_PREFIX,
  ApiError,
  apiFetch,
  apiSend,
  clearAdminSession,
  getApiBaseUrl,
  setAdminSession,
  setApiBaseUrl,
  subscribeToAdminSessionInvalidation,
  type FetchImpl,
  type RequestOptions,
  type ApiRequest,
  type SendRequest,
} from './api/client';
export { getPlayerState, playerStreamUrl } from './api/player';
export {
  loginAdmin,
  getAdminSession,
  logoutAdmin,
  createLibraryScan,
  getLibraryScan,
  getTracks,
  queueTrack,
  getDonationGoal,
  updateDonationGoal,
  getSocialLinks,
  replaceSocialLinks,
  getPlaylists,
  createPlaylist,
  replacePlaylist,
  getPlaylistItems,
  createPlaylistItem,
  replacePlaylistItems,
  getStorage,
  replaceStorage,
  getStreamNodeStatus,
  startStreamNode,
  stopStreamNode,
  restartStreamNode,
  createPaidVerticalSliceFixture,
  getSayMessages,
  approveSayMessage,
  rejectSayMessage,
  type TracksQuery,
} from './api/admin';

// SSE client + fallback.
export {
  PLAYER_EVENT_NAMES,
  type PlayerEventName,
  type PlayerEventPayloads,
} from './sse/events';
export {
  createPlayerEventsClient,
  type PlayerEventsClient,
  type PlayerEventHandlers,
  type PlayerEventsOptions,
  type PlayerEventsTransport,
  type SseConnection,
  type SseConnector,
  type SseConnectorSpec,
} from './sse/client';

// Formatters.
export { formatStars } from './format/stars';
export { formatUtcTime, INVALID_TIME_PLACEHOLDER } from './format/time';
export { formatDuration, formatProgress, formatTrackLabel } from './format/track';
