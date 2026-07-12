import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import { App } from './App';

const session = {
  username: 'cabinet-admin',
  csrfToken: 'csrf-token',
  developmentFixturesEnabled: false,
};

const playerState = {
  stream: { status: 'offline', listeners: 0, updatedAtUtc: '2026-07-10T12:00:00Z' },
  nowPlaying: {
    title: '',
    artist: '',
    startedAtUtc: null,
    durationMs: 0,
    progressMs: 0,
    source: 'auto',
  },
  queue: { items: [] },
  donationGoal: { title: 'Launch', raisedStars: 0, goalStars: 5000, topDonator: null, recent: [] },
  superChat: { messages: [] },
  socials: [],
  overlay: { style: 'aero', layout: 'corners' },
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

function bodyOf(init: RequestInit | undefined): string | null {
  return typeof init?.body === 'string' ? init.body : null;
}

function json(body: object, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function problem(status: number, code: string, message: string): Response {
  return json({ status, code, message, traceId: 'trace-admin' }, status);
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('restores an active server session before rendering the admin shell', async () => {
  const calls: RequestCall[] = [];
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    const path = pathOf(input);
    calls.push({ path, method: init?.method ?? 'GET', body: bodyOf(init) });
    if (path === '/api/v0/admin/auth/session') {
      return Promise.resolve(json(session));
    }
    return Promise.resolve(json(playerState));
  });

  render(<App />);

  await waitFor(() => expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeTruthy());
  expect(calls[0]?.path).toBe('/api/v0/admin/auth/session');
});

test('shows the credential gate when the initial session probe is unauthorized', async () => {
  vi.stubGlobal('fetch', (input: RequestInfo | URL) => {
    return Promise.resolve(
      pathOf(input) === '/api/v0/admin/auth/session'
        ? problem(401, 'admin.auth.required', 'Sign in is required')
        : json(playerState),
    );
  });

  render(<App />);

  await waitFor(() => expect(screen.getByLabelText('Username')).toBeTruthy());
  expect(screen.getByLabelText('Password')).toBeTruthy();
  expect(screen.queryByRole('heading', { name: 'Dashboard' })).toBeNull();
});

test('surfaces the server login error instead of opening the shell', async () => {
  vi.stubGlobal('fetch', (input: RequestInfo | URL) => {
    const path = pathOf(input);
    if (path === '/api/v0/admin/auth/session') {
      return Promise.resolve(problem(401, 'admin.auth.required', 'Sign in is required'));
    }
    if (path === '/api/v0/admin/auth/login') {
      return Promise.resolve(problem(401, 'admin.auth.invalid_credentials', 'Invalid credentials'));
    }
    return Promise.resolve(json(playerState));
  });

  render(<App />);
  await waitFor(() => expect(screen.getByLabelText('Username')).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'cabinet-admin' } });
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'wrong-password' } });
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

  await waitFor(() =>
    expect(screen.getByRole('alert').textContent).toContain('admin.auth.invalid_credentials'),
  );
  expect(screen.queryByRole('heading', { name: 'Dashboard' })).toBeNull();
});

test('re-probes the server session after a reload and restores the cabinet again', async () => {
  let sessionProbes = 0;
  vi.stubGlobal('fetch', (input: RequestInfo | URL) => {
    if (pathOf(input) === '/api/v0/admin/auth/session') {
      sessionProbes += 1;
      return Promise.resolve(json(session));
    }
    return Promise.resolve(json(playerState));
  });

  const first = render(<App />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeTruthy());
  first.unmount();

  render(<App />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeTruthy());
  expect(sessionProbes).toBe(2);
});

test('returns to the credential gate when an authenticated admin request receives 401', async () => {
  vi.stubGlobal('fetch', (input: RequestInfo | URL) => {
    if (pathOf(input) === '/api/v0/admin/auth/session') {
      return Promise.resolve(json(session));
    }
    return Promise.resolve(problem(401, 'admin.auth.required', 'Session expired'));
  });

  render(<App />);

  await waitFor(() => expect(screen.getByLabelText('Username')).toBeTruthy());
  expect(screen.queryByRole('heading', { name: 'Dashboard' })).toBeNull();
});

test('logout revokes the server session and returns the operator to the gate', async () => {
  const calls: RequestCall[] = [];
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    const path = pathOf(input);
    calls.push({ path, method: init?.method ?? 'GET', body: bodyOf(init) });
    if (path === '/api/v0/admin/auth/session') {
      return Promise.resolve(json(session));
    }
    if (path === '/api/v0/admin/auth/logout') {
      return Promise.resolve(new Response(null, { status: 204 }));
    }
    return Promise.resolve(json(playerState));
  });

  render(<App />);
  await waitFor(() => expect(screen.getByRole('button', { name: 'Close' })).toBeTruthy());
  fireEvent.click(screen.getByRole('button', { name: 'Close' }));

  await waitFor(() => expect(screen.getByLabelText('Username')).toBeTruthy());
  const logout = calls.find((call) => call.path === '/api/v0/admin/auth/logout');
  expect(logout?.method).toBe('POST');
  expect(logout?.body).toBe('{}');
});

test('renders every admin section without a 501 or unpinned badge after session restoration', async () => {
  vi.stubGlobal('fetch', (input: RequestInfo | URL) => {
    if (pathOf(input) === '/api/v0/admin/auth/session') {
      return Promise.resolve(json(session));
    }
    return Promise.resolve(json(playerState));
  });

  render(<App />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeTruthy());

  expect(screen.queryByText('501')).toBeNull();
  expect(screen.queryByText(/unpinned/i)).toBeNull();
  for (const section of [
    'Dashboard',
    'Queue',
    'Tracks',
    'Social links',
    'Donation goal',
    'Playlists',
    'Storage',
    'Say moderation',
    'Stream-node',
    'Library scan',
  ]) {
    expect(screen.getByRole('tab', { name: section })).toBeTruthy();
  }
});

test('opens each formerly unpinned section as a real page rather than a placeholder', async () => {
  vi.stubGlobal('fetch', (input: RequestInfo | URL) => {
    const path = pathOf(input);
    if (path === '/api/v0/admin/auth/session') {
      return Promise.resolve(json(session));
    }
    if (path === '/api/v0/admin/playlists') {
      return Promise.resolve(json([]));
    }
    if (path === '/api/v0/admin/storage') {
      return Promise.resolve(
        json({
          defaultBackend: {
            type: 'local',
            localRoot: '/storage',
            s3Bucket: null,
            s3Region: null,
            s3ServiceUrl: null,
            s3ForcePathStyle: false,
          },
          additionalBackends: [],
        }),
      );
    }
    if (path === '/api/v0/admin/stream-node/status') {
      return Promise.resolve(
        json({
          status: 'offline',
          desiredState: 'stopped',
          lastHeartbeatUtc: null,
          failureReason: null,
          bitrateKbps: 0,
          restartGeneration: 0,
        }),
      );
    }
    return Promise.resolve(json(playerState));
  });

  render(<App />);
  await screen.findByRole('heading', { name: 'Dashboard' });
  for (const page of [
    { navigation: 'Playlists', signal: 'No active playlist exists yet.' },
    { navigation: 'Storage', signal: 'Configured default backend (read-only)' },
    { navigation: 'Stream-node', signal: 'Status' },
    { navigation: 'Library scan', signal: 'Scan the default storage or an enabled additional backend for playable tracks.' },
  ]) {
    fireEvent.click(screen.getByRole('tab', { name: page.navigation }));
    await screen.findByText(page.signal);
    expect(screen.queryByText(/contract unpinned/i)).toBeNull();
  }
});

test('hides the development fixture action when the restored session does not permit fixtures', async () => {
  vi.stubGlobal('fetch', (input: RequestInfo | URL) => {
    if (pathOf(input) === '/api/v0/admin/auth/session') {
      return Promise.resolve(json(session));
    }
    return Promise.resolve(json(playerState));
  });

  render(<App />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeTruthy());

  expect(screen.queryByRole('button', { name: 'Create demo data' })).toBeNull();
});

test('creates demo data only when the restored session explicitly enables fixtures', async () => {
  const calls: RequestCall[] = [];
  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    const path = pathOf(input);
    calls.push({ path, method: init?.method ?? 'GET', body: bodyOf(init) });
    if (path === '/api/v0/admin/auth/session') {
      return Promise.resolve(json({ ...session, developmentFixturesEnabled: true }));
    }
    if (path === '/api/v0/admin/dev/fixtures/paid-vertical-slice') {
      return Promise.resolve(
        json({
          donationPaymentId: '018f0aaa-0000-7000-8000-000000000060',
          sayPaymentId: '018f0aaa-0000-7000-8000-000000000061',
          sayMessageId: '018f0aaa-0000-7000-8000-000000000062',
        }),
      );
    }
    return Promise.resolve(json(playerState));
  });

  render(<App />);
  await screen.findByRole('button', { name: 'Create demo data' });
  fireEvent.change(screen.getByLabelText('Fixture key'), { target: { value: 'admin-demo' } });
  fireEvent.click(screen.getByRole('button', { name: 'Create demo data' }));

  await waitFor(() => expect(screen.getByText('Demo data created')).toBeTruthy());
  const fixture = calls.find(
    (call) => call.path === '/api/v0/admin/dev/fixtures/paid-vertical-slice',
  );
  expect(fixture?.method).toBe('POST');
  expect(fixture?.body).toBe(JSON.stringify({ fixtureKey: 'admin-demo' }));
});
