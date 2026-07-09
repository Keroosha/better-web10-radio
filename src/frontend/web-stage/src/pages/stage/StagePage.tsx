import type { ReactElement } from 'react';

import { StageScene } from '../../widgets/stage-scene/StageScene';

// The public stage page. In F2 it is just the fullscreen scene; the overlay widgets
// (NOW PLAYING / DONATION / SUPER CHAT / FOLLOW US) layer on here in F3.
export function StagePage(): ReactElement {
  return <StageScene />;
}
