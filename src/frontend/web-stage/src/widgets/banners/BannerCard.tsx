import type { CSSProperties, ReactElement } from 'react';

import type { Banner } from '@web10/shared';

import type { StageTheme } from '../../shared/ui/theme';
import { OverlayWindow } from '../../shared/ui/OverlayWindow';

interface BannerCardProps {
  readonly banner: Banner;
  readonly theme: StageTheme;
  readonly windowStyle: CSSProperties;
}

/**
 * A free-form `custom` banner: title bar + optional subtitle/body text with the
 * banner's accent colour. Uses the shared {@link OverlayWindow} frame so it matches
 * the other overlay widgets in either theme.
 */
export function BannerCard({ banner, theme, windowStyle }: BannerCardProps): ReactElement {
  return (
    <OverlayWindow title={banner.title} theme={theme} windowStyle={windowStyle}>
      {banner.subtitle ? (
        <div style={{ fontWeight: 700, color: banner.accent || theme.accent }}>{banner.subtitle}</div>
      ) : null}
      {banner.text ? <div style={{ fontSize: '13px' }}>{banner.text}</div> : null}
    </OverlayWindow>
  );
}
