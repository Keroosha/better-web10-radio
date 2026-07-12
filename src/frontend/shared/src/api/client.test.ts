import { afterEach, describe, expect, test, vi } from 'vitest';
import { z } from 'zod';

import {
  apiFetch,
  apiSend,
  apiUpload,
  ApiError,
  clearAdminSession,
  getPlayerState,
  setAdminSession,
  subscribeToAdminSessionInvalidation,
  type FetchImpl,
} from '../index';
import { validPlayerState } from '../testing/fixtures';

function jsonResponse<TBody>(body: TBody, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function noContentResponse(): Response {
  return new Response(null, { status: 204 });
}

const activeSession = {
  username: 'operator',
  csrfToken: 'csrf-token',
  developmentFixturesEnabled: true,
};

afterEach(() => {
  clearAdminSession?.();
});

describe('apiFetch via getPlayerState', () => {
  test('requests /api/v0/player/state and returns the validated body', async () => {
    const fetchImpl: FetchImpl = vi.fn(() => Promise.resolve(jsonResponse(validPlayerState())));
    const state = await getPlayerState({ fetchImpl });

    expect(state.stream.status).toBe('live');
    expect(state.donationGoal.raisedStars).toBe(3820);
    expect(fetchImpl).toHaveBeenCalledWith(
      '/api/v0/player/state',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  test('maps a 503 problem-details body to a typed ApiError', async () => {
    const problem = {
      type: 'https://web10.radio/problems/stream-unavailable',
      title: 'Stream unavailable',
      status: 503,
      traceId: 'trace-123',
      code: 'stream.unavailable',
      message: 'Stream is offline',
    };
    const fetchImpl: FetchImpl = () => Promise.resolve(jsonResponse(problem, 503));

    const error = await getPlayerState({ fetchImpl }).catch((error) => error);

    expect(error).toBeInstanceOf(ApiError);
    if (error instanceof ApiError) {
      expect(error.status).toBe(503);
      expect(error.code).toBe('stream.unavailable');
      expect(error.traceId).toBe('trace-123');
      expect(error.message).toBe('Stream is offline');
    }
  });

  test('propagates a ZodError when a 2xx body violates the schema', async () => {
    const fetchImpl: FetchImpl = () => Promise.resolve(jsonResponse({ nope: true }));

    const error = await getPlayerState({ fetchImpl }).catch((caught) => caught);

    expect(error).toBeInstanceOf(z.ZodError);
  });
});

describe('admin session transport', () => {
  test('uses same-origin cookies and a CSRF header for authenticated mutations without bearer auth', async () => {
    setAdminSession(activeSession);
    let captured: RequestInit | undefined;
    const fetchImpl: FetchImpl = vi.fn((_url, init) => {
      captured = init;
      return Promise.resolve(noContentResponse());
    });

    await apiSend('/api/v0/admin/playback/skip', {
      method: 'POST',
      body: {},
      admin: true,
      fetchImpl,
    });

    const headers = new Headers(captured?.headers);
    expect(captured?.credentials).toBe('include');
    expect(headers.get('x-csrf-token')).toBe('csrf-token');
    expect(headers.get('authorization')).toBeNull();
    expect(captured?.body).toBe('{}');
  });

  test('uses cookies but no CSRF header for an authenticated admin read', async () => {
    setAdminSession(activeSession);
    let captured: RequestInit | undefined;
    const fetchImpl: FetchImpl = vi.fn((_url, init) => {
      captured = init;
      return Promise.resolve(jsonResponse({ healthy: true }));
    });

    const body = await apiFetch('/api/v0/admin/health-shape-test', {
      schema: z.object({ healthy: z.boolean() }).strict(),
      admin: true,
      fetchImpl,
    });

    expect(body.healthy).toBe(true);
    expect(captured?.credentials).toBe('include');
    expect(new Headers(captured?.headers).get('x-csrf-token')).toBeNull();
    expect(new Headers(captured?.headers).get('authorization')).toBeNull();
  });

  test('resolves a 204 response without attempting JSON parsing', async () => {
    setAdminSession(activeSession);
    const fetchImpl: FetchImpl = vi.fn(() => Promise.resolve(noContentResponse()));

    await expect(
      apiSend('/api/v0/admin/auth/logout', {
        method: 'POST',
        body: {},
        admin: true,
        fetchImpl,
      }),
    ).resolves.toBeUndefined();
  });

  test('sends FormData without overriding its boundary and still includes admin CSRF', async () => {
    setAdminSession(activeSession);
    const form = new FormData();
    form.set('cover', new Blob(['image bytes'], { type: 'image/png' }));
    let captured: RequestInit | undefined;
    const fetchImpl: FetchImpl = vi.fn((_url, init) => {
      captured = init;
      return Promise.resolve(jsonResponse({ uploaded: true }));
    });

    const result = await apiUpload('/api/v0/admin/tracks/track-1/cover', {
      schema: z.object({ uploaded: z.boolean() }).strict(),
      method: 'PUT',
      body: form,
      admin: true,
      fetchImpl,
    });

    expect(result.uploaded).toBe(true);
    expect(captured?.body).toBe(form);
    const headers = new Headers(captured?.headers);
    expect(headers.get('content-type')).toBeNull();
    expect(headers.get('x-csrf-token')).toBe('csrf-token');
  });

  test('does not invalidate an authenticated session when an auth probe returns 401', async () => {
    setAdminSession(activeSession);
    const onInvalidated = vi.fn();
    const unsubscribe = subscribeToAdminSessionInvalidation(onInvalidated);
    const fetchImpl: FetchImpl = () => Promise.resolve(jsonResponse({ title: 'Required' }, 401));

    await expect(
      apiFetch('/api/v0/admin/auth/session', {
        schema: z.object({ username: z.string() }).strict(),
        authProbe: true,
        fetchImpl,
      }),
    ).rejects.toBeInstanceOf(ApiError);

    const nextFetch: FetchImpl = vi.fn(() => Promise.resolve(noContentResponse()));
    await apiSend('/api/v0/admin/playback/skip', {
      method: 'POST',
      body: {},
      admin: true,
      fetchImpl: nextFetch,
    });

    expect(onInvalidated).not.toHaveBeenCalled();
    expect(new Headers(vi.mocked(nextFetch).mock.calls[0]?.[1]?.headers).get('x-csrf-token')).toBe(
      'csrf-token',
    );
    unsubscribe();
  });

  test('clears session state and notifies subscribers when an authenticated request returns 401', async () => {
    setAdminSession(activeSession);
    const onInvalidated = vi.fn();
    const unsubscribe = subscribeToAdminSessionInvalidation(onInvalidated);
    const unauthorized: FetchImpl = () => Promise.resolve(jsonResponse({ title: 'Required' }, 401));

    await expect(
      apiSend('/api/v0/admin/playback/queue', {
        method: 'POST',
        body: { trackId: 'track-1' },
        admin: true,
        fetchImpl: unauthorized,
      }),
    ).rejects.toBeInstanceOf(ApiError);

    let nextInit: RequestInit | undefined;
    const nextFetch: FetchImpl = vi.fn((_url, init) => {
      nextInit = init;
      return Promise.resolve(noContentResponse());
    });
    await apiSend('/api/v0/admin/playback/queue', {
      method: 'POST',
      body: { trackId: 'track-1' },
      admin: true,
      fetchImpl: nextFetch,
    });

    expect(onInvalidated).toHaveBeenCalledTimes(1);
    expect(new Headers(nextInit?.headers).get('x-csrf-token')).toBeNull();
    unsubscribe();
  });
});

describe('apiSend errors', () => {
  test('throws a typed ApiError carrying the problem code on a non-2xx', async () => {
    const problem = {
      type: 'https://web10.radio/problems/say-state-conflict',
      title: 'Conflict',
      status: 409,
      traceId: 'trace-9',
      code: 'say.state_conflict',
      message: 'Message already moderated',
    };
    const fetchImpl: FetchImpl = () => Promise.resolve(jsonResponse(problem, 409));

    const error = await apiSend('/api/v0/admin/playback/skip', {
      method: 'POST',
      body: {},
      fetchImpl,
    }).catch((caught) => caught);

    expect(error).toBeInstanceOf(ApiError);
    if (error instanceof ApiError) {
      expect(error.status).toBe(409);
      expect(error.code).toBe('say.state_conflict');
    }
  });
});
