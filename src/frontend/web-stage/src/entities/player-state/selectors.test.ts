import { describe, expect, test } from 'vitest';

import { message, validPlayerState } from '../../testing/fixtures';
import { createEmptyPlayerState } from './emptyPlayerState';
import {
  selectApprovedMessages,
  selectDonationPercent,
  selectSceneTrack,
  selectStreamStatus,
} from './selectors';

describe('selectApprovedMessages', () => {
  const state = {
    ...validPlayerState(),
    superChat: {
      messages: [
        message('a', 'alice', 'approved'),
        message('p', 'pat', 'pending'),
        message('r', 'rob', 'rejected'),
        message('b', 'bob', 'approved'),
        message('c', 'cora', 'approved'),
      ],
    },
  };

  test('keeps only approved messages', () => {
    const ids = selectApprovedMessages(state, 10).map((m) => m.id);
    expect(ids).toEqual(['a', 'b', 'c']);
  });

  test('respects the layout limit (3 vs 4)', () => {
    expect(selectApprovedMessages(state, 3)).toHaveLength(3);
    // Only 3 approved exist, so a limit of 4 still yields 3.
    expect(selectApprovedMessages(state, 4)).toHaveLength(3);
  });
});

describe('selectDonationPercent', () => {
  test('computes a clamped percentage', () => {
    const state = { ...validPlayerState() };
    state.donationGoal = { ...state.donationGoal, raisedStars: 3820, goalStars: 5000 };
    expect(selectDonationPercent(state)).toBeCloseTo(76.4);
  });

  test('clamps above 100 when over-funded', () => {
    const state = { ...validPlayerState() };
    state.donationGoal = { ...state.donationGoal, raisedStars: 9000, goalStars: 5000 };
    expect(selectDonationPercent(state)).toBe(100);
  });

  test('returns 0 for a zero/absent goal', () => {
    const state = createEmptyPlayerState();
    expect(selectDonationPercent(state)).toBe(0);
  });
});

describe('selectStreamStatus / selectSceneTrack', () => {
  test('reports the stream status', () => {
    expect(selectStreamStatus(validPlayerState())).toBe('live');
    expect(selectStreamStatus(createEmptyPlayerState())).toBe('offline');
  });

  test('scene track is undefined when title and artist are blank', () => {
    expect(selectSceneTrack(createEmptyPlayerState().nowPlaying)).toBeUndefined();
  });

  test('scene track passes through a real track', () => {
    const track = validPlayerState().nowPlaying;
    expect(selectSceneTrack(track)?.title).toBe(track.title);
  });
});
