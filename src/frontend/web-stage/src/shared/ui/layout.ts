// Typed port of the mock's `renderVals()` positioning (Web 1.0 Radio Scene.dc.html,
// ~L699-720). Three widget arrangements keyed by the DTO's `overlay.layout`. The
// container is `pointer-events:none`; each widget slot re-enables `pointer-events:auto`
// so mouse parallax still reaches the 3D canvas through the gaps.
import type { CSSProperties } from 'react';
import type { OverlayLayout } from '@web10/shared';

export interface StageLayout {
  /** Wrapper spanning the viewport; holds all four widgets. */
  readonly container: CSSProperties;
  /** NOW PLAYING slot — always pinned top-centre. */
  readonly now: CSSProperties;
  /** DONATION GOAL slot. */
  readonly donation: CSSProperties;
  /** SUPER CHAT slot. */
  readonly superChat: CSSProperties;
  /** FOLLOW US slot. */
  readonly social: CSSProperties;
  /** Approved-message cap (mock: 3 for bottombar, else 4). */
  readonly messageLimit: number;
}

const AUTO: CSSProperties = { pointerEvents: 'auto' };

// NOW PLAYING is absolute top-centre in every layout.
const NOW: CSSProperties = {
  position: 'absolute',
  top: '16px',
  left: '50%',
  transform: 'translateX(-50%)',
  maxWidth: '92vw',
  zIndex: 5,
  ...AUTO,
};

const SIDEBAR: StageLayout = {
  container: {
    position: 'absolute',
    inset: 0,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    gap: '14px',
    padding: '78px 18px 18px',
    zIndex: 4,
    pointerEvents: 'none',
  },
  now: NOW,
  donation: { position: 'relative', width: '320px', ...AUTO },
  superChat: { position: 'relative', width: '320px', ...AUTO },
  social: { position: 'relative', width: '320px', ...AUTO },
  messageLimit: 4,
};

const BOTTOMBAR: StageLayout = {
  container: {
    position: 'absolute',
    inset: 0,
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'flex-end',
    justifyContent: 'center',
    gap: '16px',
    padding: '18px',
    zIndex: 4,
    pointerEvents: 'none',
  },
  now: NOW,
  donation: { position: 'relative', width: 'clamp(240px,30%,330px)', ...AUTO },
  superChat: { position: 'relative', width: 'clamp(240px,30%,330px)', ...AUTO },
  social: { position: 'relative', width: 'clamp(240px,30%,330px)', ...AUTO },
  messageLimit: 3,
};

const CORNERS: StageLayout = {
  container: { position: 'absolute', inset: 0, zIndex: 4, pointerEvents: 'none' },
  now: NOW,
  donation: { position: 'absolute', top: '18px', left: '18px', width: '300px', ...AUTO },
  superChat: { position: 'absolute', bottom: '18px', left: '18px', width: '312px', ...AUTO },
  social: { position: 'absolute', bottom: '18px', right: '18px', width: '272px', ...AUTO },
  messageLimit: 4,
};

/** Resolve the widget arrangement for the current `overlay.layout`. */
export function getOverlayLayout(layout: OverlayLayout): StageLayout {
  switch (layout) {
    case 'sidebar':
      return SIDEBAR;
    case 'bottombar':
      return BOTTOMBAR;
    case 'corners':
      return CORNERS;
  }
}
