import { act, cleanup, render } from '@testing-library/react';
import { StrictMode } from 'react';
import { afterEach, expect, test } from 'vitest';

import type { NowPlaying } from '@web10/shared';

import type { RadioSceneOptions } from './createRadioScene';
import { StageScene, type SceneFactory } from './StageScene';

afterEach(cleanup);

// A fake scene factory that records lifecycle calls instead of touching WebGL (jsdom has
// no WebGL/2D canvas). This is the injection seam StageScene exposes via `createScene`.
interface Recorder {
  readonly factory: SceneFactory;
  builds: number;
  disposes: number;
  lastOptions: RadioSceneOptions | null;
  fireReady: () => void;
}

function makeRecorder(): Recorder {
  const rec: Recorder = {
    builds: 0,
    disposes: 0,
    lastOptions: null,
    fireReady: () => {},
    factory: (_canvas, options) => {
      rec.builds += 1;
      rec.lastOptions = options;
      rec.fireReady = (): void => options.onReady?.();
      return {
        resize: (): void => {},
        dispose: (): void => {
          rec.disposes += 1;
        },
      };
    },
  };
  return rec;
}

const NOW_PLAYING: NowPlaying = {
  trackId: '01920000-0000-7000-8000-000000000001',
  title: 'リサフランク420 / 現代のコンピュー',
  artist: 'Macintosh Plus',
  album: 'FLORAL SHOPPE',
  source: 'library',
  externalUrl: 'https://example.com/track',
  coverImageUrl: '/api/v0/player/assets/cover/x',
  durationMs: 240000,
  positionMs: 42000,
  startedAtUtc: '2026-07-07T00:00:00Z',
};

test('the loading overlay shows until the scene reports its first frame', () => {
  const rec = makeRecorder();
  const { queryByText } = render(<StageScene createScene={rec.factory} />);
  expect(rec.builds).toBe(1);
  expect(queryByText('web1radio.exe')).not.toBeNull();
  act(() => {
    rec.fireReady();
  });
  expect(queryByText('web1radio.exe')).toBeNull();
});

test('unmounting disposes the scene exactly once', () => {
  const rec = makeRecorder();
  const { unmount } = render(<StageScene createScene={rec.factory} />);
  expect(rec.disposes).toBe(0);
  unmount();
  expect(rec.builds).toBe(1);
  expect(rec.disposes).toBe(1);
});

test('every scene build is paired with a dispose under StrictMode (no leak)', () => {
  const rec = makeRecorder();
  const { unmount } = render(
    <StrictMode>
      <StageScene createScene={rec.factory} />
    </StrictMode>,
  );
  unmount();
  // Whether or not StrictMode double-invokes the effect, every build must be torn down.
  expect(rec.builds).toBeGreaterThanOrEqual(1);
  expect(rec.disposes).toBe(rec.builds);
});

test('defaults to parallax on with no track, and maps nowPlaying to a scene track', () => {
  const withDefaults = makeRecorder();
  render(<StageScene createScene={withDefaults.factory} />);
  expect(withDefaults.lastOptions?.pointerEnabled).toBe(true);
  expect(withDefaults.lastOptions?.track).toBeUndefined();

  const withTrack = makeRecorder();
  render(
    <StageScene
      createScene={withTrack.factory}
      nowPlaying={NOW_PLAYING}
      pointerEnabled={false}
    />,
  );
  expect(withTrack.lastOptions?.pointerEnabled).toBe(false);
  expect(withTrack.lastOptions?.track).toEqual({
    title: NOW_PLAYING.title,
    artist: NOW_PLAYING.artist,
    coverImageUrl: NOW_PLAYING.coverImageUrl,
  });
});

test('progress-only snapshots keep the same WebGL scene', () => {
  const rec = makeRecorder();
  const { rerender } = render(<StageScene createScene={rec.factory} nowPlaying={NOW_PLAYING} />);
  rerender(
    <StageScene
      createScene={rec.factory}
      nowPlaying={{ ...NOW_PLAYING, positionMs: NOW_PLAYING.positionMs + 1000 }}
    />,
  );
  expect(rec.builds).toBe(1);
  expect(rec.disposes).toBe(0);
});
