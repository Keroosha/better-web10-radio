import type { ReactElement } from 'react';

import { formatTrackLabel } from '@web10/shared';

// F1 placeholder stage. The real Three.js scene and overlays arrive in F2/F3; this
// only proves the app is wired to the validated @web10/shared layer.
export function App(): ReactElement {
  return (
    <main>
      <h1>Web10.Radio — Stage</h1>
      <p>{formatTrackLabel('Macintosh Plus', 'FLORAL SHOPPE')}</p>
    </main>
  );
}
