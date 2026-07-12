import { useState, type ReactElement } from 'react';

import { createPaidVerticalSliceFixture } from '@web10/shared';

import { useAdminSession } from '../../features/admin-auth/AdminAuthGate';
import { useToast } from '../../shared/ui/toast';
import { COLORS } from '../../shared/ui/tokens';

/** Дизайн-система: единые правила поверх 7.css + (в dev) генератор тестовых данных. */
export function StyleguidePage(): ReactElement {
  const session = useAdminSession();
  const { showToast } = useToast();
  const [seeding, setSeeding] = useState(false);

  const seed = async (): Promise<void> => {
    setSeeding(true);
    try {
      const result = await createPaidVerticalSliceFixture({ fixtureKey: `sg-${Date.now()}` });
      showToast(`Данные созданы: ${result.sayMessageId}`);
    } catch (cause) {
      showToast(cause instanceof Error ? cause.message : 'Не удалось создать данные');
    } finally {
      setSeeding(false);
    }
  };

  return (
    <div style={{ maxWidth: '840px' }}>
      <h2 style={{ marginTop: 0, fontSize: '19px' }}>Дизайн-система админки</h2>
      <p style={{ color: COLORS.subtle, fontSize: '13px', maxWidth: '70ch' }}>
        Единые правила поверх <strong>7.css</strong>, чтобы новые экраны выглядели одинаково. Полные
        правила для кодинг-агента — в файле <strong>ПРАВИЛА-UI.md</strong> в корне проекта.
      </p>

      <h3 style={{ fontSize: '14px', margin: '18px 0 6px' }}>1. Кнопки</h3>
      <div style={{ display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', border: '1px solid #e3ecf5', borderRadius: '6px', padding: '12px', background: '#fff' }}>
        <button type="button" className="default">Основное действие</button>
        <button type="button">Вторичное</button>
        <button type="button" style={{ minWidth: 0, padding: '3px 10px' }}>⟳ Иконка+текст</button>
        <button type="button" disabled>Недоступно</button>
      </div>

      <h3 style={{ fontSize: '14px', margin: '18px 0 6px' }}>2. Поля формы</h3>
      <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '9px 12px', alignItems: 'center', maxWidth: '420px', border: '1px solid #e3ecf5', borderRadius: '6px', padding: '12px', background: '#fff' }}>
        <label>Текст</label>
        <input defaultValue="Значение" />
        <label>Выбор</label>
        <select><option>Вариант A</option></select>
      </div>

      <h3 style={{ fontSize: '14px', margin: '18px 0 6px' }}>3. Статусы</h3>
      <div style={{ display: 'flex', gap: '14px', alignItems: 'center', flexWrap: 'wrap', fontSize: '13px' }}>
        {([['В эфире', COLORS.live], ['Запускается', COLORS.starting], ['Ошибка', COLORS.error], ['Оффлайн', COLORS.offline]] as const).map(
          ([label, color]) => (
            <span key={label} style={{ display: 'inline-flex', alignItems: 'center', gap: '6px' }}>
              <span style={{ width: '10px', height: '10px', borderRadius: '50%', background: color }} />
              {label}
            </span>
          ),
        )}
      </div>

      <h3 style={{ fontSize: '14px', margin: '18px 0 6px' }}>4. Палитра</h3>
      <div style={{ display: 'flex', gap: '8px', flexWrap: 'wrap' }}>
        {([['обои', '#14647a'], ['выделение', COLORS.selection], ['рамки', COLORS.panelBorder], ['прогресс', COLORS.progress]] as const).map(
          ([label, bg]) => (
            <div key={label} style={{ width: '70px' }}>
              <div style={{ height: '34px', borderRadius: '4px', background: bg }} />
              <div style={{ fontSize: '10px', color: '#789', textAlign: 'center' }}>{label}</div>
            </div>
          ),
        )}
      </div>

      {session?.developmentFixturesEnabled === true ? (
        <>
          <h3 style={{ fontSize: '14px', margin: '18px 0 6px' }}>Разработка</h3>
          <div style={{ border: '1px solid #e3ecf5', borderRadius: '6px', padding: '12px', background: '#fff' }}>
            <p style={{ margin: '0 0 8px', fontSize: '12px', color: COLORS.subtle }}>
              Создать оплаченный демо-срез (донат + платное сообщение) через реальный платёжный путь.
            </p>
            <button type="button" onClick={() => void seed()} disabled={seeding}>
              {seeding ? 'Создаём…' : '＋ Создать демо-данные'}
            </button>
          </div>
        </>
      ) : null}
    </div>
  );
}
