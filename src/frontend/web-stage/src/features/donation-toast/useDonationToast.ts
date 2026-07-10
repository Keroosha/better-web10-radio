import { useEffect, useState } from 'react';

import type { RecentDonation } from '@web10/shared';

/**
 * Shows the most recent new donation as a transient toast. Each time `newDonation`
 * changes to a non-null value it becomes visible and a dismiss timer starts; the
 * cleanup clears any pending timer so a rapid second donation resets it (latest-wins),
 * matching the mock's `clearTimeout` behaviour. Returns the donation to render, or `null`.
 */
export function useDonationToast(
  newDonation: RecentDonation | null,
  dismissMs = 3800,
): RecentDonation | null {
  const [toast, setToast] = useState<RecentDonation | null>(null);

  useEffect(() => {
    if (newDonation === null) {
      return;
    }
    setToast(newDonation);
    const id = setTimeout(() => {
      setToast(null);
    }, dismissMs);
    return () => {
      clearTimeout(id);
    };
  }, [newDonation, dismissMs]);

  return toast;
}
