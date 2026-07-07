import { expect, test } from 'vitest';

import { SHARED_PACKAGE } from './index';

test('shared package exposes its public identifier', () => {
  expect(SHARED_PACKAGE).toBe('@web10/shared');
});
