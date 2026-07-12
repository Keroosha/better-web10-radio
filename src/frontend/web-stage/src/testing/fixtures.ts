// Test-only fixtures for web-stage. Imported solely by `*.test.tsx?` files, so it is
// tree-shaken out of the production bundle; `tsc` still type-checks it. Typed as the
// shared domain types so it fails to compile if a schema drifts. (The shared workspace
// has an equivalent `testing/fixtures.ts`, but its package `exports` map only exposes
// `.`, so it cannot be reached from here — hence this local copy.)
import type { PlayerState, RecentDonation, SuperChatMessage } from '@web10/shared';

/** A fully-populated, valid player snapshot (mirrors the SPEC §5 example). */
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
      coverImageUrl: '/api/v0/player/assets/cover/1',
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
      recent: [donation('01920000-0000-7000-8000-000000000020', 'neonghost', 25)],
    },
    superChat: {
      messages: [message('01920000-0000-7000-8000-000000000030', 'vhs_wanderer', 'approved')],
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
      {
        id: '01920000-0000-7000-8000-000000000041',
        kind: 'youtube',
        name: 'YouTube',
        handle: '@web10radio',
        url: 'https://youtube.com/@web10radio',
        glyph: 'Y',
        color: '#ff0000',
        qrImageUrl: '/api/v0/player/assets/social-qr-yt',
        isFeatured: false,
      },
    ],
    overlay: { style: 'aero', layout: 'corners' },
    banners: [
      {
        id: '01920000-0000-7000-8000-0000000000c1',
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
        id: '01920000-0000-7000-8000-0000000000c2',
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
        id: '01920000-0000-7000-8000-0000000000c3',
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
      {
        id: '01920000-0000-7000-8000-0000000000c4',
        type: 'custom',
        title: 'GIVEAWAY',
        subtitle: '',
        text: 'Type /join in chat',
        style: 'win9x',
        screenPosition: 'bottom-center',
        accent: '#f39c12',
        enabled: false,
        sortOrder: 3,
        rotationSeconds: 0,
      },
    ],
    playbackState: 'playing',
  };
}

/** A `RecentDonation` builder for donation/toast tests. */
export function donation(id: string, displayName: string, amountStars: number): RecentDonation {
  return { id, displayName, amountStars, paidAtUtc: '2026-07-07T00:00:00Z' };
}

/** A `SuperChatMessage` builder; status defaults let tests exercise the approved-only filter. */
export function message(
  id: string,
  displayName: string,
  status: SuperChatMessage['status'],
): SuperChatMessage {
  return {
    id,
    displayName,
    text: `${displayName} says hi`,
    amountStars: 100,
    color: '#e0439a',
    submittedAtUtc: '2026-07-07T00:00:00Z',
    status,
  };
}
