import { useEffect, useState, type CSSProperties, type ReactElement } from 'react';
import type { NowPlaying, StreamStatus } from '@web10/shared';

import type { StageTheme } from '../../shared/ui/theme';

interface NowPlayingWidgetProps {
  readonly nowPlaying: NowPlaying;
  readonly streamStatus: StreamStatus;
  readonly theme: StageTheme;
  /** `{...theme.win, ...layout.now}`. */
  readonly windowStyle: CSSProperties;
}

const STATUS_LABEL: Record<StreamStatus, string> = {
  offline: 'OFFLINE',
  starting: 'STARTING…',
  live: 'LIVE',
  degraded: 'DEGRADED',
};

// Per-bar equalizer timings from the mock (5 bars, staggered).
const EQ_BARS = [
  { duration: '0.7s', delay: '0s' },
  { duration: '0.55s', delay: '0.12s' },
  { duration: '0.8s', delay: '0.28s' },
  { duration: '0.6s', delay: '0.05s' },
  { duration: '0.72s', delay: '0.2s' },
];

const ellipsis: CSSProperties = {
  whiteSpace: 'nowrap',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  maxWidth: '300px',
};

export function formatProgress(milliseconds: number): string {
  const safeMilliseconds = Number.isFinite(milliseconds) && milliseconds >= 0 ? Math.floor(milliseconds) : 0;
  const totalSeconds = Math.floor(safeMilliseconds / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
}

/**
 * NOW PLAYING bar (mock L50-72): cover art, title/artist/album, formatted progress,
 * an animated equalizer, and a status pill. Broken or absent artwork intentionally falls
 * back to the channel glyph so offline and legacy states keep a visible identity.
 */
export function NowPlayingWidget({
  nowPlaying,
  streamStatus,
  theme,
  windowStyle,
}: NowPlayingWidgetProps): ReactElement {
  const isLive = streamStatus === 'live';
  const title = nowPlaying.title.trim() === '' ? '@netscapedidnothingwrong' : nowPlaying.title;
  const artist = nowPlaying.artist.trim() === '' ? 'web 1.0 radio · 24/7' : nowPlaying.artist;
  const album = nowPlaying.album.trim();
  const coverUrl = nowPlaying.coverImageUrl.trim();
  const [coverFailed, setCoverFailed] = useState(false);

  useEffect(() => {
    setCoverFailed(false);
  }, [coverUrl, nowPlaying.trackId]);

  const showCover = coverUrl !== '' && !coverFailed;
  const coverAlt = `${title} cover art`;

  return (
    <div style={windowStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: '11px', padding: '8px 13px 8px 9px' }}>
        <div
          style={{
            width: '34px',
            height: '34px',
            flex: 'none',
            borderRadius: '8px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            overflow: 'hidden',
            fontWeight: 900,
            color: '#fff',
            background: 'linear-gradient(150deg,#8ec9ff,#3b7bd0)',
            boxShadow: '0 0 0 1px rgba(255,255,255,0.6), 0 2px 6px rgba(0,0,0,0.2)',
          }}
        >
          {showCover ? (
            <img
              src={coverUrl}
              alt={coverAlt}
              onError={(): void => setCoverFailed(true)}
              style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }}
            />
          ) : (
            <span aria-hidden="true">◈</span>
          )}
        </div>
        <div style={{ minWidth: 0, flex: 1 }}>
          <div
            style={{
              fontSize: '9px',
              letterSpacing: '0.16em',
              fontWeight: 700,
              opacity: 0.65,
              color: theme.accent,
            }}
          >
            NOW PLAYING · 24/7
          </div>
          <div style={{ fontWeight: 700, fontSize: '14px', lineHeight: 1.15, ...ellipsis }}>
            {title}
          </div>
          <div style={{ fontSize: '11px', opacity: 0.75, ...ellipsis }}>{artist}</div>
          {album !== '' && <div style={{ fontSize: '10px', opacity: 0.64, ...ellipsis }}>{album}</div>}
          <div
            data-testid="now-playing-progress"
            aria-label={`Progress ${formatProgress(nowPlaying.positionMs)} of ${formatProgress(nowPlaying.durationMs)}`}
            style={{ fontSize: '10px', opacity: 0.62, fontVariantNumeric: 'tabular-nums' }}
          >
            {formatProgress(nowPlaying.positionMs)} / {formatProgress(nowPlaying.durationMs)}
          </div>
        </div>
        <div
          aria-hidden="true"
          style={{ display: 'flex', alignItems: 'flex-end', gap: '3px', height: '22px', marginLeft: '2px' }}
        >
          {EQ_BARS.map((bar, index) => (
            <span
              key={index}
              style={{
                width: '3px',
                height: '100%',
                background: theme.accent,
                transformOrigin: 'bottom',
                borderRadius: '2px',
                animation: `eqbar ${bar.duration} ease-in-out infinite ${bar.delay}`,
              }}
            />
          ))}
        </div>
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '5px',
            padding: '3px 8px',
            marginLeft: '4px',
            borderRadius: '20px',
            background: isLive ? 'rgba(230,40,70,0.14)' : 'rgba(120,120,120,0.16)',
            color: isLive ? '#c11f43' : '#5a5a5a',
            fontWeight: 700,
            fontSize: '10px',
            letterSpacing: '0.08em',
          }}
        >
          {isLive && (
            <span
              style={{
                width: '7px',
                height: '7px',
                borderRadius: '50%',
                background: '#e5183d',
                animation: 'livepulse 1.4s ease-in-out infinite',
              }}
            />
          )}
          {STATUS_LABEL[streamStatus]}
        </div>
      </div>
    </div>
  );
}
