import { useEffect, useState } from 'react';

import {
  createPlayerEventsClient,
  getPlayerState,
  type FetchImpl,
  type NowPlaying,
  type PlayerEventsTransport,
  type QueueState,
  type SseConnector,
  type StreamStatus,
} from '@web10/shared';

export interface UseLiveQueueOptions {
  /** SSE transport factory; injected in tests. Defaults to the native EventSource adapter. */
  readonly connector?: SseConnector;
  /** `fetch` for the seed + polling fallback; injected in tests. */
  readonly fetchImpl?: FetchImpl;
  /** Clock for the fallback window; injected in tests. */
  readonly now?: () => number;
}

export interface LiveQueueValue {
  readonly nowPlaying: NowPlaying;
  readonly queue: QueueState;
  readonly streamStatus: StreamStatus;
  /** Which transport is live: `sse` or the polling fallback. */
  readonly transport: PlayerEventsTransport;
}

const EMPTY_NOW_PLAYING: NowPlaying = {
  trackId: '',
  title: '',
  artist: '',
  album: '',
  source: 'fallback',
  externalUrl: '',
  coverImageUrl: '',
  durationMs: 0,
  positionMs: 0,
  startedAtUtc: '',
};

const EMPTY_QUEUE: QueueState = { currentQueueItemId: '', items: [] };

/**
 * Live now-playing + playback queue for the admin cabinet. Renders empty/offline
 * defaults immediately (empty-state invariant holds on first paint), seeds from
 * `GET /api/v0/player/state`, then applies the public SSE deltas. Reuses the shared
 * `createPlayerEventsClient` handle, which owns the SSE→polling fallback and timers;
 * this hook just tears it down (and aborts the seed) on unmount.
 */
export function useLiveQueue(options: UseLiveQueueOptions = {}): LiveQueueValue {
  const { connector, fetchImpl, now } = options;
  const [nowPlaying, setNowPlaying] = useState<NowPlaying>(EMPTY_NOW_PLAYING);
  const [queue, setQueue] = useState<QueueState>(EMPTY_QUEUE);
  const [streamStatus, setStreamStatus] = useState<StreamStatus>('offline');
  const [transport, setTransport] = useState<PlayerEventsTransport>('sse');

  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();

    const seedOptions = fetchImpl
      ? { fetchImpl, signal: controller.signal }
      : { signal: controller.signal };
    getPlayerState(seedOptions)
      .then((snapshot) => {
        if (cancelled) {
          return;
        }
        setNowPlaying(snapshot.nowPlaying);
        setQueue(snapshot.queue);
        setStreamStatus(snapshot.stream.status);
      })
      .catch(() => {
        // Offline seed: empty defaults already render; SSE/polling will recover.
      });

    const clientOptions = {
      ...(connector ? { connector } : {}),
      ...(fetchImpl ? { fetchImpl } : {}),
      ...(now ? { now } : {}),
    };
    const client = createPlayerEventsClient(
      {
        onState: (snapshot) => {
          setNowPlaying(snapshot.nowPlaying);
          setQueue(snapshot.queue);
          setStreamStatus(snapshot.stream.status);
        },
        onQueue: (next) => setQueue(next),
        onHealth: (stream) => setStreamStatus(stream.status),
        onTransportChange: (next) => setTransport(next),
      },
      clientOptions,
    );

    return () => {
      cancelled = true;
      controller.abort();
      client.close();
    };
  }, [connector, fetchImpl, now]);

  return { nowPlaying, queue, streamStatus, transport };
}
