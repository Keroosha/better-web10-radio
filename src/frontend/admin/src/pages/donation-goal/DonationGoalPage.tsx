import type { ReactElement } from 'react';

import { formatStars, getDonationGoal, type DonationGoal } from '@web10/shared';

import { useApiResource } from '../../shared/lib/useApiResource';
import { ResourceView } from '../../shared/ui/ResourceView';

const loadDonationGoal = (): Promise<DonationGoal> => getDonationGoal();

/**
 * Donation goal — read-only. `GET /api/v0/admin/donation-goal` is implemented; the `PUT`
 * is still `501 admin.contract_unpinned`, so editing is disabled until the backend pins
 * the request body (F4 follow-up).
 */
export function DonationGoalPage(): ReactElement {
  const resource = useApiResource(loadDonationGoal);

  return (
    <section>
      <h2 style={{ fontSize: '16px' }}>Donation goal</h2>
      <p style={{ fontSize: '12px', opacity: 0.7 }}>
        Read-only — editing (PUT) lands once the backend pins the admin contract.
      </p>
      <ResourceView resource={resource}>
        {(goal) => (
          <div style={{ maxWidth: '420px' }}>
            <p style={{ fontWeight: 600 }}>{goal.title}</p>
            <p>
              {formatStars(goal.raisedStars)} / {formatStars(goal.goalStars)} ⭐
            </p>
            <p style={{ opacity: 0.7 }}>
              Top donator:{' '}
              {goal.topDonator === null
                ? '—'
                : `${goal.topDonator.displayName} (${formatStars(goal.topDonator.amountStars)} ⭐)`}
            </p>
          </div>
        )}
      </ResourceView>
    </section>
  );
}
