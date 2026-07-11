import { useEffect, useState, type FormEvent, type ReactElement } from 'react';

import { ApiError, formatStars, getDonationGoal, updateDonationGoal, type DonationGoal } from '@web10/shared';

import { useApiResource } from '../../shared/lib/useApiResource';
import { ResourceView } from '../../shared/ui/ResourceView';

const loadDonationGoal = (): Promise<DonationGoal> => getDonationGoal();

/** Updates the active donation goal without changing its accumulated progress. */
export function DonationGoalPage(): ReactElement {
  const resource = useApiResource(loadDonationGoal);
  const [title, setTitle] = useState('');
  const [goalStars, setGoalStars] = useState('');
  const [savedGoal, setSavedGoal] = useState<DonationGoal | null>(null);
  const [saveError, setSaveError] = useState<Error | null>(null);
  const [isSaving, setIsSaving] = useState(false);

  useEffect(() => {
    if (resource.status === 'ready') {
      setTitle(resource.data.title);
      setGoalStars(String(resource.data.goalStars));
    }
  }, [resource]);

  const saveGoal = async (event: FormEvent<HTMLFormElement>): Promise<void> => {
    event.preventDefault();
    if (isSaving) {
      return;
    }

    const normalizedTitle = title.trim();
    const parsedGoalStars = Number(goalStars);
    if (normalizedTitle.length === 0 || normalizedTitle.length > 120) {
      setSaveError(new Error('Goal title must contain 1–120 characters.'));
      return;
    }
    if (
      !/^\d+$/.test(goalStars) ||
      !Number.isSafeInteger(parsedGoalStars) ||
      parsedGoalStars < 1 ||
      parsedGoalStars > 2_147_483_647
    ) {
      setSaveError(new Error('Goal in Stars must be a positive integer.'));
      return;
    }

    setIsSaving(true);
    setSaveError(null);
    setSavedGoal(null);
    try {
      const updatedGoal = await updateDonationGoal({ title: normalizedTitle, goalStars: parsedGoalStars });
      setSavedGoal(updatedGoal);
      setTitle(updatedGoal.title);
      setGoalStars(String(updatedGoal.goalStars));
    } catch (cause) {
      setSaveError(cause instanceof Error ? cause : new Error('Unable to save donation goal.'));
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <section>
      <h2>Donation goal</h2>
      <ResourceView resource={resource}>
        {(loadedGoal) => {
          const displayedGoal = savedGoal ?? loadedGoal;
          return (
            <div style={{ maxWidth: '420px' }}>
              <p>
                {formatStars(displayedGoal.raisedStars)} / {formatStars(displayedGoal.goalStars)} ⭐
              </p>
              <p className="admin-muted">
                Top donator:{' '}
                {displayedGoal.topDonator === null
                  ? '—'
                  : `${displayedGoal.topDonator.displayName} (${formatStars(displayedGoal.topDonator.amountStars)} ⭐)`}
              </p>
              <form onSubmit={saveGoal} noValidate style={{ marginTop: '12px' }}>
                <div className="group">
                  <label htmlFor="goal-title">Goal title</label>
                  <input
                    id="goal-title"
                    value={title}
                    onChange={(event) => setTitle(event.currentTarget.value)}
                    disabled={isSaving}
                    maxLength={120}
                  />
                </div>
                <div className="group">
                  <label htmlFor="goal-stars">Goal in Stars</label>
                  <input
                    id="goal-stars"
                    type="number"
                    min="1"
                    step="1"
                    value={goalStars}
                    onChange={(event) => setGoalStars(event.currentTarget.value)}
                    disabled={isSaving}
                  />
                </div>
                <button type="submit" className="default" disabled={isSaving} style={{ marginTop: '12px' }}>
                  {isSaving ? 'Saving…' : 'Save goal'}
                </button>
              </form>
              {saveError !== null ? (
                <p role="alert" className="admin-error">
                  {saveError instanceof ApiError && saveError.code !== null ? saveError.code : saveError.message}
                </p>
              ) : null}
              {savedGoal !== null ? <p>Saved</p> : null}
            </div>
          );
        }}
      </ResourceView>
    </section>
  );
}
