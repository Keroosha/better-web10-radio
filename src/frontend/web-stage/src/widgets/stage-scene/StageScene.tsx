import type { CSSProperties, ReactElement } from 'react';
import { useEffect, useRef, useState } from 'react';

import type { NowPlaying } from '@web10/shared';

import {
  createRadioScene,
  type RadioSceneHandle,
  type RadioSceneOptions,
} from './createRadioScene';
import { LoadingOverlay } from './LoadingOverlay';

/** Injection seam: real scene by default; tests pass a fake to avoid WebGL under jsdom. */
export type SceneFactory = (
  canvas: HTMLCanvasElement,
  options: RadioSceneOptions,
) => RadioSceneHandle;

export interface StageSceneProps {
  /** Live track for the CD/nameplate. Unset ⇒ neutral placeholder (Phase F2 has no data). */
  readonly nowPlaying?: NowPlaying;
  /** Mouse parallax; off for stream-node capture. Default true. */
  readonly pointerEnabled?: boolean;
  /** Scene factory override for tests. */
  readonly createScene?: SceneFactory;
}

const canvasStyle: CSSProperties = {
  position: 'fixed',
  inset: 0,
  width: '100%',
  height: '100%',
  display: 'block',
};

// Subtle vignette above the canvas, matching the mock's z-index:2 overlay.
const vignetteStyle: CSSProperties = {
  position: 'fixed',
  inset: 0,
  pointerEvents: 'none',
  background: 'radial-gradient(120% 90% at 50% 44%, transparent 55%, rgba(120,60,90,0.18) 100%)',
  zIndex: 2,
};

export function StageScene({
  nowPlaying,
  pointerEnabled = true,
  createScene = createRadioScene,
}: StageSceneProps): ReactElement {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (canvas === null) {
      return;
    }
    let cancelled = false;
    // exactOptionalPropertyTypes: build with `track` present only when we actually have one.
    const onReady = (): void => {
      if (!cancelled) {
        setReady(true);
      }
    };
    const options: RadioSceneOptions =
      nowPlaying === undefined
        ? { pointerEnabled, onReady }
        : {
            pointerEnabled,
            onReady,
            track: { title: nowPlaying.title, artist: nowPlaying.artist },
          };
    const handle = createScene(canvas, options);
    return () => {
      cancelled = true;
      setReady(false);
      handle.dispose();
    };
  }, [createScene, pointerEnabled, nowPlaying]);

  return (
    <>
      <canvas ref={canvasRef} style={canvasStyle} />
      <div style={vignetteStyle} />
      {!ready && <LoadingOverlay />}
    </>
  );
}
