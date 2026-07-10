import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import { StoragePage } from './StoragePage';

const storage = {
  defaultBackend: {
    type: 'local',
    localRoot: '/var/lib/web10/music',
    s3Bucket: null,
    s3Region: null,
    s3ServiceUrl: null,
    s3ForcePathStyle: false,
  },
  additionalBackends: [],
};

interface RequestCall {
  readonly method: string;
  readonly body: string | null;
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


test('rejects a Local backend without an absolute root before replacing storage', async () => {
  const calls: RequestCall[] = [];
  vi.stubGlobal('fetch', (_input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : null,
    });
    return Promise.resolve(json(storage));
  });

  render(<StoragePage />);
  await screen.findByRole('button', { name: 'Add backend' });
  fireEvent.click(screen.getByRole('button', { name: 'Add backend' }));
  fireEvent.change(screen.getByLabelText('Backend type for new backend'), { target: { value: 'local' } });
  fireEvent.change(screen.getByLabelText('Local root for new backend'), { target: { value: 'relative/music' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save storage backends' }));

  expect(screen.getByRole('alert').textContent).toContain('absolute');
  expect(calls.filter((call) => call.method === 'PUT')).toHaveLength(0);
});

test('rejects an S3 backend without a bucket before replacing storage', async () => {
  const calls: RequestCall[] = [];
  vi.stubGlobal('fetch', (_input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : null,
    });
    return Promise.resolve(json(storage));
  });

  render(<StoragePage />);
  await screen.findByRole('button', { name: 'Add backend' });
  fireEvent.click(screen.getByRole('button', { name: 'Add backend' }));
  fireEvent.change(screen.getByLabelText('Backend type for new backend'), { target: { value: 's3' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save storage backends' }));

  expect(screen.getByRole('alert').textContent).toContain('bucket');
  expect(calls.filter((call) => call.method === 'PUT')).toHaveLength(0);
});

test('replaces additional Local and S3 backends with the operator-selected state', async () => {
  const calls: RequestCall[] = [];
  const additionalBackends = [
    {
      id: '018f0aaa-0000-7000-8000-000000000050',
      name: 'Local archive',
      type: 'local',
      localRoot: '/mnt/archive',
      s3Bucket: null,
      isEnabled: true,
    },
    {
      id: '018f0aaa-0000-7000-8000-000000000051',
      name: 'Cloud archive',
      type: 's3',
      localRoot: null,
      s3Bucket: 'web10-archive',
      isEnabled: false,
    },
  ];
  vi.stubGlobal('fetch', (_input: RequestInfo | URL, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    calls.push({ method, body: typeof init?.body === 'string' ? init.body : null });
    return Promise.resolve(method === 'PUT' ? json({ ...storage, additionalBackends }) : json(storage));
  });

  render(<StoragePage />);
  await screen.findByRole('button', { name: 'Add backend' });
  fireEvent.click(screen.getByRole('button', { name: 'Add backend' }));
  fireEvent.change(screen.getByLabelText('Name for new backend'), { target: { value: 'Local archive' } });
  fireEvent.change(screen.getByLabelText('Local root for new backend'), { target: { value: '/mnt/archive' } });
  fireEvent.click(screen.getByLabelText('Enabled for Local archive'));
  fireEvent.click(screen.getByRole('button', { name: 'Add backend' }));
  fireEvent.change(screen.getByLabelText('Name for new backend'), { target: { value: 'Cloud archive' } });
  fireEvent.change(screen.getByLabelText('Backend type for new backend'), { target: { value: 's3' } });
  fireEvent.change(screen.getByLabelText('S3 bucket for new backend'), { target: { value: 'web10-archive' } });
  fireEvent.click(screen.getByLabelText('Enabled for Cloud archive'));
  fireEvent.click(screen.getByRole('button', { name: 'Save storage backends' }));

  await waitFor(() => expect(screen.getByText('Saved')).toBeTruthy());
  const replacement = calls.find((call) => call.method === 'PUT');
  expect(replacement?.body).toBe(
    JSON.stringify({
      additionalBackends: [
        {
          id: null,
          name: 'Local archive',
          type: 'local',
          localRoot: '/mnt/archive',
          s3Bucket: null,
          isEnabled: false,
        },
        {
          id: null,
          name: 'Cloud archive',
          type: 's3',
          localRoot: null,
          s3Bucket: 'web10-archive',
          isEnabled: false,
        },
      ],
    }),
  );
});
