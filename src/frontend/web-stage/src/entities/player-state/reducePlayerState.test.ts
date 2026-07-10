import { describe, expect, test } from 'vitest';

import { validPlayerState } from '../../testing/fixtures';
import { createEmptyPlayerState } from './emptyPlayerState';
import { reducePlayerState } from './reducePlayerState';

describe('reducePlayerState', () => {
  test('snapshot replaces the whole state', () => {
    const next = reducePlayerState(createEmptyPlayerState(), {
      type: 'snapshot',
      state: validPlayerState(),
    });
    expect(next.stream.status).toBe('live');
    expect(next.socials).toHaveLength(2);
  });

  test('queue delta merges only the queue branch', () => {
    const base = validPlayerState();
    const next = reducePlayerState(base, {
      type: 'queue',
      queue: { currentQueueItemId: 'x', items: [] },
    });
    expect(next.queue).toEqual({ currentQueueItemId: 'x', items: [] });
    expect(next.stream).toBe(base.stream);
    expect(next.donationGoal).toBe(base.donationGoal);
    expect(next).not.toBe(base);
  });

  test('say delta merges only superChat', () => {
    const base = validPlayerState();
    const next = reducePlayerState(base, { type: 'say', superChat: { messages: [] } });
    expect(next.superChat.messages).toHaveLength(0);
    expect(next.stream).toBe(base.stream);
  });

  test('donation delta merges only donationGoal', () => {
    const base = validPlayerState();
    const goal = { ...base.donationGoal, raisedStars: 9999 };
    const next = reducePlayerState(base, { type: 'donation', donationGoal: goal });
    expect(next.donationGoal.raisedStars).toBe(9999);
    expect(next.queue).toBe(base.queue);
  });

  test('health delta merges only stream', () => {
    const base = validPlayerState();
    const stream = { ...base.stream, status: 'degraded' as const };
    const next = reducePlayerState(base, { type: 'health', stream });
    expect(next.stream.status).toBe('degraded');
    expect(next.nowPlaying).toBe(base.nowPlaying);
  });
});
