import { describe, expect, test } from 'vitest';

import {
  AdminSessionSchema,
  AdminTrackSchema,
  BannerReplaceItemSchema,
  LibraryScanAcceptedSchema,
  LibraryScanStatusSchema,
  PaidVerticalSliceFixtureSchema,
  PlaylistItemSchema,
  PlaylistSchema,
  StorageSchema,
  StreamNodeControlSchema,
  StreamNodeStatusSchema,
} from './admin';

const id = '0197f0a1-0000-7000-8000-000000000000';
const otherId = '0197f0a2-0000-7000-8000-000000000000';

function expectStrictObject(
  schema: { safeParse: (value: object) => { success: boolean } },
  value: object,
): void {
  expect(schema.safeParse({ ...value, unpinned: true }).success).toBe(false);
}

describe('BannerReplaceItemSchema', () => {
  test('accepts a create row with a null id and nullable fields', () => {
    const row = {
      id: null,
      type: 'social',
      title: 'FOLLOW US',
      subtitle: null,
      text: null,
      style: 'aero',
      screenPosition: 'bottom-right',
      accent: null,
      enabled: true,
      rotationSeconds: 5,
    };

    expect(BannerReplaceItemSchema.parse(row)).toEqual(row);
    expect(BannerReplaceItemSchema.parse({ ...row, rotationSeconds: null }).rotationSeconds).toBeNull();
    expect(BannerReplaceItemSchema.safeParse({ ...row, rotationSeconds: 1 }).success).toBe(false);
    expect(BannerReplaceItemSchema.safeParse({ ...row, rotationSeconds: 121 }).success).toBe(false);
    expect(BannerReplaceItemSchema.safeParse({ ...row, type: 'ticker' }).success).toBe(false);
    expect(BannerReplaceItemSchema.safeParse({ ...row, screenPosition: 'middle' }).success).toBe(false);
    expectStrictObject(BannerReplaceItemSchema, row);
  });
});

describe('pinned admin DTO schemas', () => {
  test('accepts an auth session and rejects nullable or surplus session fields', () => {
    const session = {
      username: 'operator',
      csrfToken: 'csrf-token',
      developmentFixturesEnabled: true,
    };

    expect(AdminSessionSchema.parse(session)).toEqual(session);
    expect(AdminSessionSchema.safeParse({ ...session, csrfToken: null }).success).toBe(false);
    expectStrictObject(AdminSessionSchema, session);
  });

  test('accepts scan acceptance and rejects a null or surplus scan job id', () => {
    const accepted = { scanJobId: id };

    expect(LibraryScanAcceptedSchema.parse(accepted)).toEqual(accepted);
    expect(LibraryScanAcceptedSchema.safeParse({ scanJobId: null }).success).toBe(false);
    expectStrictObject(LibraryScanAcceptedSchema, accepted);
  });

  test('accepts nullable terminal scan timestamps and rejects non-wire scan status', () => {
    const scan = {
      scanJobId: id,
      status: 'queued',
      discoveredCount: 0,
      requestedAtUtc: '2026-07-10T12:00:00Z',
      startedAtUtc: null,
      finishedAtUtc: null,
      failureReason: null,
    };

    expect(LibraryScanStatusSchema.parse(scan)).toEqual(scan);
    expect(LibraryScanStatusSchema.safeParse({ ...scan, status: 'Queued' }).success).toBe(false);
    expectStrictObject(LibraryScanStatusSchema, scan);
  });

  test('accepts cached track metadata and rejects null required booleans', () => {
    const track = {
      id,
      title: 'Web 1.0 Radio',
      artist: '',
      album: '',
      durationMs: 0,
      hasCachedFile: true,
      coverImageUrl: '',
      metadataSource: 'filename',
      storageBackendId: '',
    };

    expect(AdminTrackSchema.parse(track)).toEqual(track);
    expect(AdminTrackSchema.safeParse({ ...track, hasCachedFile: null }).success).toBe(false);
    expectStrictObject(AdminTrackSchema, track);
  });

  test('accepts nullable playlist descriptions and rejects surplus playlist keys', () => {
    const playlist = {
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
    };

    expect(PlaylistSchema.parse(playlist)).toEqual(playlist);
    expect(PlaylistSchema.safeParse({ ...playlist, description: 1 }).success).toBe(false);
    expectStrictObject(PlaylistSchema, playlist);
  });

  test('accepts playlist item ordering and rejects an internal negative position', () => {
    const item = {
      id,
      trackId: otherId,
      title: 'The track',
      artist: '',
      position: 0,
    };

    expect(PlaylistItemSchema.parse(item)).toEqual(item);
    expect(PlaylistItemSchema.safeParse({ ...item, position: -1 }).success).toBe(false);
    expectStrictObject(PlaylistItemSchema, item);
  });

  test('accepts local and S3 storage nullability and rejects an unpinned backend type', () => {
    const storage = {
      defaultBackend: {
        type: 'local',
        localRoot: '/storage',
        s3Bucket: null,
        s3Region: null,
        s3ServiceUrl: null,
        s3ForcePathStyle: false,
      },
      additionalBackends: [
        {
          id,
          name: 'archive',
          type: 's3',
          localRoot: null,
          s3Bucket: 'web10-archive',
          isEnabled: true,
        },
      ],
    };

    expect(StorageSchema.parse(storage)).toEqual(storage);
    expect(
      StorageSchema.safeParse({
        ...storage,
        defaultBackend: { ...storage.defaultBackend, type: 'filesystem' },
      }).success,
    ).toBe(false);
    expectStrictObject(StorageSchema, storage);
  });

  test('accepts nullable offline heartbeat details and rejects internal stream status', () => {
    const status = {
      status: 'offline',
      desiredState: 'stopped',
      lastHeartbeatUtc: null,
      failureReason: null,
      bitrateKbps: 0,
      restartGeneration: 0,
    };

    expect(StreamNodeStatusSchema.parse(status)).toEqual(status);
    expect(StreamNodeStatusSchema.safeParse({ ...status, status: 'Failed' }).success).toBe(false);
    expectStrictObject(StreamNodeStatusSchema, status);
  });

  test('accepts stream control and rejects a nullable desired state', () => {
    const control = { desiredState: 'running', restartGeneration: 1 };

    expect(StreamNodeControlSchema.parse(control)).toEqual(control);
    expect(StreamNodeControlSchema.safeParse({ ...control, desiredState: null }).success).toBe(false);
    expectStrictObject(StreamNodeControlSchema, control);
  });

  test('accepts paid fixture identifiers and rejects missing or surplus identifiers', () => {
    const fixture = {
      donationPaymentId: id,
      sayPaymentId: otherId,
      sayMessageId: '0197f0a3-0000-7000-8000-000000000000',
    };

    expect(PaidVerticalSliceFixtureSchema.parse(fixture)).toEqual(fixture);
    expect(PaidVerticalSliceFixtureSchema.safeParse({ ...fixture, sayMessageId: null }).success).toBe(
      false,
    );
    expectStrictObject(PaidVerticalSliceFixtureSchema, fixture);
  });
});
