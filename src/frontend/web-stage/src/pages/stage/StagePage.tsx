import { useMemo, type ReactElement } from 'react';

import type { FetchImpl, OverlayLayout, OverlayStyle, SseConnector } from '@web10/shared';

import {
  selectApprovedMessages,
  selectDonationPercent,
  selectSceneTrack,
} from '../../entities/player-state';
import { DonationToast } from '../../features/donation-toast';
import { useStageState, type UseStageStateOptions } from '../../features/stage-state';
import { StreamAudio } from '../../features/stream-audio';
import { getOverlayLayout } from '../../shared/ui/layout';
import { getOverlayTheme } from '../../shared/ui/theme';
import { DonationGoalWidget } from '../../widgets/donation-goal';
import { FollowUsWidget } from '../../widgets/follow-us';
import { NowPlayingWidget } from '../../widgets/now-playing';
import {
  StageScene,
  type SceneFactory,
  type StageSceneProps,
} from '../../widgets/stage-scene/StageScene';
import { SuperChatWidget } from '../../widgets/super-chat';
import '../../shared/ui/overlay.css';

export interface StagePageProps {
  /** Scene factory override for tests (jsdom has no WebGL). */
  readonly createScene?: SceneFactory;
  /** SSE transport override for tests. */
  readonly connector?: SseConnector;
  /** `fetch` override for tests. */
  readonly fetchImpl?: FetchImpl;
  /** Stream-node kiosk capture mode: disables browser audio + pointer parallax. */
  readonly captureEnabled?: boolean;
}

interface StageParams {
  readonly style?: OverlayStyle;
  readonly layout?: OverlayLayout;
  readonly capture: boolean;
}

/**
 * URL params: `?capture=1` (all envs — the stream-node kiosk URL sets it) and, in dev
 * only, `?overlayStyle=` / `?overlayLayout=` to force a skin/layout for QA screenshots
 * (no admin route exposes these yet). Values are validated against the enums — no casts.
 */
function readStageParams(): StageParams {
  if (typeof window === 'undefined') {
    return { capture: false };
  }
  const params = new URLSearchParams(window.location.search);
  const base: StageParams = { capture: params.get('capture') === '1' };
  if (!import.meta.env.DEV) {
    return base;
  }
  const style = params.get('overlayStyle');
  const layout = params.get('overlayLayout');
  return {
    ...base,
    ...(style === 'aero' || style === 'win9x' ? { style } : {}),
    ...(layout === 'corners' || layout === 'sidebar' || layout === 'bottombar' ? { layout } : {}),
  };
}

/**
 * The public stage: the 3D scene with the four overlay widgets, donation toast and audio
 * layered on top, all driven by the single live `useStageState` hook. Renders correctly
 * from the empty/offline default (SPEC §10/§12) and never gates the scene on stream status.
 */
export function StagePage({
  createScene,
  connector,
  fetchImpl,
  captureEnabled,
}: StagePageProps): ReactElement {
  const options: UseStageStateOptions = {
    ...(connector ? { connector } : {}),
    ...(fetchImpl ? { fetchImpl } : {}),
  };
  const { state, newDonation } = useStageState(options);

  const params = readStageParams();
  const theme = getOverlayTheme(params.style ?? state.overlay.style);
  const layout = getOverlayLayout(params.layout ?? state.overlay.layout);
  const capture = captureEnabled ?? params.capture;

  // Stabilise the scene track on identity fields only — position ticks (new snapshots
  // every second) must NOT re-run StageScene's build effect, which would flash the loader.
  const { trackId, title, artist } = state.nowPlaying;
  const sceneTrack = useMemo(() => selectSceneTrack(state.nowPlaying), [trackId, title, artist]);

  const sceneProps: StageSceneProps = {
    pointerEnabled: !capture,
    ...(sceneTrack ? { nowPlaying: sceneTrack } : {}),
    ...(createScene ? { createScene } : {}),
  };

  return (
    <>
      <StageScene {...sceneProps} />
      <div style={layout.container}>
        <NowPlayingWidget
          nowPlaying={state.nowPlaying}
          streamStatus={state.stream.status}
          theme={theme}
          windowStyle={{ ...theme.win, ...layout.now }}
        />
        <DonationGoalWidget
          donationGoal={state.donationGoal}
          percent={selectDonationPercent(state)}
          theme={theme}
          windowStyle={{ ...theme.win, ...layout.donation }}
        />
        <SuperChatWidget
          messages={selectApprovedMessages(state, layout.messageLimit)}
          theme={theme}
          windowStyle={{ ...theme.win, ...layout.superChat }}
        />
        <FollowUsWidget
          socials={state.socials}
          theme={theme}
          windowStyle={{ ...theme.win, ...layout.social }}
        />
      </div>
      <DonationToast newDonation={newDonation} theme={theme} />
      <StreamAudio streamStatus={state.stream.status} captureEnabled={capture} theme={theme} />
    </>
  );
}
