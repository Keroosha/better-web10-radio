import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import { LibraryScanPage } from './LibraryScanPage';

const storage = {
  defaultBackend: {
    type: 'local',
    localRoot: '/var/lib/web10/music',
    s3Bucket: null,
    s3Region: null,
    s3ServiceUrl: null,
    s3ForcePathStyle: false,
  },
  additionalBackends: [
    {
      id: '018f0aaa-0000-7000-8000-000000000003',
      name: 'Archive',
      type: 's3',
      localRoot: null,
      s3Bucket: 'web10-archive',
      isEnabled: true,
    },
  ],
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

test('polls a started scan to completion and renders the discovered track count', async () => {
  vi.useFakeTimers();
  const calls: RequestCall[] = [];
  let scanReads = 0;
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    const path = pathOf(input);
    const method = init?.method ?? 'GET';
    calls.push({ path, method, body: typeof init?.body === 'string' ? init.body : null });
    if (path === '/api/v0/admin/storage') {
      return Promise.resolve(json(storage));
    }
    if (path === '/api/v0/admin/library/scan' && method === 'POST') {
      return Promise.resolve(json({ scanJobId: '018f0aaa-0000-7000-8000-000000000010' }, 202));
    }
    scanReads += 1;
    return Promise.resolve(
      json({
        scanJobId: '018f0aaa-0000-7000-8000-000000000010',
        status: scanReads === 1 ? 'running' : 'completed',
        discoveredCount: 12,
        requestedAtUtc: '2026-07-10T12:00:00Z',
        startedAtUtc: '2026-07-10T12:00:01Z',
        finishedAtUtc: scanReads === 1 ? null : '2026-07-10T12:00:02Z',
        failureReason: null,
      }),
    );
  });

  render(<LibraryScanPage />);
  await act(async () => {
    await Promise.resolve();
  });
  expect(screen.getByRole('button', { name: 'Start scan' })).toBeTruthy();
  fireEvent.change(screen.getByLabelText('Storage backend'), {
    target: { value: '018f0aaa-0000-7000-8000-000000000003' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Start scan' }));
  await act(async () => {
    await Promise.resolve();
    await vi.advanceTimersByTimeAsync(2000);
  });

  expect(screen.getByText('12 tracks found')).toBeTruthy();
  const create = calls.find((call) => call.path === '/api/v0/admin/library/scan' && call.method === 'POST');
  expect(create?.body).toBe(JSON.stringify({ storageBackendId: '018f0aaa-0000-7000-8000-000000000003' }));
});

test('shows the scan failure reason when polling reaches a failed terminal state', async () => {
  vi.useFakeTimers();
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    const path = pathOf(input);
    if (path === '/api/v0/admin/storage') {
      return Promise.resolve(json(storage));
    }
    if ((init?.method ?? 'GET') === 'POST') {
      return Promise.resolve(json({ scanJobId: '018f0aaa-0000-7000-8000-000000000011' }, 202));
    }
    return Promise.resolve(
      json({
        scanJobId: '018f0aaa-0000-7000-8000-000000000011',
        status: 'failed',
        discoveredCount: 3,
        requestedAtUtc: '2026-07-10T12:00:00Z',
        startedAtUtc: '2026-07-10T12:00:01Z',
        finishedAtUtc: '2026-07-10T12:00:02Z',
        failureReason: 'Storage mount is unavailable',
      }),
    );
  });

  render(<LibraryScanPage />);
  await act(async () => {
    await Promise.resolve();
  });
  expect(screen.getByRole('button', { name: 'Start scan' })).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Start scan' }));
  await act(async () => {
    await Promise.resolve();
    await vi.advanceTimersByTimeAsync(1000);
  });
  expect(screen.getByRole('alert').textContent).toContain('Storage mount is unavailable');
});
