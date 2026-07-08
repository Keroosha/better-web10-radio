import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, test } from 'vitest';

import { App } from './App';

afterEach(cleanup);

test('admin app renders and is wired to the shared package', () => {
  const { getByText } = render(<App />);
  // getByText throws if the node is absent; the grouped amount comes from
  // @web10/shared, so a match proves both the render and the wiring.
  expect(getByText(/3,820/)).toBeDefined();
});
