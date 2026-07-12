import { useEffect, useRef, useState, type DragEvent, type ReactElement } from 'react';

import {
  formatDuration,
  formatTrackLabel,
  playNow,
  reorderQueue,
  restartCurrent,
  skipCurrent,
  type QueueItem,
  type QueueState,
} from '@web10/shared';

import { useLiveQueue, type UseLiveQueueOptions } from '../../entities/live-queue';
import { useApiMutation } from '../../shared/lib/useApiMutation';

interface QueuePageProps {
  /** Live-queue data-source overrides; injected in tests. */
  readonly options?: UseLiveQueueOptions;
}

function errorMessage(error: Error | null): string | null {
  return error === null ? null : error.message || 'The request could not be completed.';
}

function moveQueuedItem(queue: QueueState, draggedId: string, targetId: string): QueueState | null {
  if (draggedId === targetId) {
    return null;
  }
  const queued = queue.items.filter((item) => item.status === 'queued');
  const sourceIndex = queued.findIndex((item) => item.queueItemId === draggedId);
  const targetIndex = queued.findIndex((item) => item.queueItemId === targetId);
  if (sourceIndex < 0 || targetIndex < 0) {
    return null;
  }
  const reorderedQueued = [...queued];
  const [moved] = reorderedQueued.splice(sourceIndex, 1);
  if (moved === undefined) {
    return null;
  }
  reorderedQueued.splice(targetIndex, 0, moved);
  let queuedIndex = 0;
  return {
    currentQueueItemId: queue.currentQueueItemId,
    items: queue.items.map((item) => {
      if (item.status !== 'queued') {
        return item;
      }
      const replacement = reorderedQueued[queuedIndex];
      queuedIndex += 1;
      return replacement ?? item;
    }),
  };
}

/** Live queue cabinet with ordered optimistic reordering and playback controls. */
export function QueuePage(props: QueuePageProps = {}): ReactElement {
  const { nowPlaying, queue, streamStatus, transport } = useLiveQueue(props.options ?? {});
  const [displayQueue, setDisplayQueue] = useState<QueueState>(queue);
  const [actionMessage, setActionMessage] = useState<string | null>(null);
  const draggedQueueItemId = useRef<string | null>(null);
  const reorderMutation = useApiMutation();
  const skipMutation = useApiMutation();
  const restartMutation = useApiMutation();
  const playNowMutation = useApiMutation();

  // Every SSE queue snapshot is authoritative, including one arriving after an optimistic reorder.
  useEffect(() => {
    setDisplayQueue(queue);
  }, [queue]);

  const handleDragStart = (event: DragEvent<HTMLLIElement>, item: QueueItem): void => {
    if (item.status !== 'queued' || reorderMutation.status === 'pending') {
      return;
    }
    draggedQueueItemId.current = item.queueItemId
    if (event.dataTransfer !== null) {
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData('text/plain', item.queueItemId);
    }
  };

  const handleDrop = (event: DragEvent<HTMLLIElement>, target: QueueItem): void => {
    event.preventDefault();
    const draggedId = draggedQueueItemId.current;
    draggedQueueItemId.current = null;
    if (draggedId === null || target.status !== 'queued' || reorderMutation.status === 'pending') {
      return;
    }
    const previous = displayQueue;
    const next = moveQueuedItem(previous, draggedId, target.queueItemId);
    if (next === null) {
      return;
    }
    setDisplayQueue(next);
    const queueItemIds = next.items
      .filter((item) => item.status === 'queued')
      .map((item) => item.queueItemId);
    setActionMessage(null);
    reorderMutation.run(
      () =>
        reorderQueue({ queueItemIds }).then((serverQueue) => {
          setDisplayQueue(serverQueue);
          setActionMessage('Queue order saved.');
        }).catch((cause) => {
          setDisplayQueue(previous);
          throw cause;
        }),
    );
  };

  const handleSkip = (): void => {
    setActionMessage(null);
    skipMutation.run(() => skipCurrent(), () => setActionMessage('Current track skipped.'));
  };

  const handleRestart = (): void => {
    setActionMessage(null);
    restartMutation.run(() => restartCurrent(), () => setActionMessage('Current track restarted.'));
  };

  const handlePlayNow = (item: QueueItem): void => {
    setActionMessage(null);
    playNowMutation.run(
      () => playNow({ trackId: item.trackId }).then(() => undefined),
      () => setActionMessage(`${item.title || 'Track'} queued to play now.`),
    );
  };

  const nowPlayingLabel = formatTrackLabel(nowPlaying.artist, nowPlaying.title);
  const currentAvailable = nowPlayingLabel !== '' || displayQueue.currentQueueItemId !== '';
  const reorderError = errorMessage(reorderMutation.error);
  const controlError = errorMessage(skipMutation.error) ?? errorMessage(restartMutation.error);
  const playNowError = errorMessage(playNowMutation.error);

  return (
    <section>
      <h2>Queue</h2>
      <dl>
        <dt>Stream</dt>
        <dd>{streamStatus}</dd>
        <dt>Live updates</dt>
        <dd>{transport === 'polling' ? 'polling fallback' : 'SSE'}</dd>
      </dl>

      {actionMessage !== null ? <p aria-live="polite">{actionMessage}</p> : null}
      {reorderError !== null ? <p role="alert" className="admin-error">Could not save queue order: {reorderError}</p> : null}
      {controlError !== null ? <p role="alert" className="admin-error">Playback control failed: {controlError}</p> : null}
      {playNowError !== null ? <p role="alert" className="admin-error">Play now failed: {playNowError}</p> : null}

      <section aria-labelledby="now-playing-heading">
        <h3 id="now-playing-heading">Now playing</h3>
        {nowPlayingLabel === '' ? (
          <p className="admin-muted">Nothing is playing right now.</p>
        ) : (
          <div>
            {nowPlaying.coverImageUrl !== '' ? (
              <img
                src={nowPlaying.coverImageUrl}
                alt={`Cover for ${nowPlayingLabel}`}
                width={128}
                height={128}
                style={{ objectFit: 'cover' }}
              />
            ) : null}
            <dl>
              <dt>Track</dt>
              <dd>{nowPlayingLabel}</dd>
              <dt>Album</dt>
              <dd>{nowPlaying.album || '—'}</dd>
              <dt>Progress</dt>
              <dd>
                {formatDuration(nowPlaying.positionMs)} / {formatDuration(nowPlaying.durationMs)}
              </dd>
              <dt>Source</dt>
              <dd>{nowPlaying.source}</dd>
            </dl>
          </div>
        )}
        <div style={{ display: 'flex', gap: '8px', marginTop: '8px' }}>
          <button type="button" onClick={handleSkip} disabled={!currentAvailable || skipMutation.status === 'pending'}>
            {skipMutation.status === 'pending' ? 'Skipping…' : 'Skip current'}
          </button>
          <button
            type="button"
            onClick={handleRestart}
            disabled={!currentAvailable || restartMutation.status === 'pending'}
          >
            {restartMutation.status === 'pending' ? 'Restarting…' : 'Restart current'}
          </button>
        </div>
      </section>

      <section aria-labelledby="upcoming-queue-heading">
        <h3 id="upcoming-queue-heading">Upcoming queue</h3>
        {displayQueue.items.length === 0 ? (
          <p className="admin-muted">The queue is empty.</p>
        ) : (
          <ul role="listbox" className="has-hover has-scrollbar" aria-label="Playback queue">
            {displayQueue.items.map((item, index) => (
              <QueueRow
                key={item.queueItemId}
                item={item}
                position={index + 1}
                isCurrent={item.queueItemId === displayQueue.currentQueueItemId}
                dragDisabled={reorderMutation.status === 'pending'}
                onDragStart={handleDragStart}
                onDrop={handleDrop}
                onPlayNow={handlePlayNow}
                playNowPending={playNowMutation.status === 'pending'}
              />
            ))}
          </ul>
        )}
      </section>
    </section>
  );
}

interface QueueRowProps {
  readonly item: QueueItem;
  readonly position: number;
  readonly isCurrent: boolean;
  readonly dragDisabled: boolean;
  readonly onDragStart: (event: DragEvent<HTMLLIElement>, item: QueueItem) => void;
  readonly onDrop: (event: DragEvent<HTMLLIElement>, item: QueueItem) => void;
  readonly onPlayNow: (item: QueueItem) => void;
  readonly playNowPending: boolean;
}

function QueueRow({
  item,
  position,
  isCurrent,
  dragDisabled,
  onDragStart,
  onDrop,
  onPlayNow,
  playNowPending,
}: QueueRowProps): ReactElement {
  const draggable = item.status === 'queued' && !dragDisabled;
  return (
    <li
      role="option"
      aria-selected={isCurrent}
      draggable={draggable}
      onDragStart={(event) => onDragStart(event, item)}
      onDragOver={(event) => {
        if (item.status === 'queued' && draggable) {
          event.preventDefault();
          if (event.dataTransfer !== null) {
            event.dataTransfer.dropEffect = 'move';
          }
        }
      }}
      onDrop={(event) => onDrop(event, item)}
    >
      <span>{position}. </span>
      <strong>{item.title || 'Untitled'}</strong>
      {item.artist ? <span> — {item.artist}</span> : null}
      <span className="admin-muted">
        {' · '}
        {item.source} · {item.status}
      </span>
      {isCurrent ? <span> ◀ now playing</span> : null}
      <button type="button" onClick={() => onPlayNow(item)} disabled={playNowPending}>
        {playNowPending ? 'Queueing…' : 'Play now'}
      </button>
    </li>
  );
}
