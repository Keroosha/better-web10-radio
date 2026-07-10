import { useEffect, useRef, useState, type RefObject } from 'react';

import { playerStreamUrl, type StreamStatus } from '@web10/shared';

export interface StreamAudioControls {
  readonly audioRef: RefObject<HTMLAudioElement | null>;
  readonly muted: boolean;
  toggleMuted(): void;
  /** True after the media element reports an error (e.g. the 503 or a mid-stream drop). */
  readonly errored: boolean;
  /** Whether audio should be attempted at all (viewer mode + a playable status). */
  readonly active: boolean;
}

function isPlayable(status: StreamStatus): boolean {
  return status === 'live' || status === 'starting' || status === 'degraded';
}

/**
 * Owns the `<audio>` element lifecycle for the public web viewer.
 *
 * - **Capture mode** (`captureEnabled === false` here means viewer; `true` = stream-node
 *   kiosk): the Telegram stream's audio is mixed server-side by LiquidSoap, so the browser
 *   tab stays silent — we never set a `src`.
 * - **Viewer mode**: for a playable status we point at `playerStreamUrl()` and attempt
 *   muted autoplay (allowed by browsers); the unmute affordance flips `muted`. For
 *   `offline` we never touch the endpoint (it 503s), avoiding console/network noise.
 *
 * Every listener and the source are torn down on unmount / status change.
 */
export function useStreamAudio(
  streamStatus: StreamStatus,
  captureEnabled: boolean,
): StreamAudioControls {
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const [muted, setMuted] = useState(true);
  const [errored, setErrored] = useState(false);

  const active = !captureEnabled && isPlayable(streamStatus);

  useEffect(() => {
    const audio = audioRef.current;
    if (audio === null) {
      return;
    }
    setErrored(false);
    if (!active) {
      audio.pause();
      audio.removeAttribute('src');
      return;
    }
    audio.src = playerStreamUrl();
    const onError = (): void => {
      setErrored(true);
      audio.pause();
    };
    audio.addEventListener('error', onError);
    // Muted autoplay is permitted; swallow the rejection if a browser still blocks it.
    // `play()` returns a Promise in browsers but `undefined` under jsdom — guard both.
    const playback: Promise<void> | undefined = audio.play();
    void playback?.catch(() => undefined);
    return () => {
      audio.removeEventListener('error', onError);
      audio.pause();
    };
  }, [active]);

  useEffect(() => {
    const audio = audioRef.current;
    if (audio !== null) {
      audio.muted = muted;
    }
  }, [muted]);

  return {
    audioRef,
    muted,
    toggleMuted: () => setMuted((current) => !current),
    errored,
    active,
  };
}
