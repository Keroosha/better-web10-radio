import { useEffect, useState, type ReactElement } from 'react';

import {
  getStreamNodeStatus,
  restartStreamNode,
  startStreamNode,
  stopStreamNode,
  type StreamNodeStatus,
} from '@web10/shared';

import { errorMessage } from '../../shared/lib/errorMessage';
import { useToast } from '../../shared/ui/toast';
import { panel, statusColor } from '../../shared/ui/tokens';

const STATUS_LABEL: Record<string, string> = {
  live: 'В эфире (live)',
  starting: 'Запускается',
  offline: 'Оффлайн',
  degraded: 'Деградация',
  failed: 'Ошибка',
};

const DESIRED_LABEL: Record<string, string> = {
  running: 'работает',
  paused: 'на паузе',
  stopped: 'остановлена',
};

/** Нода трансляции: статус heartbeat + управление запуском. */
export function NodePage(): ReactElement {
  const { showToast } = useToast();
  const [status, setStatus] = useState<StreamNodeStatus | null>(null);

  // Poll heartbeat freshness every 2s, updating in place (no loading flicker so the
  // control buttons stay stably clickable).
  useEffect(() => {
    let active = true;
    const refresh = (): void => {
      void getStreamNodeStatus()
        .then((next) => {
          if (active) {
            setStatus(next);
          }
        })
        .catch(() => undefined);
    };
    refresh();
    const timer = setInterval(refresh, 2000);
    return () => {
      active = false;
      clearInterval(timer);
    };
  }, []);

  const control = <T,>(action: () => Promise<T>, message: string): void => {
    action()
      .then((): Promise<StreamNodeStatus> => {
        showToast(message);
        return getStreamNodeStatus();
      })
      .then((next) => setStatus(next))
      .catch((cause) => showToast(errorMessage(cause, 'Ошибка')));
  };

  return (
    <div style={{ maxWidth: '560px' }}>
      <h2 style={{ marginTop: 0, fontSize: '19px' }}>Нода трансляции</h2>
      {status === null ? (
        <p className="admin-muted">Загрузка…</p>
      ) : (
        ((): ReactElement => {
          const color = statusColor(status.status);
          return (
            <>
              <div style={{ ...panel, background: 'linear-gradient(#f7fbff,#eef5fc)', padding: '16px' }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: '10px', marginBottom: '12px' }}>
                  <span style={{ width: '12px', height: '12px', borderRadius: '50%', background: color }} />
                  <strong style={{ fontSize: '16px', color }}>{STATUS_LABEL[status.status] ?? status.status}</strong>
                </div>
                <table style={{ width: '100%' }}>
                  <tbody>
                    <tr>
                      <td style={{ color: '#789' }}>Желаемое состояние</td>
                      <td style={{ fontWeight: 600 }}>{DESIRED_LABEL[status.desiredState] ?? status.desiredState}</td>
                    </tr>
                    <tr>
                      <td style={{ color: '#789' }}>Битрейт</td>
                      <td style={{ fontWeight: 600 }}>{status.bitrateKbps > 0 ? `${status.bitrateKbps} kbps` : '—'}</td>
                    </tr>
                    <tr>
                      <td style={{ color: '#789' }}>Последний heartbeat</td>
                      <td style={{ fontWeight: 600 }}>{status.lastHeartbeatUtc ?? 'нет'}</td>
                    </tr>
                    <tr>
                      <td style={{ color: '#789' }}>Поколение перезапуска</td>
                      <td style={{ fontWeight: 600 }}>#{status.restartGeneration}</td>
                    </tr>
                  </tbody>
                </table>
              </div>
              <div style={{ display: 'flex', gap: '8px', marginTop: '14px' }}>
                <button type="button" className="default" onClick={() => control(startStreamNode, 'Запуск ноды…')}>
                  ▶ Запустить
                </button>
                <button type="button" onClick={() => control(stopStreamNode, 'Нода остановлена')}>
                  ⏹ Остановить
                </button>
                <button type="button" onClick={() => control(restartStreamNode, 'Перезапуск ноды…')}>
                  ↺ Перезапустить
                </button>
              </div>
            </>
          );
        })()
      )}
    </div>
  );
}
