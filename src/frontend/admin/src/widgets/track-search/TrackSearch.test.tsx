import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import { TrackSearch } from './TrackSearch';

const tracks = [
  {
    id: '018f0aaa-0000-7000-8000-000000000020',
    title: 'Dawn signal',
    artist: 'CyberDove',
    album: 'Launch set',
    durationMs: 184000,
    hasCachedFile: true,
    coverImageUrl: '',
    metadataSource: 'filename',
  },
  {
    id: '018f0aaa-0000-7000-8000-000000000021',
    title: 'Night signal',
    artist: 'CyberDove',
    album: 'Launch set',
    durationMs: 198000,
    hasCachedFile: true,
    coverImageUrl: '',
    metadataSource: 'filename',
  },
];

function pathOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') {
    return input;
  }
  if (input instanceof URL) {
    return input.pathname + input.search;
  }
  return input.url;
}

function json(body: object, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('loads the whole library on mount and narrows it as the operator types', async () => {
  const calls: string[] = [];
  vi.stubGlobal('fetch', (input: RequestInfo | URL) => {
    const path = pathOf(input);
    calls.push(path);
    const matched = path.includes('query=Night') ? [tracks[1]] : tracks;
    return Promise.resolve(json({ items: matched, nextCursor: null }));
  });

  render(<TrackSearch renderActions={(track) => <span>action:{track.id}</span>} />);

  // List-first: full library visible before any typing.
  await screen.findByText('Dawn signal');
  expect(screen.getByText('Night signal')).toBeTruthy();
  expect(calls.some((path) => path === '/api/v0/admin/tracks?query=&limit=100')).toBe(true);

  // Typing narrows the results via a debounced server query.
  fireEvent.change(screen.getByLabelText('Track search'), { target: { value: 'Night' } });
  await waitFor(() =>
    expect(calls.some((path) => path === '/api/v0/admin/tracks?query=Night&limit=100')).toBe(true),
  );
  await waitFor(() => expect(screen.queryByText('Dawn signal')).toBeNull());
  expect(screen.getByText('Night signal')).toBeTruthy();
});

test('renders caller-supplied actions for each track', async () => {
  vi.stubGlobal('fetch', () => Promise.resolve(json({ items: tracks, nextCursor: null })));

  render(<TrackSearch renderActions={(track) => <button type="button">Queue {track.title}</button>} />);

  await screen.findByRole('button', { name: 'Queue Dawn signal' });
  expect(screen.getByRole('button', { name: 'Queue Night signal' })).toBeTruthy();
});
