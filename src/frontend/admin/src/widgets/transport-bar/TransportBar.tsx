import type { CSSProperties, ReactElement } from 'react';

import { formatDuration, type NowPlaying, type PlaybackState } from '@web10/shared';

import { COLORS, ellipsis } from '../../shared/ui/tokens';

interface TransportBarProps {
  readonly nowPlaying: NowPlaying;
  readonly playbackState: PlaybackState;
  readonly onSkip: () => void;
  readonly onTogglePlay: () => void;
  readonly onStop: () => void;
}

const compactButton: CSSProperties = { minWidth: 0, padding: '3px 10px' };

/**
 * The persistent bottom transport (ПРАВИЛА §3): the only place playback is controlled.
 * ▶/⏸ toggles the stream-node between running and paused; ⏹ stops; ⏭ skips the current
 * track. State comes from the live player snapshot (`playbackState`).
 */
export function TransportBar({
  nowPlaying,
  playbackState,
  onSkip,
  onTogglePlay,
  onStop,
}: TransportBarProps): ReactElement {
  const playing = playbackState === 'playing';
  const durationMs = nowPlaying.durationMs;
  const positionMs = nowPlaying.positionMs;
  const progressPct = durationMs > 0 ? `${Math.min(100, (positionMs / durationMs) * 100)}%` : '0%';
  const liveColor = playing ? COLORS.error : COLORS.offline;

  return (
    <div
      style={{
        flex: 'none',
        marginTop: '8px',
        display: 'flex',
        alignItems: 'center',
        gap: '14px',
        padding: '8px 12px',
        border: '1px solid #7ea6c6',
        borderRadius: '6px',
        background: 'linear-gradient(#eef6fd,#d4e7f8)',
      }}
    >
      <div
        style={{
          width: '44px',
          height: '44px',
          borderRadius: '4px',
          background: nowPlaying.coverImageUrl
            ? `center / cover no-repeat url("${nowPlaying.coverImageUrl}")`
            : 'linear-gradient(135deg,#ff9ec7,#a6e3ff)',
          flex: 'none',
          boxShadow: '0 1px 4px rgba(0,50,80,.3)',
        }}
      />
      <div style={{ minWidth: '160px', maxWidth: '220px' }}>
        <div style={{ fontWeight: 'bold', fontSize: '13px', ...ellipsis }}>
          {nowPlaying.title || 'Нет трека'}
        </div>
        <div style={{ fontSize: '11px', color: COLORS.subtle, ...ellipsis }}>{nowPlaying.artist}</div>
      </div>
      <div style={{ display: 'flex', gap: '4px', flex: 'none' }}>
        <button type="button" onClick={onSkip} title="Пропустить" style={compactButton}>
          ⏭
        </button>
        <button
          type="button"
          className="default"
          onClick={onTogglePlay}
          title="Плей/Пауза"
          style={{ minWidth: 0, padding: '3px 12px' }}
        >
          {playing ? '⏸' : '▶'}
        </button>
        <button type="button" onClick={onStop} title="Стоп" style={compactButton}>
          ⏹
        </button>
      </div>
      <div style={{ flex: 1, minWidth: '100px' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: '10px', color: COLORS.subtle }}>
          <span>{formatDuration(positionMs)}</span>
          <span>{formatDuration(durationMs)}</span>
        </div>
        <div
          style={{
            height: '7px',
            background: '#cddff0',
            border: '1px solid #9bb6cd',
            borderRadius: '4px',
            overflow: 'hidden',
          }}
        >
          <div style={{ height: '100%', width: progressPct, background: COLORS.progress }} />
        </div>
      </div>
      <span
        style={{
          flex: 'none',
          display: 'inline-flex',
          alignItems: 'center',
          gap: '5px',
          fontSize: '11px',
          fontWeight: 'bold',
          color: liveColor,
        }}
      >
        <span style={{ width: '8px', height: '8px', borderRadius: '50%', background: liveColor }} />
        {playbackState === 'playing' ? 'В ЭФИРЕ' : playbackState === 'paused' ? 'ПАУЗА' : 'СТОП'}
      </span>
    </div>
  );
}
