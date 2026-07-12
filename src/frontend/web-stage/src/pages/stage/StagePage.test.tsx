import { render, screen, act, waitFor, cleanup } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest';

import type { FetchImpl } from '@web10/shared';

import type { SceneFactory } from '../../widgets/stage-scene/StageScene';
import { donation, validPlayerState } from '../../testing/fixtures';
import { fakeStateFetch, makeFakeConnector } from '../../testing/sse';
import { StagePage } from './StagePage';

// A scene factory that reports ready immediately and touches no WebGL.
const fakeScene: SceneFactory = (_canvas, options) => {
  options.onReady?.();
  return { resize: (): void => {}, dispose: (): void => {} };
};

const rejectingFetch: FetchImpl = () => Promise.reject(new Error('offline'));

beforeEach(() => {
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined);
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('StagePage', () => {
  test('renders from the empty/offline default (stage stays alive)', () => {
    const fake = makeFakeConnector();
    render(<StagePage createScene={fakeScene} connector={fake.connector} fetchImpl={rejectingFetch} />);

    // Canvas + the always-present panels render even with no data.
    expect(document.querySelector('canvas')).not.toBeNull();
    expect(screen.getByText('OFFLINE')).toBeTruthy();
    expect(screen.getByText('DONATION GOAL')).toBeTruthy();
    expect(screen.getByText('SUPER CHAT')).toBeTruthy();
    // FOLLOW US hides itself when there are no socials.
    expect(screen.queryByText('FOLLOW US')).toBeNull();
  });

  test('capture mode hides the document cursor and cleans up on exit', () => {
    const fake = makeFakeConnector();
    const view = render(
      <StagePage
        createScene={fakeScene}
        connector={fake.connector}
        fetchImpl={rejectingFetch}
        captureEnabled
      />,
    );
    expect(document.documentElement.classList.contains('capture-mode')).toBe(true);
    view.rerender(
      <StagePage
        createScene={fakeScene}
        connector={fake.connector}
        fetchImpl={rejectingFetch}
        captureEnabled={false}
      />,
    );
    expect(document.documentElement.classList.contains('capture-mode')).toBe(false);
    view.unmount();
    expect(document.documentElement.classList.contains('capture-mode')).toBe(false);
  });

  test('renders live data and fires a donation toast on a new donation', async () => {
    const fake = makeFakeConnector();
    render(
      <StagePage
        createScene={fakeScene}
        connector={fake.connector}
        fetchImpl={fakeStateFetch(validPlayerState())}
      />,
    );

    await waitFor(() => expect(screen.getByText('LIVE')).toBeTruthy());
    expect(screen.getByText('FOLLOW US')).toBeTruthy();
    expect(screen.getByText('CyberDove')).toBeTruthy();

    act(() =>
      fake.emit('player.donation', {
        ...validPlayerState().donationGoal,
        recent: [donation('fresh', 'newcomer', 50), donation('old', 'seeded', 10)],
      }),
    );
    expect(screen.getByText('НОВЫЙ ДОНАТ')).toBeTruthy();
    // 'newcomer' now appears in both the toast and the recent-donations list.
    expect(screen.getAllByText(/newcomer/).length).toBeGreaterThan(0);
  });
});
