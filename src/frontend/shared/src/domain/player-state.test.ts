import { describe, expect, test } from 'vitest';
import { z } from 'zod';

import { BannerSchema, PlayerStateSchema, type PlayerState } from './player-state';
import { emptyPlayerState, superChatBanner, validPlayerState } from '../testing/fixtures';

// Deep clone that yields a mutable, structurally-typed-`any` value (JSON.parse's
// return) so negative tests can craft malformed input without authored `any`/`unknown`.
function clone(value: PlayerState) {
  return JSON.parse(JSON.stringify(value));
}

describe('PlayerStateSchema', () => {
  test('accepts a fully-populated snapshot and returns a typed value', () => {
    const parsed = PlayerStateSchema.parse(validPlayerState());
    expect(parsed.nowPlaying.artist).toBe('Macintosh Plus');
    expect(parsed.donationGoal.topDonator?.amountStars).toBe(500);
  });

  test('accepts the exact superchat banner type', () => {
    expect(BannerSchema.parse(superChatBanner()).type).toBe('superchat');
  });

  test('accepts the empty/offline snapshot (empty arrays, null top donator)', () => {
    const parsed = PlayerStateSchema.parse(emptyPlayerState());
    expect(parsed.stream.status).toBe('offline');
    expect(parsed.queue.items).toEqual([]);
    expect(parsed.socials).toEqual([]);
    expect(parsed.superChat.messages).toEqual([]);
    expect(parsed.donationGoal.recent).toEqual([]);
    expect(parsed.donationGoal.topDonator).toBeNull();
  });

  test('accepts fallback as a queue item source (SPEC §5)', () => {
    const state = clone(validPlayerState());
    state.queue.items[0].source = 'fallback';
    const parsed = PlayerStateSchema.parse(state);
    expect(parsed.queue.items[0]?.source).toBe('fallback');
  });

  test('rejects an unknown enum literal', () => {
    const bad = clone(validPlayerState());
    bad.stream.status = 'paused';
    expect(() => PlayerStateSchema.parse(bad)).toThrow(z.ZodError);
  });

  test('rejects a missing required field', () => {
    const bad = clone(validPlayerState());
    delete bad.overlay;
    expect(() => PlayerStateSchema.parse(bad)).toThrow(z.ZodError);
  });

  test('rejects a wrong scalar type', () => {
    const bad = clone(validPlayerState());
    bad.donationGoal.raisedStars = '3820';
    expect(() => PlayerStateSchema.parse(bad)).toThrow(z.ZodError);
  });
});
