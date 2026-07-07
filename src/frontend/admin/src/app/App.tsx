import type { ReactElement } from 'react';

import { SHARED_PACKAGE } from '@web10/shared';

export function App(): ReactElement {
  return (
    <main>
      <h1>Web10.Radio — Admin</h1>
      <p>Wired to shared package: {SHARED_PACKAGE}</p>
    </main>
  );
}
