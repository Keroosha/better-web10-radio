import { afterEach, describe, expect, test, vi } from 'vitest';

import {
  getDonationGoal,
  getPlayerState,
  getSocialLinks,
  setAdminToken,
  type FetchImpl,
} from '../index';
import { validPlayerState } from '../testing/fixtures';

function jsonResponse<TBody>(body: TBody, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Captures the `RequestInit` the client hands to fetch so we can inspect headers. */
function capturingFetch<TBody>(body: TBody): {
  fetchImpl: FetchImpl;
  lastInit: () => RequestInit | undefined;
} {
  let init: RequestInit | undefined;
  const fetchImpl: FetchImpl = vi.fn((_url, requestInit) => {
    init = requestInit;
    return Promise.resolve(jsonResponse(body));
  });
  return { fetchImpl, lastInit: () => init };
}

// The token is module-global; always reset so tests don't leak into each other.
afterEach(() => {
  setAdminToken(null);
});

describe('admin routes attach the bearer token', () => {
  test('getDonationGoal sends Authorization: Bearer <token> when set', async () => {
    setAdminToken('test-token');
    const { fetchImpl, lastInit } = capturingFetch(validPlayerState().donationGoal);

    await getDonationGoal({ fetchImpl });

    const headers = new Headers(lastInit()?.headers);
    expect(headers.get('authorization')).toBe('Bearer test-token');
  });

  test('getSocialLinks sends Authorization: Bearer <token> when set', async () => {
    setAdminToken('sekret');
    const { fetchImpl, lastInit } = capturingFetch(validPlayerState().socials);

    await getSocialLinks({ fetchImpl });

    const headers = new Headers(lastInit()?.headers);
    expect(headers.get('authorization')).toBe('Bearer sekret');
  });

  test('no Authorization header when the admin token is unset', async () => {
    const { fetchImpl, lastInit } = capturingFetch(validPlayerState().donationGoal);

    await getDonationGoal({ fetchImpl });

    const headers = new Headers(lastInit()?.headers);
    expect(headers.get('authorization')).toBeNull();
  });
});

describe('player routes never attach the admin token', () => {
  test('getPlayerState has no Authorization header even when a token is set', async () => {
    setAdminToken('should-not-be-sent');
    const { fetchImpl, lastInit } = capturingFetch(validPlayerState());

    await getPlayerState({ fetchImpl });

    const headers = new Headers(lastInit()?.headers);
    expect(headers.get('authorization')).toBeNull();
  });
});
