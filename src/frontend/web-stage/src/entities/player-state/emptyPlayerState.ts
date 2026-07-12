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
    // The default overlay banners the seeded backend always returns, so the
    // offline/pre-seed baseline still shows NOW PLAYING + DONATION GOAL (+ FOLLOW US
    // once socials exist), matching the always-on panels the stage had before banners.
    banners: [
      {
        id: '01920000-0000-7000-8000-0000000000d1',
        type: 'nowplaying',
        title: 'NOW PLAYING',
        subtitle: '24/7',
        text: '',
        style: 'aero',
        screenPosition: 'top-center',
        accent: '#e74c3c',
        enabled: true,
        sortOrder: 0,
        rotationSeconds: 0,
      },
      {
        id: '01920000-0000-7000-8000-0000000000d2',
        type: 'donation',
        title: 'DONATION GOAL',
        subtitle: '',
        text: '',
        style: 'aero',
        screenPosition: 'top-left',
        accent: '#2ecc71',
        enabled: true,
        sortOrder: 1,
        rotationSeconds: 0,
      },
      {
        id: '01920000-0000-7000-8000-0000000000d3',
        type: 'social',
        title: 'FOLLOW US',
        subtitle: '@web1.radio',
        text: '',
        style: 'aero',
        screenPosition: 'bottom-right',
        accent: '#c0392b',
        enabled: true,
        sortOrder: 2,
        rotationSeconds: 5,
      },
    ],
    playbackState: 'playing',
  };
}
