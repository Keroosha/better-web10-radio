import { useEffect, useRef, useState, type ReactElement } from 'react';

import {
  formatDuration,
  playNow,
  removeQueueItem,
  reorderQueue,
  restartCurrent,
  type NowPlaying,
  type QueueItem,
  type QueueState,
} from '@web10/shared';

import { errorMessage } from '../../shared/lib/errorMessage';
import { useToast } from '../../shared/ui/toast';
import { COLORS, ellipsis, iconButton, listRow, panel } from '../../shared/ui/tokens';

interface PlayerPageProps {
  readonly nowPlaying: NowPlaying;
  readonly queue: QueueState;
}

/** Плеер: карточка «сейчас играет» + очередь с ручным порядком. */
export function PlayerPage({ nowPlaying, queue }: PlayerPageProps): ReactElement {
  const { showToast } = useToast();
  const queued = queue.items.filter((item) => item.status === 'queued');
  const byId = new Map<string, QueueItem>(queued.map((item) => [item.queueItemId, item]));

  const [order, setOrder] = useState<string[]>(queued.map((item) => item.queueItemId));
  const dragIndex = useRef<number | null>(null);

  // Re-sync the local order whenever the live queue changes (SSE snapshot).
  useEffect(() => {
    setOrder(queue.items.filter((item) => item.status === 'queued').map((item) => item.queueItemId));
  }, [queue]);

  const commitOrder = (next: string[]): void => {
    const previous = order;
    setOrder(next);
    reorderQueue({ queueItemIds: next }).catch((cause) => {
      setOrder(previous);
      showToast(errorMessage(cause, 'Не удалось изменить порядок'));
    });
  };

  const onDrop = (index: number): void => {
    const from = dragIndex.current;
    dragIndex.current = null;
    if (from === null || from === index) {
      return;
    }
    const next = [...order];
    const [moved] = next.splice(from, 1);
    if (moved === undefined) {
      return;
    }
    next.splice(index, 0, moved);
    commitOrder(next);
  };

  const onPlayNow = (item: QueueItem): void => {
    playNow({ trackId: item.trackId })
      .then(() => showToast('Играет сейчас'))
      .catch((cause) => showToast(errorMessage(cause, 'Ошибка')));
  };

  const onRemove = (item: QueueItem): void => {
    setOrder((current) => current.filter((id) => id !== item.queueItemId));
    removeQueueItem(item.queueItemId)
      .then(() => showToast('Убрано из очереди'))
      .catch((cause) => showToast(errorMessage(cause, 'Не удалось убрать')));
  };

  const eqBar = (height: string, delay: string): ReactElement => (
    <div
      style={{
        width: '4px',
        background: '#fff',
        height,
        animation: `eqbar .8s ease-in-out infinite ${delay}`,
      }}
    />
  );

  return (
    <div style={{ display: 'flex', gap: '18px', flexWrap: 'wrap', alignItems: 'flex-start' }}>
      <div style={{ ...panel, flex: 1, minWidth: '300px', padding: '16px', background: 'linear-gradient(#f7fbff,#eef5fc)' }}>
        <div style={{ display: 'flex', gap: '16px' }}>
          <div
            style={{
              width: '150px',
              height: '150px',
              borderRadius: '8px',
              background: nowPlaying.coverImageUrl
                ? `center / cover no-repeat url("${nowPlaying.coverImageUrl}")`
                : 'linear-gradient(135deg,#ff9ec7,#a6e3ff)',
              flex: 'none',
              boxShadow: '0 3px 10px rgba(0,60,90,.25)',
              display: 'flex',
              alignItems: 'flex-end',
              padding: '8px',
            }}
          >
            <div style={{ display: 'flex', alignItems: 'flex-end', gap: '3px', height: '26px' }}>
              {eqBar('40%', '0s')}
              {eqBar('80%', '.15s')}
              {eqBar('55%', '.3s')}
              {eqBar('70%', '.1s')}
            </div>
          </div>
          <div style={{ minWidth: 0, flex: 1 }}>
            <div style={{ fontSize: '11px', letterSpacing: '.6px', color: '#4a8fb0', fontWeight: 600 }}>
              СЕЙЧАС ИГРАЕТ
            </div>
            <div style={{ fontSize: '22px', fontWeight: 'bold', margin: '3px 0', lineHeight: 1.15 }}>
              {nowPlaying.title || 'Нет трека'}
            </div>
            <div style={{ color: '#345', fontSize: '14px' }}>{nowPlaying.artist}</div>
            <div style={{ color: '#789', fontSize: '12px', marginBottom: '8px' }}>{nowPlaying.album}</div>
            <span
              style={{
                display: 'inline-block',
                fontSize: '11px',
                padding: '1px 8px',
                border: '1px solid #9bc',
                borderRadius: '10px',
                background: '#eaf3fb',
                color: '#456',
              }}
            >
              {nowPlaying.source === 'library' ? 'Библиотека' : nowPlaying.source === 'request' ? 'Заказ' : 'Плейлист'}
            </span>
          </div>
        </div>
        <div style={{ marginTop: '14px' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: '11px', color: COLORS.subtle, marginBottom: '3px' }}>
            <span>{formatDuration(nowPlaying.positionMs)}</span>
            <span>{formatDuration(nowPlaying.durationMs)}</span>
          </div>
          <div style={{ height: '9px', background: '#d3e4f4', border: '1px solid #9bb6cd', borderRadius: '5px', overflow: 'hidden' }}>
            <div
              style={{
                height: '100%',
                width: nowPlaying.durationMs > 0 ? `${Math.min(100, (nowPlaying.positionMs / nowPlaying.durationMs) * 100)}%` : '0%',
                background: COLORS.progress,
              }}
            />
          </div>
        </div>
        <div style={{ marginTop: '16px' }}>
          <button
            type="button"
            onClick={() => restartCurrent().then(() => showToast('Трек перезапущен')).catch(() => showToast('Ошибка'))}
          >
            ↺ Заново
          </button>
          <span style={{ marginLeft: '10px', fontSize: '12px', color: COLORS.subtle }}>
            Плей/пауза/стоп/пропуск — в панели-транспорте ниже.
          </span>
        </div>
      </div>

      <div style={{ flex: 1, minWidth: '300px', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
          <strong style={{ fontSize: '14px' }}>Очередь · {order.length}</strong>
          <span style={{ fontSize: '11px', color: '#89a' }}>перетащите строки для порядка</span>
        </div>
        <ul className="tree-view has-container" style={{ marginTop: '6px', overflow: 'auto', maxHeight: '420px' }}>
          {order.length === 0 ? (
            <li style={{ padding: '10px', color: '#89a' }}>Очередь пуста.</li>
          ) : null}
          {order.map((id, index) => {
            const item = byId.get(id);
            if (item === undefined) {
              return null;
            }
            return (
              <li
                key={id}
                draggable
                onDragStart={() => {
                  dragIndex.current = index;
                }}
                onDragOver={(event) => event.preventDefault()}
                onDrop={() => onDrop(index)}
                style={{ ...listRow, cursor: 'grab' }}
              >
                <span style={{ color: '#9ab', fontSize: '12px' }}>⋮⋮</span>
                <span style={{ width: '20px', textAlign: 'right', color: '#89a', fontSize: '12px' }}>{index + 1}</span>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontWeight: 600, fontSize: '13px', ...ellipsis }}>{item.title}</div>
                  <div style={{ fontSize: '11px', color: '#678' }}>{item.artist}</div>
                </div>
                <button type="button" onClick={() => onPlayNow(item)} title="Играть сейчас" style={iconButton}>
                  ▶
                </button>
                <button type="button" onClick={() => onRemove(item)} title="Убрать" style={iconButton}>
                  ✕
                </button>
              </li>
            );
          })}
        </ul>
      </div>
    </div>
  );
}
