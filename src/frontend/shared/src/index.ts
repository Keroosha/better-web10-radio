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

// Error contract.
export { ProblemDetailsSchema, type ProblemDetails } from './domain/problem-details';

// HTTP API client.
export {
  API_V0_PREFIX,
  ApiError,
  apiFetch,
  getApiBaseUrl,
  setApiBaseUrl,
  type FetchImpl,
  type RequestOptions,
  type ApiRequest,
} from './api/client';
export { getPlayerState, playerStreamUrl } from './api/player';
export { getDonationGoal, getSocialLinks } from './api/admin';

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
