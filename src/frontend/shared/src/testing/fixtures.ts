// Typed test fixtures. Not part of the public surface (not re-exported from
// index.ts) and excluded from the build. Typing the builders as domain types means
// the fixtures fail to compile if they ever drift from the schemas.
import type { PlayerState } from '../domain/player-state';

/** A fully-populated, valid player snapshot mirroring the SPEC §5 example. */
export function validPlayerState(): PlayerState {
  return {
    serverTimeUtc: '2026-07-07T00:00:00Z',
    stream: {
      status: 'live',
      publicAudioUrl: '/api/v0/player/stream',
      rtmpRelay: 'telegram',
      bitrateKbps: 192,
      startedAtUtc: '2026-07-07T00:00:00Z',
      offlineReason: null,
    },
    nowPlaying: {
      trackId: '01920000-0000-7000-8000-000000000001',
      title: 'リサフランク420 / 現代のコンピュー',
      artist: 'Macintosh Plus',
      album: 'FLORAL SHOPPE',
      source: 'library',
      externalUrl: 'https://bandcamp.com/track/example',
      coverImageUrl: '/api/v0/player/assets/cover/01920000-0000-7000-8000-000000000001',
      durationMs: 240000,
      positionMs: 42000,
      startedAtUtc: '2026-07-07T00:00:00Z',
    },
    queue: {
      currentQueueItemId: '01920000-0000-7000-8000-000000000010',
      items: [
        {
          queueItemId: '01920000-0000-7000-8000-000000000010',
          trackId: '01920000-0000-7000-8000-000000000002',
          title: 'Track title',
          artist: 'Artist',
          source: 'playlist',
          status: 'playing',
        },
      ],
    },
    donationGoal: {
      title: 'Цель сбора',
      raisedStars: 3820,
      goalStars: 5000,
      topDonator: { displayName: 'CyberDove', amountStars: 500 },
      recent: [
        {
          id: '01920000-0000-7000-8000-000000000020',
          displayName: 'neonghost',
          amountStars: 25,
          paidAtUtc: '2026-07-07T00:00:00Z',
        },
      ],
    },
    superChat: {
      messages: [
        {
          id: '01920000-0000-7000-8000-000000000030',
          displayName: 'vhs_wanderer',
          text: 'this station literally saved my night shift',
          amountStars: 100,
          color: '#e0439a',
          submittedAtUtc: '2026-07-07T00:00:00Z',
          status: 'approved',
        },
      ],
    },
    socials: [
      {
        id: '01920000-0000-7000-8000-000000000040',
        kind: 'telegram',
        name: 'Telegram',
        handle: '@netscapedidnothingwrong',
        url: 'https://t.me/netscapedidnothingwrong',
        glyph: 'T',
        color: '#2aabee',
        qrImageUrl: '/api/v0/player/assets/social-qr',
        isFeatured: true,
      },
    ],
    overlay: { style: 'aero', layout: 'corners' },
    banners: [
      {
        id: '01920000-0000-7000-8000-0000000000b1',
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
    ],
    playbackState: 'playing',
  };
}

/**
 * The empty/offline snapshot the stage must still render (SPEC §10/§12): stream
 * offline, every collection empty, no top donator.
 */
export function emptyPlayerState(): PlayerState {
  return {
    serverTimeUtc: '2026-07-07T00:00:00Z',
    stream: {
      status: 'offline',
      publicAudioUrl: '/api/v0/player/stream',
      rtmpRelay: 'telegram',
      bitrateKbps: 0,
      startedAtUtc: '2026-07-07T00:00:00Z',
      offlineReason: 'stream-node not connected',
    },
    nowPlaying: {
      trackId: '01920000-0000-7000-8000-0000000000ff',
      title: '',
      artist: '',
      album: '',
      source: 'fallback',
      externalUrl: '',
      coverImageUrl: '',
      durationMs: 0,
      positionMs: 0,
      startedAtUtc: '2026-07-07T00:00:00Z',
    },
    queue: { currentQueueItemId: '', items: [] },
    donationGoal: {
      title: 'Цель сбора',
      raisedStars: 0,
      goalStars: 5000,
      topDonator: null,
      recent: [],
    },
    superChat: { messages: [] },
    socials: [],
    overlay: { style: 'aero', layout: 'corners' },
    banners: [],
    playbackState: 'playing',
  };
}
