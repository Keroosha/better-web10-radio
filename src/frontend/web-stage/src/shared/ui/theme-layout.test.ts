import { describe, expect, test } from 'vitest';

import { getOverlayLayout } from './layout';
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

describe('getOverlayLayout', () => {
  test('corners stacks widgets absolutely, limit 4', () => {
    const l = getOverlayLayout('corners');
    expect(l.container.display).toBeUndefined();
    expect(l.donation.position).toBe('absolute');
    expect(l.messageLimit).toBe(4);
  });

  test('sidebar is a flex column, limit 4', () => {
    const l = getOverlayLayout('sidebar');
    expect(l.container.display).toBe('flex');
    expect(l.container.flexDirection).toBe('column');
    expect(l.donation.position).toBe('relative');
    expect(l.messageLimit).toBe(4);
  });

  test('bottombar is a flex row, limit 3', () => {
    const l = getOverlayLayout('bottombar');
    expect(l.container.display).toBe('flex');
    expect(l.container.flexDirection).toBe('row');
    expect(l.messageLimit).toBe(3);
  });

  test('NOW PLAYING is pinned top-centre in every layout', () => {
    for (const layout of ['corners', 'sidebar', 'bottombar'] as const) {
      const l = getOverlayLayout(layout);
      expect(l.now.position).toBe('absolute');
      expect(l.now.top).toBe('16px');
    }
  });
});
