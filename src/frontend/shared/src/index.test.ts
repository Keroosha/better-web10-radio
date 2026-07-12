import { describe, expect, test } from 'vitest';

import * as shared from './index';

const id = '0197f0a1-0000-7000-8000-000000000000';

describe('@web10/shared public contracts', () => {
  test('keeps the public player stream path and SSE event vocabulary stable', () => {
    expect(shared.playerStreamUrl()).toBe('/api/v0/player/stream');
    expect(shared.PLAYER_EVENT_NAMES).toEqual([
      'player.state',
      'player.queue',
      'player.say',
      'player.donation',
      'player.health',
    ]);
  });

  test('makes every pinned admin response schema available to app workspaces', () => {
    expect(
      shared.AdminSessionSchema.parse({
        username: 'operator',
        csrfToken: 'csrf-token',
        developmentFixturesEnabled: true,
      }).username,
    ).toBe('operator');
    expect(shared.LibraryScanAcceptedSchema.parse({ scanJobId: id }).scanJobId).toBe(id);
    expect(
      shared.LibraryScanStatusSchema.parse({
        scanJobId: id,
        status: 'queued',
        discoveredCount: 0,
        requestedAtUtc: '2026-07-10T12:00:00Z',
        startedAtUtc: null,
        finishedAtUtc: null,
        failureReason: null,
      }).status,
    ).toBe('queued');
    expect(
      shared.AdminTrackSchema.parse({
        id,
        title: 'The track',
        artist: '',
        album: '',
        durationMs: 0,
        hasCachedFile: true,
        coverImageUrl: '',
        metadataSource: 'filename',
      }).hasCachedFile,
    ).toBe(true);
    expect(
      shared.PlaylistSchema.parse({
        id,
        name: 'Night shift',
        description: null,
        isActive: true,
        type: 'general',
        source: 'manual',
        order: 'sequential',
        weight: 3,
        isJingle: false,
        interrupt: false,
        avoidDuplicates: true,
        playEverySongs: null,
        playEveryMinutes: null,
        playAtMinute: null,
        schedules: [],
        isSystem: false,
        itemCount: 0,
      }).isActive,
    ).toBe(true);
    expect(
      shared.PlaylistItemSchema.parse({
        id,
        trackId: '0197f0a2-0000-7000-8000-000000000000',
        title: 'The track',
        artist: '',
        position: 0,
      }).position,
    ).toBe(0);
    expect(
      shared.StorageSchema.parse({
        defaultBackend: {
          type: 'local',
          localRoot: '/storage',
          s3Bucket: null,
          s3Region: null,
          s3ServiceUrl: null,
          s3ForcePathStyle: false,
        },
        additionalBackends: [],
      }).defaultBackend.type,
    ).toBe('local');
    expect(
      shared.StreamNodeStatusSchema.parse({
        status: 'offline',
        desiredState: 'stopped',
        lastHeartbeatUtc: null,
        failureReason: null,
        bitrateKbps: 0,
        restartGeneration: 0,
      }).status,
    ).toBe('offline');
    expect(shared.StreamNodeControlSchema.parse({ desiredState: 'running', restartGeneration: 0 }).desiredState).toBe(
      'running',
    );
    expect(
      shared.PaidVerticalSliceFixtureSchema.parse({
        donationPaymentId: id,
        sayPaymentId: '0197f0a2-0000-7000-8000-000000000000',
        sayMessageId: '0197f0a3-0000-7000-8000-000000000000',
      }).sayMessageId,
    ).toBe('0197f0a3-0000-7000-8000-000000000000');
  });
});
