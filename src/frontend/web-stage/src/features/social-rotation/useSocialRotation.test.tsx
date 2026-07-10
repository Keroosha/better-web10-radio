import { renderHook, cleanup, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, test } from 'vitest';

import { useSocialRotation } from './useSocialRotation';

afterEach(cleanup);

describe('useSocialRotation', () => {
  test('idles at 0 for 0 or 1 items (no interval)', () => {
    const { result: none } = renderHook(() => useSocialRotation(0, 15));
    expect(none.current).toBe(0);
    const { result: one } = renderHook(() => useSocialRotation(1, 15));
    expect(one.current).toBe(0);
  });

  test('advances the index over time and tears down cleanly', async () => {
    const { result, unmount } = renderHook(() => useSocialRotation(3, 15));
    expect(result.current).toBe(0);
    await waitFor(() => expect(result.current).toBe(1));
    expect(() => unmount()).not.toThrow();
  });
});
