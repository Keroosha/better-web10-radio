// Absolute placement for admin-configured overlay banners. Mirrors the corners
// layout anchoring in layout.ts but keyed per-banner by `screenPosition`, so each
// banner positions itself independently over the 3D scene.
import type { CSSProperties } from 'react';
import type { BannerPosition } from '@web10/shared';

const ANCHORS: Record<BannerPosition, CSSProperties> = {
  'top-left': { top: '18px', left: '18px' },
  'top-center': { top: '16px', left: '50%', transform: 'translateX(-50%)' },
  'top-right': { top: '18px', right: '18px' },
  'bottom-left': { bottom: '18px', left: '18px' },
  'bottom-center': { bottom: '18px', left: '50%', transform: 'translateX(-50%)' },
  'bottom-right': { bottom: '18px', right: '18px' },
};

/** Resolve the absolute placement + pointer/z-index for one banner. */
export function getBannerPositionStyle(position: BannerPosition): CSSProperties {
  return {
    position: 'absolute',
    width: '300px',
    maxWidth: '92vw',
    pointerEvents: 'auto',
    zIndex: 5,
    ...ANCHORS[position],
  };
}
