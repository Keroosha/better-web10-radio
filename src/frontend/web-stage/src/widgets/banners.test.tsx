import { render, screen, cleanup } from '@testing-library/react';
import { afterEach, describe, expect, test } from 'vitest';

import type { Banner } from '@web10/shared';

import { selectDonationPercent } from '../entities/player-state';
import { validPlayerState } from '../testing/fixtures';
import { BannersLayer } from './banners';

afterEach(cleanup);

function renderLayer(banners: readonly Banner[]): void {
  const state = validPlayerState();
  render(
    <BannersLayer
      banners={banners}
      nowPlaying={state.nowPlaying}
      streamStatus="live"
      donationGoal={state.donationGoal}
      donationPercent={selectDonationPercent(state)}
      socials={state.socials}
      superChatMessages={state.superChat.messages}
    />,
  );
}

const customBanner: Banner = {
  id: 'b-custom',
  type: 'custom',
  title: 'GIVEAWAY',
  subtitle: '',
  text: 'Type /join in chat',
  style: 'win9x',
  screenPosition: 'bottom-center',
  accent: '#f39c12',
  enabled: true,
  sortOrder: 0,
  rotationSeconds: 0,
};

const superChatBanner: Banner = {
  id: 'b-superchat',
  type: 'superchat',
  title: 'SUPER CHAT',
  subtitle: '',
  text: '',
  style: 'aero',
  screenPosition: 'bottom-left',
  accent: '#e0439a',
  enabled: true,
  sortOrder: 0,
  rotationSeconds: 0,
};

describe('BannersLayer', () => {
  test('renders enabled banners by type and their live content', () => {
    const state = validPlayerState();
    renderLayer(state.banners);
    expect(screen.getByText('LIVE')).toBeTruthy();
    expect(screen.getByText('DONATION GOAL')).toBeTruthy();
    expect(screen.getByText('FOLLOW US')).toBeTruthy();
    expect(screen.getByText('SUPER CHAT')).toBeTruthy();
  });

  test('renders custom banner title and text', () => {
    renderLayer([customBanner]);

    expect(screen.getByText('GIVEAWAY')).toBeTruthy();
    expect(screen.getByText('Type /join in chat')).toBeTruthy();
  });

  test('skips disabled banners', () => {
    renderLayer([{ ...customBanner, enabled: false }]);

    expect(screen.queryByText('GIVEAWAY')).toBeNull();
  });

  test('renders approved messages through an enabled superchat banner', () => {
    renderLayer([superChatBanner]);

    expect(screen.getByText('SUPER CHAT')).toBeTruthy();
    expect(screen.getByText('vhs_wanderer')).toBeTruthy();
  });

  test('renders neither title nor messages through a disabled superchat banner', () => {
    renderLayer([{ ...superChatBanner, enabled: false }]);

    expect(screen.queryByText('SUPER CHAT')).toBeNull();
    expect(screen.queryByText('vhs_wanderer')).toBeNull();
  });
});
