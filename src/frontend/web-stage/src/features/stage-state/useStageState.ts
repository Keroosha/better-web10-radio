import { useEffect, useReducer, useRef, useState } from 'react';

import {
  createPlayerEventsClient,
  getPlayerState,
  type FetchImpl,
  type PlayerEventsTransport,
  type PlayerState,
  type RecentDonation,
  type SseConnector,
} from '@web10/shared';

import {
  createEmptyPlayerState,
  detectNewDonation,
  reducePlayerState,
} from '../../entities/player-state';

export interface UseStageStateOptions {
  /** SSE transport factory; injected in tests. Defaults to the native EventSource adapter. */
  readonly connector?: SseConnector;
  /** `fetch` for the seed + polling fallback; injected in tests. */
  readonly fetchImpl?: FetchImpl;
  /** Clock for the fallback window; injected in tests. */
  readonly now?: () => number;
}

export interface StageStateValue {
  readonly state: PlayerState;
  /** Which transport is live: `sse` or the polling fallback. */
  readonly transport: PlayerEventsTransport;
  /** The newest unseen donation, or `null`. Feeds the donation toast. */
  readonly newDonation: RecentDonation | null;
}

/**
 * The single live data source for the stage. Renders `createEmptyPlayerState()`
 * immediately (offline/empty ⇒ the empty-render invariant holds on first paint), seeds
 * from `GET /api/v0/player/state`, then merges the five SSE deltas via `reducePlayerState`.
 *
 * Donation toasts fire ONLY from the `player.donation` event and only for ids not already
 * seen; the seed and every full snapshot (including 5s polling-fallback snapshots) fold
 * their donation ids into `seenDonationIds` silently, so reconnects/polls never re-toast.
 *
 * The `createPlayerEventsClient` handle owns the SSE→polling fallback and all timers; this
 * hook just tears it down (and aborts the seed) on unmount.
 */
export function useStageState(options: UseStageStateOptions = {}): StageStateValue {
  const { connector, fetchImpl, now } = options;
  const [state, dispatch] = useReducer(reducePlayerState, undefined, createEmptyPlayerState);
  const [transport, setTransport] = useState<PlayerEventsTransport>('sse');
  const [newDonation, setNewDonation] = useState<RecentDonation | null>(null);
  const seenDonationIds = useRef<Set<string>>(new Set());

  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();

    const remember = (recent: readonly RecentDonation[]): void => {
      for (const donation of recent) {
        seenDonationIds.current.add(donation.id);
      }
    };

    // 1) Seed the snapshot without toasting existing donations.
    const seedOptions = fetchImpl
      ? { fetchImpl, signal: controller.signal }
      : { signal: controller.signal };
    getPlayerState(seedOptions)
      .then((snapshot) => {
        if (cancelled) {
          return;
        }
        remember(snapshot.donationGoal.recent);
        dispatch({ type: 'snapshot', state: snapshot });
      })
      .catch(() => {
        // Offline seed: the empty state is already rendered and SSE/polling will recover.
      });

    // 2) Subscribe. The client validates every payload and applies the mandated fallback.
    const clientOptions = {
      ...(connector ? { connector } : {}),
      ...(fetchImpl ? { fetchImpl } : {}),
      ...(now ? { now } : {}),
    };
    const client = createPlayerEventsClient(
      {
        onState: (snapshot) => {
          remember(snapshot.donationGoal.recent);
          dispatch({ type: 'snapshot', state: snapshot });
        },
        onQueue: (queue) => dispatch({ type: 'queue', queue }),
        onSay: (superChat) => dispatch({ type: 'say', superChat }),
        onDonation: (donationGoal) => {
          const fresh = detectNewDonation(seenDonationIds.current, donationGoal.recent);
          if (fresh !== null) {
            setNewDonation(fresh);
          }
          remember(donationGoal.recent);
          dispatch({ type: 'donation', donationGoal });
        },
        onHealth: (stream) => dispatch({ type: 'health', stream }),
        onTransportChange: (next) => setTransport(next),
        onParseError: (name, error) => {
          if (import.meta.env.DEV) {
            console.warn('[stage] SSE parse error', name, error);
          }
        },
        onPollError: (error) => {
          if (import.meta.env.DEV) {
            console.warn('[stage] poll error', error);
          }
        },
      },
      clientOptions,
    );

    return () => {
      cancelled = true;
      controller.abort();
      client.close();
    };
  }, [connector, fetchImpl, now]);

  return { state, transport, newDonation };
}
