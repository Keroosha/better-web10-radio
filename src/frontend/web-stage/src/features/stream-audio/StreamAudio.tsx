import type { CSSProperties, ReactElement } from 'react';

import type { StreamStatus } from '@web10/shared';

import type { StageTheme } from '../../shared/ui/theme';
import { useStreamAudio } from './useStreamAudio';

interface StreamAudioProps {
  readonly streamStatus: StreamStatus;
  /** True in the stream-node kiosk capture context — suppresses browser audio + controls. */
  readonly captureEnabled: boolean;
  readonly theme: StageTheme;
}

/**
 * The public audio element plus a retro mute/unmute pill. In capture mode the pill is
 * hidden (the stream-node needs no controls and the tab stays muted). The `<audio>`
 * element is always mounted so the hook can manage its lifecycle.
 */
export function StreamAudio({
  streamStatus,
  captureEnabled,
  theme,
}: StreamAudioProps): ReactElement {
  const { audioRef, muted, toggleMuted, errored, active } = useStreamAudio(
    streamStatus,
    captureEnabled,
  );

  const pillStyle: CSSProperties = {
    ...theme.win,
    position: 'fixed',
    top: '14px',
    right: '14px',
    zIndex: 6,
    width: '34px',
    height: '34px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '15px',
    cursor: 'pointer',
    pointerEvents: 'auto',
  };

  return (
    <>
      {/* Live radio stream — no captions track. */}
      <audio ref={audioRef} />
      {active && (
        <button
          type="button"
          onClick={toggleMuted}
          style={pillStyle}
          title={errored ? 'stream unavailable' : muted ? 'unmute' : 'mute'}
          aria-label={muted ? 'unmute stream' : 'mute stream'}
        >
          {muted ? '🔇' : '🔊'}
        </button>
      )}
    </>
  );
}
