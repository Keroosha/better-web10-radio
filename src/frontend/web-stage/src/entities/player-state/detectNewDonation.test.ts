import { describe, expect, test } from 'vitest';

import { donation } from '../../testing/fixtures';
import { detectNewDonation } from './detectNewDonation';

describe('detectNewDonation', () => {
  const recent = [donation('d3', 'c', 30), donation('d2', 'b', 20), donation('d1', 'a', 10)];

  test('returns the newest donation whose id is unseen', () => {
    const seen = new Set(['d2', 'd1']);
    expect(detectNewDonation(seen, recent)?.id).toBe('d3');
  });

  test('returns null when every recent donation is already seen', () => {
    const seen = new Set(['d3', 'd2', 'd1']);
    expect(detectNewDonation(seen, recent)).toBeNull();
  });

  test('returns null for an empty recent list', () => {
    expect(detectNewDonation(new Set(), [])).toBeNull();
  });

  test('with nothing seen, returns the newest (first) donation', () => {
    expect(detectNewDonation(new Set(), recent)?.id).toBe('d3');
  });
});
