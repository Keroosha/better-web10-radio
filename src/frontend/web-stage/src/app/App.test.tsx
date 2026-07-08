import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, test } from 'vitest';

import { App } from './App';

afterEach(cleanup);

test('web-stage app renders and is wired to the shared package', () => {
  const { getByText } = render(<App />);
  // getByText throws if the node is absent; the formatted label comes from
  // @web10/shared, so a match proves both the render and the wiring.
  expect(getByText('Macintosh Plus — FLORAL SHOPPE')).toBeDefined();
});
