import type { ReactElement } from 'react';

import type { Banner, NowPlaying, DonationGoal, SocialLink, StreamStatus } from '@web10/shared';

import { getBannerPositionStyle } from '../../shared/ui/banner-position';
import { getOverlayTheme } from '../../shared/ui/theme';
import { DonationGoalWidget } from '../donation-goal';
import { FollowUsWidget } from '../follow-us';
import { NowPlayingWidget } from '../now-playing';
import { BannerCard } from './BannerCard';

interface BannersLayerProps {
  readonly banners: readonly Banner[];
  readonly nowPlaying: NowPlaying;
  readonly streamStatus: StreamStatus;
  readonly donationGoal: DonationGoal;
  readonly donationPercent: number;
  readonly socials: readonly SocialLink[];
}

/**
 * Renders every enabled admin-configured banner over the 3D scene. Each banner
 * resolves its own theme (from `style`) and absolute placement (from `screenPosition`);
 * the `nowplaying` / `donation` / `social` types reuse the existing overlay widgets so
 * their content stays live, while `custom` banners render free-form text.
 */
export function BannersLayer({
  banners,
  nowPlaying,
  streamStatus,
  donationGoal,
  donationPercent,
  socials,
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
            case 'custom':
              return (
                <BannerCard key={banner.id} banner={banner} theme={theme} windowStyle={windowStyle} />
              );
          }
        })}
    </>
  );
}
