import type { CSSProperties, ReactElement, ReactNode } from 'react';

import type { StageTheme } from './theme';

interface OverlayWindowProps {
  /** Title-bar caption (e.g. "DONATION GOAL"). */
  readonly title: string;
  readonly theme: StageTheme;
  /** `{...theme.win, ...layout.<slot>}` — frame styling + slot position. */
  readonly windowStyle: CSSProperties;
  readonly children: ReactNode;
}

/**
 * The titled glass/Win9x frame shared by DONATION GOAL, SUPER CHAT and FOLLOW US
 * (NOW PLAYING and the donation toast are bare frames and do not use it). The `×`
 * button is decorative — the stage is a read-only broadcast surface.
 */
export function OverlayWindow({
  title,
  theme,
  windowStyle,
  children,
}: OverlayWindowProps): ReactElement {
  return (
    <div style={windowStyle}>
      <div style={theme.title}>
        <span>{title}</span>
        <span style={theme.btn} aria-hidden="true">
          ×
        </span>
      </div>
      <div style={theme.body}>{children}</div>
    </div>
  );
}
