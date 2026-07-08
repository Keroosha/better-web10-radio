import { describe, expect, test, vi } from 'vitest';
import { z } from 'zod';

import { ApiError, getPlayerState, getSocialLinks, type FetchImpl } from '../index';
import { validPlayerState } from '../testing/fixtures';

function jsonResponse<TBody>(body: TBody, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

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

    const error = await getPlayerState({ fetchImpl }).catch((e) => e);

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

    const error = await getPlayerState({ fetchImpl }).catch((e) => e);

    expect(error).toBeInstanceOf(z.ZodError);
  });
});

describe('getSocialLinks', () => {
  test('validates an array of social links from the admin route', async () => {
    const [social] = validPlayerState().socials;
    const fetchImpl: FetchImpl = vi.fn(() => Promise.resolve(jsonResponse([social])));

    const links = await getSocialLinks({ fetchImpl });

    expect(links).toHaveLength(1);
    expect(links[0]?.kind).toBe('telegram');
    expect(fetchImpl).toHaveBeenCalledWith(
      '/api/v0/admin/social-links',
      expect.objectContaining({ method: 'GET' }),
    );
  });
});
