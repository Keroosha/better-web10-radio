import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import { StreamNodePage } from './StreamNodePage';

const offline = {
  status: 'offline',
  desiredState: 'stopped',
  lastHeartbeatUtc: null,
  failureReason: null,
  bitrateKbps: 0,
  restartGeneration: 4,
};

const live = {
  status: 'live',
  desiredState: 'running',
  lastHeartbeatUtc: '2026-07-10T12:00:00Z',
  failureReason: null,
  bitrateKbps: 192,
  restartGeneration: 5,
};

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
    return input.pathname;
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
  vi.useRealTimers();
});

test('polls stream-node status and replaces an offline state with a live heartbeat', async () => {
  vi.useFakeTimers();
  let statusReads = 0;
  vi.stubGlobal('fetch', (input: RequestInfo | URL) => {
    if (pathOf(input) === '/api/v0/admin/stream-node/status') {
      statusReads += 1;
      return Promise.resolve(json(statusReads === 1 ? offline : live));
    }
    return Promise.resolve(json(live));
  });

  render(<StreamNodePage />);
  await act(async () => {
    await Promise.resolve();
  });
  expect(screen.getByText('offline')).toBeTruthy();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1000);
  });

  expect(screen.getByText('live')).toBeTruthy();
  expect(screen.getByText('192 kbps')).toBeTruthy();
});

test('sends exact empty control bodies for start, stop, and restart and renders the returned generation', async () => {
  const calls: RequestCall[] = [];
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    const path = pathOf(input);
    const method = init?.method ?? 'GET';
    calls.push({ path, method, body: typeof init?.body === 'string' ? init.body : null });
    if (path === '/api/v0/admin/stream-node/status') {
      return Promise.resolve(json(offline));
    }
    if (path.endsWith('/start')) {
      return Promise.resolve(json({ desiredState: 'running', restartGeneration: 4 }, 202));
    }
    if (path.endsWith('/stop')) {
      return Promise.resolve(json({ desiredState: 'stopped', restartGeneration: 4 }, 202));
    }
    return Promise.resolve(json({ desiredState: 'running', restartGeneration: 5 }, 202));
  });

  render(<StreamNodePage />);
  await screen.findByRole('button', { name: 'Start' });
  fireEvent.click(screen.getByRole('button', { name: 'Start' }));
  await screen.findByText('running');
  fireEvent.click(screen.getByRole('button', { name: 'Stop' }));
  await screen.findByText('stopped');
  fireEvent.click(screen.getByRole('button', { name: 'Restart' }));

  await waitFor(() => expect(screen.getByText('Generation 5')).toBeTruthy());
  for (const endpoint of ['start', 'stop', 'restart']) {
    const call = calls.find((candidate) => candidate.path === `/api/v0/admin/stream-node/${endpoint}`);
    expect(call?.method).toBe('POST');
    expect(call?.body).toBe('{}');
  }
});

test('surfaces a status loading failure instead of presenting stale controls as healthy', async () => {
  vi.stubGlobal('fetch', () =>
    Promise.resolve(
      json({ status: 503, code: 'repository.read_failed', message: 'Status source unavailable' }, 503),
    ),
  );

  render(<StreamNodePage />);

  await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('repository.read_failed'));
});
