import type { ReactElement } from 'react';

import { formatStars } from '@web10/shared';

// F1 placeholder cabinet. The real admin pages arrive in F4; this only proves the
// app is wired to the validated @web10/shared layer.
export function App(): ReactElement {
  return (
    <main>
      <h1>Web10.Radio — Admin</h1>
      <p>Raised: {formatStars(3820)} ⭐</p>
    </main>
  );
}
