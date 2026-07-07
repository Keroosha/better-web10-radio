import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, test } from 'vitest';

import { App } from './App';

afterEach(cleanup);

test('admin app renders and is wired to the shared package', () => {
  const { getByText } = render(<App />);
  // getByText throws if the node is absent, so a truthy match proves the render + wiring.
  expect(getByText(/@web10\/shared/)).toBeDefined();
});
