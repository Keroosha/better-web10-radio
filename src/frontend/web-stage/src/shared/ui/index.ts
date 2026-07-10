// Public API of the web-stage local `shared/ui` slice: overlay skin + layout system
// + the shared window frame. Keyframes live in `./overlay.css` (imported by StagePage).
export { getOverlayTheme, type StageTheme } from './theme';
export { getOverlayLayout, type StageLayout } from './layout';
export { OverlayWindow } from './OverlayWindow';
