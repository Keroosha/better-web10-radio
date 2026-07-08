// SSE event contract for `GET /api/v0/player/events` (SPEC §5/§6).
//
// The frontend consumes NATIVE named SSE events (`event: player.state\ndata: {…}`).
// Each event's `data` is the matching fragment of the `/api/v0/player/state`
// snapshot. NOTE: the `{eventId, eventType, producer, correlationId, …}` envelope
// in SPEC §6 is the backend's internal outbox/MailboxProcessor model — it is NOT
// what crosses this boundary, so it is deliberately absent here.
import type {
  DonationGoal,
  PlayerState,
  QueueState,
  StreamState,
  SuperChatState,
} from '../domain/player-state';

/** The five SSE event names, in the order SPEC §5 lists them. */
export const PLAYER_EVENT_NAMES = [
  'player.state',
  'player.queue',
  'player.say',
  'player.donation',
  'player.health',
] as const;

export type PlayerEventName = (typeof PLAYER_EVENT_NAMES)[number];

/** Payload type carried by each named event (a fragment of `PlayerState`). */
export interface PlayerEventPayloads {
  'player.state': PlayerState;
  'player.queue': QueueState;
  'player.say': SuperChatState;
  'player.donation': DonationGoal;
  'player.health': StreamState;
}
