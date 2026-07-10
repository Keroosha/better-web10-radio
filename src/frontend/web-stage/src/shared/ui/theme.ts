// Typed port of the mock's `theme(os)` (Web 1.0 Radio Scene.dc.html, ~L662-688).
// Two overlay skins — Aero glass and Win9x — keyed by the DTO's `overlay.style`.
// Style objects are `React.CSSProperties`; `WebkitBackdropFilter` is a valid key, so
// the Aero blur ports with no cast.
import type { CSSProperties } from 'react';
import type { OverlayStyle } from '@web10/shared';

export interface StageTheme {
  /** The window frame. */
  readonly win: CSSProperties;
  /** Title bar. */
  readonly title: CSSProperties;
  /** The close (×) button chrome. */
  readonly btn: CSSProperties;
  /** Window body padding/colour. */
  readonly body: CSSProperties;
  /** Accent colour (eq bars, VT323 numerals, dots). */
  readonly accent: string;
  /** Row background. */
  readonly row: string;
  /** Highlighted row background (top donator). */
  readonly hiRow: string;
  /** Progress-bar track. */
  readonly barTrack: CSSProperties;
  /** Progress-bar fill (width is applied by the widget). */
  readonly barFill: CSSProperties;
}

const AERO_THEME: StageTheme = {
  win: {
    background: 'linear-gradient(180deg, rgba(255,255,255,0.62), rgba(210,240,255,0.42))',
    backdropFilter: 'blur(16px)',
    WebkitBackdropFilter: 'blur(16px)',
    border: '1px solid rgba(255,255,255,0.75)',
    borderRadius: '16px',
    boxShadow: '0 12px 36px rgba(40,100,150,0.28), inset 0 1px 0 rgba(255,255,255,0.95)',
    color: '#0c3550',
    overflow: 'hidden',
    fontFamily: "'Tahoma','Segoe UI',sans-serif",
  },
  title: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
    padding: '9px 12px',
    background: 'linear-gradient(180deg, rgba(255,255,255,0.92), rgba(174,220,255,0.62))',
    borderBottom: '1px solid rgba(255,255,255,0.6)',
    fontWeight: 700,
    fontSize: '11.5px',
    letterSpacing: '0.09em',
    color: '#0b3a5a',
    textShadow: '0 1px 0 rgba(255,255,255,0.8)',
  },
  btn: {
    width: '15px',
    height: '15px',
    borderRadius: '50%',
    background: 'radial-gradient(circle at 35% 28%, #ffffff, #86c6ff 58%, #2b8fe0)',
    boxShadow: 'inset 0 0 0 1px rgba(255,255,255,0.7), 0 1px 2px rgba(0,0,0,0.2)',
    flex: 'none',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '9px',
    lineHeight: 1,
    color: 'rgba(255,255,255,0.95)',
    fontWeight: 700,
    textShadow: '0 1px 1px rgba(0,0,0,0.25)',
  },
  body: { padding: '12px 13px 13px', color: '#0c3550' },
  accent: '#0a86c9',
  row: 'rgba(255,255,255,0.5)',
  hiRow: 'rgba(255,255,255,0.6)',
  barTrack: {
    height: '18px',
    borderRadius: '9px',
    background: 'rgba(10,50,80,0.16)',
    boxShadow: 'inset 0 1px 3px rgba(0,0,0,0.25)',
    padding: '3px',
    overflow: 'hidden',
  },
  barFill: {
    height: '100%',
    borderRadius: '6px',
    background: 'linear-gradient(180deg,#e0ffb0,#5fce55 55%,#3ba838)',
    boxShadow: '0 0 8px rgba(120,220,90,0.6), inset 0 1px 0 rgba(255,255,255,0.7)',
  },
};

const WIN9X_THEME: StageTheme = {
  win: {
    background: '#c3c7cb',
    boxShadow:
      'inset -1px -1px 0 #000, inset 1px 1px 0 #dfe3e6, inset -2px -2px 0 #85898c, inset 2px 2px 0 #fff',
    color: '#000',
    fontFamily: "'Tahoma','MS Sans Serif',sans-serif",
    padding: '3px',
  },
  title: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '6px',
    padding: '3px 4px 3px 6px',
    background: 'linear-gradient(90deg,#000080,#1084d0)',
    color: '#fff',
    fontWeight: 'bold',
    fontSize: '12px',
    letterSpacing: '0.02em',
  },
  btn: {
    width: '16px',
    height: '14px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: '#c3c7cb',
    boxShadow: 'inset -1px -1px 0 #000, inset 1px 1px 0 #fff, inset -2px -2px 0 #85898c',
    fontSize: '11px',
    color: '#000',
    fontWeight: 'bold',
    lineHeight: 1,
  },
  body: { padding: '9px 9px 10px', background: '#c3c7cb', color: '#000' },
  accent: '#000080',
  row: '#ffffff',
  hiRow: '#dfe3e6',
  barTrack: {
    height: '20px',
    background: '#fff',
    boxShadow: 'inset 1px 1px 0 #85898c, inset -1px -1px 0 #dfe3e6',
    padding: '2px',
    overflow: 'hidden',
  },
  barFill: {
    height: '100%',
    background: 'repeating-linear-gradient(90deg,#000080 0 12px,#1030a8 12px 15px)',
  },
};

/** Resolve the overlay skin for the current `overlay.style`. */
export function getOverlayTheme(style: OverlayStyle): StageTheme {
  return style === 'win9x' ? WIN9X_THEME : AERO_THEME;
}
