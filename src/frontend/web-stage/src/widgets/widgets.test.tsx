import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import { afterEach, describe, expect, test } from 'vitest';

import {
  createEmptyPlayerState,
  selectApprovedMessages,
  selectDonationPercent,
} from '../entities/player-state';
import { getBannerPositionStyle } from '../shared/ui/banner-position';
import { getOverlayTheme } from '../shared/ui/theme';
import { message, validPlayerState } from '../testing/fixtures';
import { DonationGoalWidget } from './donation-goal';
import { FollowUsWidget } from './follow-us';
import { NowPlayingWidget } from './now-playing';
import { SuperChatWidget } from './super-chat';

afterEach(cleanup);

const theme = getOverlayTheme('aero');
const nowPlayingStyle = getBannerPositionStyle('top-center');
const donationStyle = getBannerPositionStyle('top-left');
const superChatStyle = getBannerPositionStyle('bottom-left');
const socialStyle = getBannerPositionStyle('bottom-right');

describe('NowPlayingWidget', () => {
  test('shows LIVE and the track when the stream is live', () => {
    const state = validPlayerState();
    render(
      <NowPlayingWidget
        nowPlaying={state.nowPlaying}
        streamStatus="live"
        theme={theme}
        windowStyle={nowPlayingStyle}
      />,
    );
    expect(screen.getByText('LIVE')).toBeTruthy();
    expect(screen.getByText(state.nowPlaying.title)).toBeTruthy();
  });

  test('shows cover art, album and formatted progress', () => {
    const state = validPlayerState();
    render(
      <NowPlayingWidget
        nowPlaying={state.nowPlaying}
        streamStatus="live"
        theme={theme}
        windowStyle={nowPlayingStyle}
      />,
    );
    expect(screen.getByAltText(`${state.nowPlaying.title} cover art`)).toBeTruthy();
    expect(screen.getByText(state.nowPlaying.album)).toBeTruthy();
    expect(screen.getByTestId('now-playing-progress').textContent).toBe('00:42 / 04:00');
  });

  test('broken cover art falls back to the channel glyph', () => {
    const state = validPlayerState();
    render(
      <NowPlayingWidget
        nowPlaying={state.nowPlaying}
        streamStatus="live"
        theme={theme}
        windowStyle={nowPlayingStyle}
      />,
    );
    fireEvent.error(screen.getByRole('img'));
    expect(screen.queryByRole('img')).toBeNull();
    expect(screen.getByText('◈')).toBeTruthy();
  });

  test('offline: shows OFFLINE and channel fallback text', () => {
    render(
      <NowPlayingWidget
        nowPlaying={createEmptyPlayerState().nowPlaying}
        streamStatus="offline"
        theme={theme}
        windowStyle={nowPlayingStyle}
      />,
    );
    expect(screen.getByText('OFFLINE')).toBeTruthy();
    expect(screen.getByText('@netscapedidnothingwrong')).toBeTruthy();
    expect(screen.queryByText('LIVE')).toBeNull();
  });
});

describe('DonationGoalWidget', () => {
  test('renders top donator, goal and recent donations', () => {
    const state = validPlayerState();
    render(
      <DonationGoalWidget
        donationGoal={state.donationGoal}
        percent={selectDonationPercent(state)}
        theme={theme}
        windowStyle={donationStyle}
      />,
    );
    expect(screen.getByText('CyberDove')).toBeTruthy();
    expect(screen.getByText('neonghost')).toBeTruthy();
    expect(screen.getByText(/цель/)).toBeTruthy();
  });

  test('empty: no top donator shows a dash and no recent rows', () => {
    const empty = createEmptyPlayerState();
    render(
      <DonationGoalWidget
        donationGoal={empty.donationGoal}
        percent={selectDonationPercent(empty)}
        theme={theme}
        windowStyle={donationStyle}
      />,
    );
    expect(screen.getByText('—')).toBeTruthy();
    expect(screen.getByText('0% собрано')).toBeTruthy();
  });
});

describe('SuperChatWidget', () => {
  test('renders approved messages', () => {
    const messages = [message('m1', 'vhs_wanderer', 'approved')];
    render(<SuperChatWidget messages={messages} theme={theme} windowStyle={superChatStyle} />);
    expect(screen.getByText('vhs_wanderer')).toBeTruthy();
  });

  test('empty: renders the titled block with no placeholder text', () => {
    const empty = selectApprovedMessages(createEmptyPlayerState(), 4);
    render(<SuperChatWidget messages={empty} theme={theme} windowStyle={superChatStyle} />);
    expect(screen.getByText('SUPER CHAT')).toBeTruthy();
    expect(screen.queryByText('Пока тихо…')).toBeNull();
  });
});

describe('FollowUsWidget', () => {
  test('renders nothing when there are no socials', () => {
    const { container } = render(
      <FollowUsWidget socials={[]} theme={theme} windowStyle={socialStyle} />,
    );
    expect(container.firstChild).toBeNull();
  });

  test('featured chip is fully opaque, others dimmed', () => {
    const socials = validPlayerState().socials;
    const { container } = render(
      <FollowUsWidget socials={socials} theme={theme} windowStyle={socialStyle} />,
    );
    expect(screen.getByText(socials[0]?.name ?? '')).toBeTruthy();
    // The chip strip: first chip (featured index 0 initially) opaque, second dimmed.
    const chips = container.querySelectorAll<HTMLElement>('span[aria-hidden="true"]');
    const opacities = Array.from(chips).map((chip) => chip.style.opacity);
    expect(opacities).toContain('1');
    expect(opacities).toContain('0.4');
  });
});
