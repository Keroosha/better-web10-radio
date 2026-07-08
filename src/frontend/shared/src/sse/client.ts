// Player events client: consumes the SSE stream and applies the mandated fallback.
//
// Fallback rule (SPEC §5/§10): after TWO SSE disconnects within 30 seconds, stop
// using SSE and poll `GET /api/v0/player/state` every 5 seconds instead.
//
// Transport is abstracted behind `SseConnector` so this module is fully testable
// without a real `EventSource` (jsdom/node do not provide one): the default
// connector adapts the browser `EventSource`; tests inject a fake and drive it.
import { z } from 'zod';

import { API_V0_PREFIX, getApiBaseUrl, type FetchImpl } from '../api/client';
import { getPlayerState } from '../api/player';
import {
  DonationGoalSchema,
  PlayerStateSchema,
  QueueStateSchema,
  StreamStateSchema,
  SuperChatStateSchema,
} from '../domain/player-state';
import { PLAYER_EVENT_NAMES, type PlayerEventName } from './events';

/** Which transport the client is currently using. */
export type PlayerEventsTransport = 'sse' | 'polling';

/** A live connection the client can shut down. */
export interface SseConnection {
  close(): void;
}

/** What the client asks a connector to wire up. */
export interface SseConnectorSpec {
  readonly url: string;
  /** Called with the raw `data` string for a named event. */
  onMessage(eventName: PlayerEventName, data: string): void;
  /** Called when the underlying transport reports an error. */
  onError(): void;
}

/** Opens a transport for `spec` and returns a handle to close it. */
export type SseConnector = (spec: SseConnectorSpec) => SseConnection;

/** Default connector: adapts the browser-native `EventSource`. */
const defaultConnector: SseConnector = (spec) => {
  const source = new EventSource(spec.url);
  for (const name of PLAYER_EVENT_NAMES) {
    source.addEventListener(name, (event) => {
      // Custom-named SSE events arrive as `MessageEvent`; the generic
      // `addEventListener` overload types the arg as `Event`. This narrows to the
      // DOM subtype the SSE spec guarantees — it erases no domain type.
      spec.onMessage(name, (event as MessageEvent).data);
    });
  }
  source.addEventListener('error', () => spec.onError());
  return { close: () => source.close() };
};

export interface PlayerEventHandlers {
  onState?: (state: z.infer<typeof PlayerStateSchema>) => void;
  onQueue?: (queue: z.infer<typeof QueueStateSchema>) => void;
  onSay?: (superChat: z.infer<typeof SuperChatStateSchema>) => void;
  onDonation?: (donationGoal: z.infer<typeof DonationGoalSchema>) => void;
  onHealth?: (stream: z.infer<typeof StreamStateSchema>) => void;
  /** Fired when the active transport switches (e.g. SSE → polling). */
  onTransportChange?: (transport: PlayerEventsTransport) => void;
  /** Fired when an event payload is malformed and could not be validated. */
  onParseError?: (eventName: PlayerEventName, error: Error) => void;
  /** Fired when a fallback poll fails. */
  onPollError?: (error: Error) => void;
}

export interface PlayerEventsOptions {
  /** SSE URL; defaults to `<base>/api/v0/player/events`. */
  readonly url?: string;
  /** Transport factory; defaults to the native `EventSource` adapter. */
  readonly connector?: SseConnector;
  /** `fetch` used by fallback polling; forwarded to `getPlayerState`. */
  readonly fetchImpl?: FetchImpl;
  /** Clock, injectable for deterministic tests; defaults to `Date.now`. */
  readonly now?: () => number;
  /** Rolling window for counting disconnects. Default 30_000 ms. */
  readonly disconnectWindowMs?: number;
  /** Disconnects within the window that trigger fallback. Default 2. */
  readonly disconnectThreshold?: number;
  /** Fallback poll interval. Default 5_000 ms. */
  readonly pollIntervalMs?: number;
}

/** A running client. Call `close()` to release the transport and any timers. */
export interface PlayerEventsClient {
  close(): void;
}

// Generic param (not `unknown`) so authored `unknown`/`any` are avoided while still
// accepting whatever `catch`/promise rejection hands us.
function toError<TCause>(cause: TCause): Error {
  return cause instanceof Error ? cause : new Error(String(cause));
}

/**
 * Open the player events stream. Validates every payload against its schema before
 * invoking the handler, tracks disconnects, and switches to polling per the SPEC
 * fallback rule. Returns a handle whose `close()` tears everything down.
 */
export function createPlayerEventsClient(
  handlers: PlayerEventHandlers,
  options: PlayerEventsOptions = {},
): PlayerEventsClient {
  const url = options.url ?? `${getApiBaseUrl()}${API_V0_PREFIX}/player/events`;
  const connector = options.connector ?? defaultConnector;
  const now = options.now ?? Date.now;
  const windowMs = options.disconnectWindowMs ?? 30_000;
  const threshold = options.disconnectThreshold ?? 2;
  const pollIntervalMs = options.pollIntervalMs ?? 5_000;

  let closed = false;
  let transport: PlayerEventsTransport = 'sse';
  let connection: SseConnection | null = null;
  let pollTimer: ReturnType<typeof setInterval> | null = null;
  const disconnects: number[] = [];

  function dispatch<T>(
    schema: z.ZodType<T>,
    eventName: PlayerEventName,
    data: string,
    handler: ((payload: T) => void) | undefined,
  ): void {
    if (!handler) return;
    let payload: T;
    try {
      payload = schema.parse(JSON.parse(data));
    } catch (cause) {
      handlers.onParseError?.(eventName, toError(cause));
      return;
    }
    handler(payload);
  }

  function handleMessage(eventName: PlayerEventName, data: string): void {
    if (closed) return;
    switch (eventName) {
      case 'player.state':
        return dispatch(PlayerStateSchema, eventName, data, handlers.onState);
      case 'player.queue':
        return dispatch(QueueStateSchema, eventName, data, handlers.onQueue);
      case 'player.say':
        return dispatch(SuperChatStateSchema, eventName, data, handlers.onSay);
      case 'player.donation':
        return dispatch(DonationGoalSchema, eventName, data, handlers.onDonation);
      case 'player.health':
        return dispatch(StreamStateSchema, eventName, data, handlers.onHealth);
    }
  }

  function poll(): void {
    const pollOptions = options.fetchImpl ? { fetchImpl: options.fetchImpl } : {};
    getPlayerState(pollOptions)
      .then((state) => {
        if (!closed) handlers.onState?.(state);
      })
      .catch((cause) => {
        if (!closed) handlers.onPollError?.(toError(cause));
      });
  }

  function switchToPolling(): void {
    if (closed || transport === 'polling') return;
    transport = 'polling';
    connection?.close();
    connection = null;
    handlers.onTransportChange?.('polling');
    poll(); // recover state immediately, then on the interval
    pollTimer = setInterval(poll, pollIntervalMs);
  }

  function handleError(): void {
    if (closed || transport === 'polling') return;
    const stamp = now();
    disconnects.push(stamp);
    while (disconnects.length > 0 && stamp - (disconnects[0] ?? stamp) > windowMs) {
      disconnects.shift();
    }
    if (disconnects.length >= threshold) {
      switchToPolling();
    }
  }

  connection = connector({ url, onMessage: handleMessage, onError: handleError });

  return {
    close(): void {
      if (closed) return;
      closed = true;
      connection?.close();
      connection = null;
      if (pollTimer !== null) {
        clearInterval(pollTimer);
        pollTimer = null;
      }
    },
  };
}
