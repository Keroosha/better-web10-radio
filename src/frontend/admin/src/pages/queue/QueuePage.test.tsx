import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import type { FetchImpl, PlayerEventName, SseConnector, SseConnectorSpec } from '@web10/shared';

import { QueuePage } from './QueuePage';

const livePlayerState = {
  serverTimeUtc: '2026-07-10T12:00:00Z',
  stream: {
    status: 'live',
    publicAudioUrl: '/api/v0/player/stream',
    rtmpRelay: 'telegram',
    bitrateKbps: 192,
    startedAtUtc: '2026-07-10T11:59:00Z',
    offlineReason: null,
  },
  nowPlaying: {
    trackId: '018f0aaa-0000-7000-8000-000000000001',
    title: 'Live intro',
    artist: 'DJ Test',
    album: 'Broadcast set',
    source: 'library',
    externalUrl: '',
    coverImageUrl: '',
    durationMs: 200000,
    positionMs: 42000,
    startedAtUtc: '2026-07-10T11:59:18Z',
  },
  queue: {
    currentQueueItemId: '018f0aaa-0000-7000-8000-000000000010',
    items: [
      {
        queueItemId: '018f0aaa-0000-7000-8000-000000000010',
        trackId: '018f0aaa-0000-7000-8000-000000000020',
        title: 'Dawn signal',
        artist: 'CyberDove',
        source: 'playlist',
        status: 'queued',
      },
      {
        queueItemId: '018f0aaa-0000-7000-8000-000000000011',
        trackId: '018f0aaa-0000-7000-8000-000000000021',
        title: 'Night signal',
        artist: 'CyberDove',
        source: 'request',
        status: 'queued',
      },
    ],
  },
  donationGoal: { title: 'Launch', raisedStars: 0, goalStars: 5000, topDonator: null, recent: [] },
  superChat: { messages: [] },
  socials: [],
  overlay: { style: 'aero', layout: 'corners' },
};

const failingFetch: FetchImpl = () => Promise.reject(new Error('offline'));

function makeConnector(): { connector: SseConnector; emit: (name: PlayerEventName, data: object) => void } {
  let spec: SseConnectorSpec | null = null;
  const connector: SseConnector = (received) => {
    spec = received;
    return { close: () => undefined };
  };
  const emit = (name: PlayerEventName, data: object): void => {
    spec?.onMessage(name, JSON.stringify(data));
  };
  return { connector, emit };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('renders an empty, offline queue before any state arrives', async () => {
  const { connector } = makeConnector();

  render(<QueuePage options={{ connector, fetchImpl: failingFetch }} />);

  await screen.findByText('The queue is empty.');
  expect(screen.getByText('offline')).toBeTruthy();
  expect(screen.getByText('Nothing is playing right now.')).toBeTruthy();
});

test('renders now-playing and the live queue from a player.state event', async () => {
  const { connector, emit } = makeConnector();

  render(<QueuePage options={{ connector, fetchImpl: failingFetch }} />);
  await screen.findByText('Nothing is playing right now.');

  act(() => emit('player.state', livePlayerState));

  await screen.findByText('DJ Test — Live intro');
  expect(screen.getByText('live')).toBeTruthy();
  expect(screen.getByText('Dawn signal')).toBeTruthy();
  expect(screen.getByText('Night signal')).toBeTruthy();
});
