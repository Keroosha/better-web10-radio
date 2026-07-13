import type { ReactElement } from 'react';

import type {
  Banner,
  NowPlaying,
  DonationGoal,
  SocialLink,
  StreamStatus,
  SuperChatMessage,
} from '@web10/shared';

import { getBannerPositionStyle } from '../../shared/ui/banner-position';
import { getOverlayTheme } from '../../shared/ui/theme';
import { DonationGoalWidget } from '../donation-goal';
import { FollowUsWidget } from '../follow-us';
import { NowPlayingWidget } from '../now-playing';
import { SuperChatWidget } from '../super-chat';
import { BannerCard } from './BannerCard';

interface BannersLayerProps {
  readonly banners: readonly Banner[];
  readonly nowPlaying: NowPlaying;
  readonly streamStatus: StreamStatus;
  readonly donationGoal: DonationGoal;
  readonly donationPercent: number;
  readonly socials: readonly SocialLink[];
  readonly superChatMessages: readonly SuperChatMessage[];
}

/**
 * Renders every enabled admin-configured banner over the 3D scene. Each banner
 * resolves its own theme (from `style`) and absolute placement (from `screenPosition`);
 * `nowplaying`, `donation`, and `social` reuse live overlay widgets, while `superchat`
 * and `custom` respectively render approved messages and free-form text.
 */
export function BannersLayer({
  banners,
  nowPlaying,
  streamStatus,
  donationGoal,
  donationPercent,
  socials,
  superChatMessages,
}: BannersLayerProps): ReactElement {
  return (
    <>
      {banners
        .filter((banner) => banner.enabled)
        .map((banner) => {
          const theme = getOverlayTheme(banner.style);
          const windowStyle = { ...theme.win, ...getBannerPositionStyle(banner.screenPosition) };
          switch (banner.type) {
            case 'nowplaying':
              return (
                <NowPlayingWidget
                  key={banner.id}
                  nowPlaying={nowPlaying}
                  streamStatus={streamStatus}
                  theme={theme}
                  windowStyle={windowStyle}
                  label={banner.subtitle === '' ? banner.title : `${banner.title} · ${banner.subtitle}`}
                />
              );
            case 'donation':
              return (
                <DonationGoalWidget
                  key={banner.id}
                  donationGoal={donationGoal}
                  percent={donationPercent}
                  theme={theme}
                  windowStyle={windowStyle}
                  title={banner.title}
                />
              );
            case 'social':
              return (
                <FollowUsWidget
                  key={banner.id}
                  socials={socials}
                  theme={theme}
                  windowStyle={windowStyle}
                  title={banner.title}
                  rotationSeconds={banner.rotationSeconds}
                />
              );
            case 'superchat':
              return (
                <SuperChatWidget
                  key={banner.id}
                  title={banner.title}
                  messages={superChatMessages}
                  theme={theme}
                  windowStyle={windowStyle}
                />
              );
            case 'custom':
              return (
                <BannerCard key={banner.id} banner={banner} theme={theme} windowStyle={windowStyle} />
              );
          }
        })}
    </>
  );
}
