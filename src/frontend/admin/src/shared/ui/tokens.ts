// The admin cabinet palette + reusable inline-style fragments, taken verbatim from
// the design system screen in the mock (docs ПРАВИЛА-UI.md §7). Keeping them here
// stops screens from inventing new ad-hoc colours.
import type { CSSProperties } from 'react';

export const COLORS = {
  panelBorder: '#cddff0',
  panelBg: '#fafcff',
  selection: 'linear-gradient(#e3f0fb,#c9e2fb)',
  progress: 'linear-gradient(#aef0ae,#3caf3c)',
  live: '#2b7a2b',
  starting: '#b8860b',
  error: '#c0392b',
  offline: '#888',
  muted: '#789',
  subtle: '#567',
} as const;

/** A titled content card: `#fafcff` panel with the standard border/radius. */
export const panel: CSSProperties = {
  border: `1px solid ${COLORS.panelBorder}`,
  borderRadius: '8px',
  padding: '14px',
  background: COLORS.panelBg,
};

/** The `label / control` form grid (ПРАВИЛА §5). */
export const formGrid: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'auto 1fr',
  gap: '9px 12px',
  alignItems: 'center',
};

/** Compact icon/text button that avoids 7.css's 75px min-width in list rows. */
export const iconButton: CSSProperties = {
  minWidth: 0,
  padding: '2px 8px',
};

/** A single list row: content left (flex:1), actions right. */
export const listRow: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: '8px',
  padding: '5px 6px',
  borderRadius: '3px',
};

export const ellipsis: CSSProperties = {
  whiteSpace: 'nowrap',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
};

/** Status dot + colour for a stream/node status. */
export function statusColor(status: string): string {
  switch (status) {
    case 'live':
    case 'playing':
      return COLORS.live;
    case 'starting':
    case 'paused':
      return COLORS.starting;
    case 'degraded':
    case 'error':
    case 'failed':
      return COLORS.error;
    default:
      return COLORS.offline;
  }
}
