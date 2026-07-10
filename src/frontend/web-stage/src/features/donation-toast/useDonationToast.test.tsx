import { renderHook, cleanup, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, test } from 'vitest';

import type { RecentDonation } from '@web10/shared';

import { donation } from '../../testing/fixtures';
import { useDonationToast } from './useDonationToast';

afterEach(cleanup);

// Stable donation objects. IMPORTANT: never build a fresh donation inside the render
// callback — a new object each render changes the effect's `[newDonation]` dep every
// render and spins an infinite setToast→re-render loop. Drive changes via `rerender`.
const A: RecentDonation = donation('a', 'alice', 10);
const B: RecentDonation = donation('b', 'bob', 20);

describe('useDonationToast', () => {
  test('shows a new donation then auto-dismisses', async () => {
    const { result, rerender, unmount } = renderHook(
      ({ nd }: { nd: RecentDonation | null }) => useDonationToast(nd, 30),
      { initialProps: { nd: null as RecentDonation | null } },
    );
    expect(result.current).toBeNull();

    rerender({ nd: A });
    expect(result.current?.id).toBe('a');

    await waitFor(() => expect(result.current).toBeNull());
    unmount();
  });

  test('a second donation replaces the first (latest-wins)', () => {
    const { result, rerender, unmount } = renderHook(
      ({ nd }: { nd: RecentDonation | null }) => useDonationToast(nd, 30),
      { initialProps: { nd: null as RecentDonation | null } },
    );
    rerender({ nd: A });
    expect(result.current?.id).toBe('a');

    rerender({ nd: B });
    expect(result.current?.id).toBe('b');
    unmount();
  });

  test('null input yields no toast', () => {
    const { result, unmount } = renderHook(
      ({ nd }: { nd: RecentDonation | null }) => useDonationToast(nd, 30),
      { initialProps: { nd: null as RecentDonation | null } },
    );
    expect(result.current).toBeNull();
    unmount();
  });
});
