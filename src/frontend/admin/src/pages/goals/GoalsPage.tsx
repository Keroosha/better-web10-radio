import { useCallback, useState, type ReactElement } from 'react';

import { formatStars, getDonationGoal, updateDonationGoal, type DonationGoal } from '@web10/shared';

import { useApiResource } from '../../shared/lib/useApiResource';
import { ResourceView } from '../../shared/ui/ResourceView';
import { useToast } from '../../shared/ui/toast';
import { COLORS, formGrid, panel } from '../../shared/ui/tokens';

/** Донат-цель (⭐): прогресс + редактирование текста и цели в звёздах. */
export function GoalsPage(): ReactElement {
  const [reloadKey, setReloadKey] = useState(0);
  const load = useCallback((): Promise<DonationGoal> => getDonationGoal(), [reloadKey]);
  const resource = useApiResource(load);

  return (
    <div style={{ maxWidth: '520px' }}>
      <h2 style={{ marginTop: 0, fontSize: '19px' }}>Цели сбора</h2>
      <ResourceView resource={resource}>
        {(goal) => <GoalEditor goal={goal} onSaved={() => setReloadKey((key) => key + 1)} />}
      </ResourceView>
    </div>
  );
}

function GoalEditor({ goal, onSaved }: { readonly goal: DonationGoal; readonly onSaved: () => void }): ReactElement {
  const { showToast } = useToast();
  const [title, setTitle] = useState(goal.title);
  const [target, setTarget] = useState(goal.goalStars);
  const [saving, setSaving] = useState(false);

  const pct = goal.goalStars > 0 ? Math.min(100, Math.round((goal.raisedStars / goal.goalStars) * 100)) : 0;

  const save = async (): Promise<void> => {
    const trimmed = title.trim();
    if (trimmed.length < 1 || trimmed.length > 120 || target < 1) {
      showToast('Заполните текст и цель (≥1 ⭐)');
      return;
    }
    setSaving(true);
    try {
      await updateDonationGoal({ title: trimmed, goalStars: target });
      showToast('Цель сбора сохранена');
      onSaved();
    } catch (cause) {
      showToast(cause instanceof Error ? cause.message : 'Не удалось сохранить');
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <div style={{ ...panel, background: 'linear-gradient(#f7fbff,#eef5fc)' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
          <strong style={{ fontSize: '15px' }}>{goal.title}</strong>
          <span style={{ fontSize: '12px', color: COLORS.subtle }}>{pct}% собрано</span>
        </div>
        <div
          style={{
            margin: '10px 0 4px',
            height: '16px',
            background: '#d3e4f4',
            border: '1px solid #9bb6cd',
            borderRadius: '8px',
            overflow: 'hidden',
          }}
        >
          <div style={{ height: '100%', width: `${pct}%`, background: COLORS.progress }} />
        </div>
        <div style={{ fontSize: '13px' }}>
          <strong>{formatStars(goal.raisedStars)} ⭐</strong> из {formatStars(goal.goalStars)} ⭐
        </div>
        {goal.topDonator !== null ? (
          <div style={{ fontSize: '12px', color: COLORS.subtle, marginTop: '4px' }}>
            Топ-донатер: {goal.topDonator.displayName} ({formatStars(goal.topDonator.amountStars)} ⭐)
          </div>
        ) : null}
      </div>
      <div style={{ ...formGrid, marginTop: '16px' }}>
        <label htmlFor="goal-title">Цель (текст)</label>
        <input id="goal-title" value={title} maxLength={120} onChange={(event) => setTitle(event.target.value)} />
        <label htmlFor="goal-target">Цель (⭐, число)</label>
        <input
          id="goal-target"
          type="number"
          min={1}
          value={target}
          onChange={(event) => setTarget(Number(event.target.value))}
        />
      </div>
      <div style={{ marginTop: '14px' }}>
        <button type="button" className="default" onClick={() => void save()} disabled={saving}>
          {saving ? 'Сохранение…' : 'Сохранить цель'}
        </button>
      </div>
      <p style={{ fontSize: '12px', color: '#89a', marginTop: '8px' }}>
        Собранная сумма не сбрасывается при изменении цели.
      </p>
    </>
  );
}
