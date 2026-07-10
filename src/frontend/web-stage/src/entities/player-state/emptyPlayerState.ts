import type { PlayerState } from '@web10/shared';

/**
 * The initial client-side snapshot rendered before `getPlayerState` seeds, and the
 * baseline the stage must always render (SPEC §10/§12): stream offline, every
 * collection empty, no top donator. This mirrors the real backend's offline
 * `/api/v0/player/state` response (verified live). Typed as `PlayerState` so it fails
 * to compile if the shared schema ever drifts.
 *
 * `shared/src/testing/fixtures.ts` has an equivalent builder but is build-excluded and
 * not re-exported, so web-stage needs its own runtime default here.
 */
export function createEmptyPlayerState(): PlayerState {
  return {
    serverTimeUtc: '',
    stream: {
      status: 'offline',
      publicAudioUrl: '/api/v0/player/stream',
      rtmpRelay: 'telegram',
      bitrateKbps: 0,
      startedAtUtc: '',
      offlineReason: 'connecting',
    },
    nowPlaying: {
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
    },
    queue: { currentQueueItemId: '', items: [] },
    donationGoal: { title: '', raisedStars: 0, goalStars: 0, topDonator: null, recent: [] },
    superChat: { messages: [] },
    socials: [],
    overlay: { style: 'aero', layout: 'corners' },
  };
}
