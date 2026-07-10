import type { RecentDonation } from '@web10/shared';

/**
 * The toast dedup primitive. `recent` is newest-first; returns the newest donation
 * whose `id` has not been seen yet, or `null` when every recent donation is already
 * known. Keeping this pure (no timers, no set mutation) makes the toast logic testable
 * and keeps the mandated 5s polling fallback from re-toasting old donations.
 */
export function detectNewDonation(
  seen: ReadonlySet<string>,
  recent: readonly RecentDonation[],
): RecentDonation | null {
  for (const donation of recent) {
    if (!seen.has(donation.id)) {
      return donation;
    }
  }
  return null;
}
