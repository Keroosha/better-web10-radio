import type { OverlayLayout } from '@web10/shared';

/** Preserve the mock's layout-dependent superchat cap without prescribing banner placement. */
export function getSuperChatMessageLimit(layout: OverlayLayout): number {
  switch (layout) {
    case 'bottombar':
      return 3;
    case 'corners':
    case 'sidebar':
      return 4;
  }
}
