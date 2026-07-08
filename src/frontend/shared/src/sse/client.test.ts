import { afterEach, describe, expect, test, vi } from 'vitest';

import {
  createPlayerEventsClient,
  type FetchImpl,
  type SseConnector,
  type SseConnectorSpec,
} from '../index';
import { validPlayerState } from '../testing/fixtures';

interface FakeTransport {
  readonly connector: SseConnector;
  spec(): SseConnectorSpec;
  closes(): number;
}

/** A connector the test drives directly, capturing the spec and counting closes. */
function fakeTransport(): FakeTransport {
  let captured: SseConnectorSpec | null = null;
  let closeCount = 0;
  return {
    connector: (spec) => {
      captured = spec;
      return {
        close: () => {
          closeCount += 1;
        },
      };
    },
    spec: () => {
      if (!captured) throw new Error('connector was not invoked');
      return captured;
    },
    closes: () => closeCount,
  };
}

function okFetch(): FetchImpl {
  return vi.fn(() =>
    Promise.resolve(
      new Response(JSON.stringify(validPlayerState()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  );
}

afterEach(() => {
  vi.useRealTimers();
});

describe('createPlayerEventsClient', () => {
  test('routes each named event to the matching handler with a validated payload', () => {
    const t = fakeTransport();
    const onQueue = vi.fn();
    const onDonation = vi.fn();
    createPlayerEventsClient({ onQueue, onDonation }, { connector: t.connector });

    const state = validPlayerState();
    t.spec().onMessage('player.queue', JSON.stringify(state.queue));
    t.spec().onMessage('player.donation', JSON.stringify(state.donationGoal));

    expect(onQueue).toHaveBeenCalledWith(state.queue);
    expect(onDonation).toHaveBeenCalledWith(state.donationGoal);
  });

  test('reports a malformed payload and does not invoke the handler', () => {
    const t = fakeTransport();
    const onState = vi.fn();
    const onParseError = vi.fn();
    createPlayerEventsClient({ onState, onParseError }, { connector: t.connector });

    t.spec().onMessage('player.state', 'not-json');

    expect(onState).not.toHaveBeenCalled();
    expect(onParseError).toHaveBeenCalledTimes(1);
    expect(onParseError.mock.calls[0]?.[0]).toBe('player.state');
  });

  test('a single disconnect keeps using SSE', () => {
    const t = fakeTransport();
    const onTransportChange = vi.fn();
    const clock = 0;
    createPlayerEventsClient(
      { onTransportChange },
      { connector: t.connector, now: () => clock },
    );

    t.spec().onError();

    expect(onTransportChange).not.toHaveBeenCalled();
  });

  test('disconnects spread beyond the 30s window do not trigger fallback', () => {
    const t = fakeTransport();
    const onTransportChange = vi.fn();
    let clock = 0;
    createPlayerEventsClient(
      { onTransportChange },
      { connector: t.connector, now: () => clock },
    );

    t.spec().onError(); // t = 0
    clock = 40_000;
    t.spec().onError(); // 40s later — only one disconnect inside the window

    expect(onTransportChange).not.toHaveBeenCalled();
  });

  test('two disconnects within 30s switch to polling every 5s', async () => {
    vi.useFakeTimers();
    const t = fakeTransport();
    const onState = vi.fn();
    const onTransportChange = vi.fn();
    const fetchImpl = okFetch();
    let clock = 0;
    createPlayerEventsClient(
      { onState, onTransportChange },
      { connector: t.connector, fetchImpl, now: () => clock },
    );

    t.spec().onError(); // disconnect 1
    clock = 1_000;
    t.spec().onError(); // disconnect 2 → fallback

    expect(onTransportChange).toHaveBeenCalledWith('polling');
    expect(t.closes()).toBe(1); // SSE connection torn down

    await vi.advanceTimersByTimeAsync(0); // immediate recovery poll
    expect(onState).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(5_000);
    expect(onState).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(5_000);
    expect(onState).toHaveBeenCalledTimes(3);
  });

  test('close() stops fallback polling', async () => {
    vi.useFakeTimers();
    const t = fakeTransport();
    const onState = vi.fn();
    let clock = 0;
    const client = createPlayerEventsClient(
      { onState },
      { connector: t.connector, fetchImpl: okFetch(), now: () => clock },
    );

    t.spec().onError();
    clock = 1_000;
    t.spec().onError();
    await vi.advanceTimersByTimeAsync(0); // immediate poll → 1

    client.close();
    await vi.advanceTimersByTimeAsync(20_000);

    expect(onState).toHaveBeenCalledTimes(1); // interval cleared, no further polls
  });

  test('close() shuts the SSE connection when still on SSE', () => {
    const t = fakeTransport();
    const client = createPlayerEventsClient({}, { connector: t.connector });

    client.close();

    expect(t.closes()).toBe(1);
  });
});
