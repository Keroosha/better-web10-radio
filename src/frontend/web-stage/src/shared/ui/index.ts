// Public API of the web-stage local `shared/ui` slice: overlay skin, configured banner
// placement, superchat message limits, and the shared window frame. Keyframes live in
// `./overlay.css` (imported by StagePage).
export { getOverlayTheme, type StageTheme } from './theme';
export { getSuperChatMessageLimit } from './layout';
export { OverlayWindow } from './OverlayWindow';
