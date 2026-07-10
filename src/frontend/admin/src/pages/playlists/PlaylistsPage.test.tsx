import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import { PlaylistsPage } from './PlaylistsPage';

const tracks = [
  {
    id: '018f0aaa-0000-7000-8000-000000000020',
    title: 'Dawn signal',
    artist: 'CyberDove',
    album: 'Launch set',
    durationMs: 184000,
    hasCachedFile: true,
  },
  {
    id: '018f0aaa-0000-7000-8000-000000000021',
    title: 'Night signal',
    artist: 'CyberDove',
    album: 'Launch set',
    durationMs: 198000,
    hasCachedFile: true,
  },
];

const playlist = {
  id: '018f0aaa-0000-7000-8000-000000000030',
  name: 'Launch rotation',
  description: 'Morning and night',
  isActive: true,
  itemCount: 2,
};

const items = [
  {
    id: '018f0aaa-0000-7000-8000-000000000031',
    trackId: tracks[0]!.id,
    title: tracks[0]!.title,
    artist: tracks[0]!.artist,
    position: 0,
  },
  {
    id: '018f0aaa-0000-7000-8000-000000000032',
    trackId: tracks[1]!.id,
    title: tracks[1]!.title,
    artist: tracks[1]!.artist,
    position: 1,
  },
];

interface RequestCall {
  readonly path: string;
  readonly method: string;
  readonly body: string | null;
}

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

test('searches scanned tracks and queues the selected track without waiting for a payment', async () => {
  const calls: RequestCall[] = [];
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    const path = pathOf(input);
    const method = init?.method ?? 'GET';
    calls.push({ path, method, body: typeof init?.body === 'string' ? init.body : null });
    if (path.startsWith('/api/v0/admin/tracks')) {
      return Promise.resolve(json(tracks));
    }
    if (path === '/api/v0/admin/playback/queue') {
      return Promise.resolve(json({ queueItemId: '018f0aaa-0000-7000-8000-000000000040' }, 202));
    }
    if (path === '/api/v0/admin/playlists') {
      return Promise.resolve(json([]));
    }
    return Promise.resolve(json([]));
  });

  render(<PlaylistsPage />);
  await screen.findByLabelText('Track search');
  fireEvent.change(screen.getByLabelText('Track search'), { target: { value: 'Dawn' } });
  fireEvent.click(screen.getByRole('button', { name: 'Search tracks' }));
  await screen.findByText('Dawn signal');
  fireEvent.click(screen.getByRole('button', { name: 'Queue Dawn signal now' }));

  await waitFor(() => expect(screen.getByText('Queued')).toBeTruthy());
  expect(calls.some((call) => call.path === '/api/v0/admin/tracks?query=Dawn&limit=100')).toBe(true);
  const enqueue = calls.find((call) => call.path === '/api/v0/admin/playback/queue');
  expect(enqueue?.body).toBe(JSON.stringify({ trackId: tracks[0]!.id }));
});

test('creates and edits the active playlist with its pinned request shape', async () => {
  const calls: RequestCall[] = [];
  const edited = { ...playlist, name: 'Updated rotation', description: null, isActive: false };
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    const path = pathOf(input);
    const method = init?.method ?? 'GET';
    calls.push({ path, method, body: typeof init?.body === 'string' ? init.body : null });
    if (path === '/api/v0/admin/playlists' && method === 'GET') {
      return Promise.resolve(json([]));
    }
    if (path === '/api/v0/admin/playlists' && method === 'POST') {
      return Promise.resolve(json(playlist, 201));
    }
    if (path === `/api/v0/admin/playlists/${playlist.id}` && method === 'PUT') {
      return Promise.resolve(json(edited));
    }
    if (path === `/api/v0/admin/playlists/${playlist.id}/items`) {
      return Promise.resolve(json([]));
    }
    return Promise.resolve(json(tracks));
  });

  render(<PlaylistsPage />);
  await screen.findByRole('button', { name: 'Create playlist' });
  fireEvent.change(screen.getByLabelText('Playlist name'), { target: { value: 'Launch rotation' } });
  fireEvent.change(screen.getByLabelText('Playlist description'), { target: { value: 'Morning and night' } });
  fireEvent.click(screen.getByLabelText('Active playlist'));
  fireEvent.click(screen.getByRole('button', { name: 'Create playlist' }));

  await screen.findByDisplayValue('Launch rotation');
  await screen.findByText('No tracks in this playlist yet.');
  fireEvent.change(screen.getByLabelText('Playlist name'), { target: { value: 'Updated rotation' } });
  fireEvent.change(screen.getByLabelText('Playlist description'), { target: { value: '' } });
  await waitFor(() => expect(screen.getByDisplayValue('Updated rotation')).toBeTruthy());
  fireEvent.click(screen.getByLabelText('Active playlist'));
  fireEvent.click(screen.getByRole('button', { name: 'Save playlist' }));

  await waitFor(() => expect(screen.getByText('Saved')).toBeTruthy());
  const create = calls.find((call) => call.path === '/api/v0/admin/playlists' && call.method === 'POST');
  expect(create?.body).toBe(
    JSON.stringify({ name: 'Launch rotation', description: 'Morning and night', isActive: true }),
  );
  const update = calls.find((call) => call.path === `/api/v0/admin/playlists/${playlist.id}` && call.method === 'PUT');
  expect(update?.body).toBe(
    JSON.stringify({ name: 'Updated rotation', description: null, isActive: false }),
  );
});

test('reorders and removes playlist items by replacing the remaining ordered collection', async () => {
  const calls: RequestCall[] = [];
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    const path = pathOf(input);
    const method = init?.method ?? 'GET';
    calls.push({ path, method, body: typeof init?.body === 'string' ? init.body : null });
    if (path === '/api/v0/admin/playlists') {
      return Promise.resolve(json([playlist]));
    }
    if (path === `/api/v0/admin/playlists/${playlist.id}/items` && method === 'GET') {
      return Promise.resolve(json(items));
    }
    if (path === `/api/v0/admin/playlists/${playlist.id}/items` && method === 'PUT') {
      return Promise.resolve(json([items[1]]));
    }
    return Promise.resolve(json(tracks));
  });

  render(<PlaylistsPage />);
  await screen.findByRole('button', { name: 'Move Night signal up' });
  fireEvent.click(screen.getByRole('button', { name: 'Move Night signal up' }));
  fireEvent.click(screen.getByRole('button', { name: 'Remove Dawn signal' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save playlist items' }));

  await waitFor(() => expect(screen.queryByText('Dawn signal')).toBeNull());
  const replacement = calls.find(
    (call) => call.path === `/api/v0/admin/playlists/${playlist.id}/items` && call.method === 'PUT',
  );
  expect(replacement?.body).toBe(
    JSON.stringify({ items: [{ id: items[1]!.id, trackId: tracks[1]!.id }] }),
  );
});
