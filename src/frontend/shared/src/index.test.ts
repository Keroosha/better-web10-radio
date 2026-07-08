import { describe, expect, test } from 'vitest';

import * as shared from './index';

// Guards the public surface: everything F2/F3/F4 rely on must be exported from the
// single entry point (the `exports` map blocks deep imports).
describe('@web10/shared public surface', () => {
  test('exposes the player client, sse client, and formatters', () => {
    expect(typeof shared.getPlayerState).toBe('function');
    expect(typeof shared.playerStreamUrl).toBe('function');
    expect(typeof shared.createPlayerEventsClient).toBe('function');
    expect(typeof shared.formatStars).toBe('function');
    expect(typeof shared.formatDuration).toBe('function');
    expect(typeof shared.formatTrackLabel).toBe('function');
    expect(shared.ApiError).toBeTypeOf('function');
  });

  test('exposes the five SSE event names verbatim', () => {
    expect(shared.PLAYER_EVENT_NAMES).toEqual([
      'player.state',
      'player.queue',
      'player.say',
      'player.donation',
      'player.health',
    ]);
  });
});
