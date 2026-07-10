import { render, screen, fireEvent, act, cleanup } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest';

import type { StreamStatus } from '@web10/shared';

import { getOverlayTheme } from '../../shared/ui/theme';
import { StreamAudio } from './StreamAudio';

// jsdom does not implement HTMLMediaElement.play; stub it to a resolved promise.
let playSpy: ReturnType<typeof vi.spyOn>;
beforeEach(() => {
  playSpy = vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined);
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function setup(streamStatus: StreamStatus, captureEnabled: boolean) {
  return render(
    <StreamAudio
      streamStatus={streamStatus}
      captureEnabled={captureEnabled}
      theme={getOverlayTheme('aero')}
    />,
  );
}

function audioIn(container: HTMLElement): HTMLAudioElement {
  const audio = container.querySelector<HTMLAudioElement>('audio');
  if (audio === null) {
    throw new Error('no <audio> rendered');
  }
  return audio;
}

describe('StreamAudio', () => {
  test('offline: no src, no play attempt, no control', () => {
    const { container } = setup('offline', false);
    expect(audioIn(container).getAttribute('src')).toBeNull();
    expect(playSpy).not.toHaveBeenCalled();
    expect(screen.queryByRole('button')).toBeNull();
  });

  test('live viewer: sets stream src, attempts play, starts muted, control present', () => {
    const { container } = setup('live', false);
    const audio = audioIn(container);
    expect(audio.getAttribute('src')).toBe('/api/v0/player/stream');
    expect(playSpy).toHaveBeenCalled();
    expect(audio.muted).toBe(true);
    expect(screen.getByRole('button')).toBeTruthy();
  });

  test('unmute pill toggles muted', () => {
    const { container } = setup('live', false);
    const audio = audioIn(container);
    expect(audio.muted).toBe(true);
    fireEvent.click(screen.getByRole('button'));
    expect(audio.muted).toBe(false);
  });

  test('capture mode: silent, no src, no control even when live', () => {
    const { container } = setup('live', true);
    expect(audioIn(container).getAttribute('src')).toBeNull();
    expect(playSpy).not.toHaveBeenCalled();
    expect(screen.queryByRole('button')).toBeNull();
  });

  test('media error is handled and surfaced on the control title', () => {
    const { container } = setup('live', false);
    const audio = audioIn(container);
    act(() => {
      audio.dispatchEvent(new Event('error'));
    });
    expect(screen.getByRole('button').getAttribute('title')).toBe('stream unavailable');
  });
});
