import { describe, expect, test } from 'vitest';

import {
  INVALID_TIME_PLACEHOLDER,
  formatDuration,
  formatProgress,
  formatStars,
  formatTrackLabel,
  formatUtcTime,
} from '../index';

describe('formatStars', () => {
  test('groups thousands and drops fractions', () => {
    expect(formatStars(0)).toBe('0');
    expect(formatStars(25)).toBe('25');
    expect(formatStars(3820)).toBe('3,820');
    expect(formatStars(1_234_567)).toBe('1,234,567');
    expect(formatStars(99.9)).toBe('99');
  });

  test('treats non-finite input as zero', () => {
    expect(formatStars(Number.NaN)).toBe('0');
    expect(formatStars(Number.POSITIVE_INFINITY)).toBe('0');
  });
});

describe('formatUtcTime', () => {
  test('renders HH:MM in UTC', () => {
    expect(formatUtcTime('2026-07-07T09:05:00Z')).toBe('09:05');
    expect(formatUtcTime('2026-07-07T23:59:59Z')).toBe('23:59');
  });

  test('returns the placeholder for unparseable input', () => {
    expect(formatUtcTime('not-a-date')).toBe(INVALID_TIME_PLACEHOLDER);
    expect(formatUtcTime('')).toBe(INVALID_TIME_PLACEHOLDER);
  });
});

describe('formatDuration', () => {
  test('formats M:SS and H:MM:SS', () => {
    expect(formatDuration(0)).toBe('0:00');
    expect(formatDuration(42_000)).toBe('0:42');
    expect(formatDuration(240_000)).toBe('4:00');
    expect(formatDuration(3_900_000)).toBe('1:05:00'); // 65 minutes
  });

  test('clamps invalid input to 0:00', () => {
    expect(formatDuration(-1)).toBe('0:00');
    expect(formatDuration(Number.NaN)).toBe('0:00');
  });
});

describe('formatProgress', () => {
  test('returns a clamped fraction', () => {
    expect(formatProgress(42_000, 240_000)).toBeCloseTo(0.175);
    expect(formatProgress(300_000, 240_000)).toBe(1); // clamped
  });

  test('returns 0 for unknown or non-positive duration', () => {
    expect(formatProgress(10_000, 0)).toBe(0);
    expect(formatProgress(10_000, Number.NaN)).toBe(0);
    expect(formatProgress(-5, 240_000)).toBe(0);
  });
});

describe('formatTrackLabel', () => {
  test('joins artist and title with an em dash', () => {
    expect(formatTrackLabel('Macintosh Plus', 'FLORAL SHOPPE')).toBe(
      'Macintosh Plus — FLORAL SHOPPE',
    );
  });

  test('degrades gracefully when a part is missing', () => {
    expect(formatTrackLabel('', 'Untitled')).toBe('Untitled');
    expect(formatTrackLabel('Unknown Artist', '')).toBe('Unknown Artist');
    expect(formatTrackLabel('  ', '  ')).toBe('');
  });
});
