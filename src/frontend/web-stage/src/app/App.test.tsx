import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import { App } from './App';

// The real scene needs a WebGL context, which jsdom lacks; stub the factory so <App/>
// mounts. `vi.mock` is hoisted above the imports by Vitest.
vi.mock('../widgets/stage-scene/createRadioScene', () => ({
  createRadioScene: () => ({ resize: (): void => {}, dispose: (): void => {} }),
}));

afterEach(cleanup);

test('web-stage app mounts the stage scene', () => {
  const { container, getByText } = render(<App />);
  expect(container.querySelector('canvas')).not.toBeNull();
  // The stub never reports ready, so the loading window stays visible.
  expect(getByText('web1radio.exe')).toBeDefined();
});
