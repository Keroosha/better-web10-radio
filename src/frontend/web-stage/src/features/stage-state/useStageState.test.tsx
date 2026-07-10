import { act, renderHook, waitFor, cleanup } from '@testing-library/react';
import { afterEach, describe, expect, test } from 'vitest';

afterEach(cleanup);

import { donation, validPlayerState } from '../../testing/fixtures';
import { fakeStateFetch, makeFakeConnector } from '../../testing/sse';
import { useStageState } from './useStageState';

describe('useStageState', () => {
  test('renders the empty offline state before the seed resolves', () => {
    const fake = makeFakeConnector();
    const { result } = renderHook(() =>
      useStageState({ connector: fake.connector, fetchImpl: fakeStateFetch(validPlayerState()) }),
    );
    // First synchronous render: empty default (seed is async).
    expect(result.current.state.stream.status).toBe('offline');
    expect(result.current.newDonation).toBeNull();
  });

  test('seeds from getPlayerState', async () => {
    const fake = makeFakeConnector();
    const { result } = renderHook(() =>
      useStageState({ connector: fake.connector, fetchImpl: fakeStateFetch(validPlayerState()) }),
    );
    await waitFor(() => expect(result.current.state.stream.status).toBe('live'));
    expect(result.current.state.socials).toHaveLength(2);
  });

  test('merges each SSE delta into its own branch', async () => {
    const fake = makeFakeConnector();
    const { result } = renderHook(() =>
      useStageState({ connector: fake.connector, fetchImpl: fakeStateFetch(validPlayerState()) }),
    );
    await waitFor(() => expect(result.current.state.stream.status).toBe('live'));

    act(() => fake.emit('player.queue', { currentQueueItemId: 'q9', items: [] }));
    expect(result.current.state.queue.currentQueueItemId).toBe('q9');
    // Other branches untouched.
    expect(result.current.state.stream.status).toBe('live');

    act(() => fake.emit('player.health', { ...validPlayerState().stream, status: 'degraded' }));
    expect(result.current.state.stream.status).toBe('degraded');
  });

  test('switches to polling after two disconnects within the window', async () => {
    const fake = makeFakeConnector();
    const { result } = renderHook(() =>
      useStageState({
        connector: fake.connector,
        fetchImpl: fakeStateFetch(validPlayerState()),
        now: () => 1000,
      }),
    );
    await waitFor(() => expect(result.current.state.stream.status).toBe('live'));

    expect(result.current.transport).toBe('sse');
    act(() => {
      fake.fail();
      fake.fail();
    });
    await waitFor(() => expect(result.current.transport).toBe('polling'));
  });

  test('toasts a new donation, but not existing or snapshot donations', async () => {
    const seed = validPlayerState();
    seed.donationGoal = { ...seed.donationGoal, recent: [donation('old', 'seeded', 10)] };
    const fake = makeFakeConnector();
    const { result } = renderHook(() =>
      useStageState({ connector: fake.connector, fetchImpl: fakeStateFetch(seed) }),
    );
    await waitFor(() => expect(result.current.state.stream.status).toBe('live'));
    // Seed's donation must not toast.
    expect(result.current.newDonation).toBeNull();

    // A snapshot carrying only seen donations must not toast.
    act(() => fake.emit('player.state', seed));
    expect(result.current.newDonation).toBeNull();

    // A donation event with a genuinely new id toasts it.
    act(() =>
      fake.emit('player.donation', {
        ...seed.donationGoal,
        recent: [donation('fresh', 'newcomer', 50), donation('old', 'seeded', 10)],
      }),
    );
    expect(result.current.newDonation?.id).toBe('fresh');
  });

  test('closes the connector on unmount', async () => {
    const fake = makeFakeConnector();
    const { result, unmount } = renderHook(() =>
      useStageState({ connector: fake.connector, fetchImpl: fakeStateFetch(validPlayerState()) }),
    );
    await waitFor(() => expect(result.current.state.stream.status).toBe('live'));
    unmount();
    expect(fake.isClosed()).toBe(true);
  });
});
