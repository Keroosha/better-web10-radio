import { describe, expect, test } from 'vitest';

import { getSuperChatMessageLimit } from './layout';
import { getOverlayTheme } from './theme';

describe('getOverlayTheme', () => {
  test('aero and win9x are distinct skins', () => {
    const aero = getOverlayTheme('aero');
    const win9x = getOverlayTheme('win9x');
    expect(aero.accent).toBe('#0a86c9');
    expect(win9x.accent).toBe('#000080');
    expect(aero.win).not.toEqual(win9x.win);
  });
});

describe('getSuperChatMessageLimit', () => {
  test('corners and sidebar preserve the four-message cap', () => {
    expect(getSuperChatMessageLimit('corners')).toBe(4);
    expect(getSuperChatMessageLimit('sidebar')).toBe(4);
  });

  test('bottombar preserves the three-message cap', () => {
    expect(getSuperChatMessageLimit('bottombar')).toBe(3);
  });
});
