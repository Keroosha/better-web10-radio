import { afterEach, describe, expect, test, vi } from 'vitest';

import {
  approveSayMessage,
  clearAdminSession,
  createLibraryScan,
  createPaidVerticalSliceFixture,
  createPlaylist,
  createPlaylistItem,
  getAdminSession,
  getDonationGoal,
  getLibraryScan,
  getPlaylistItems,
  getPlaylists,
  getSayMessages,
  getSocialLinks,
  getStorage,
  getStreamNodeStatus,
  getTracksPage,
  loginAdmin,
  logoutAdmin,
  playNow,
  queueTrack,
  removeTrackCover,
  reorderQueue,
  rejectSayMessage,
  replacePlaylist,
  replacePlaylistItems,
  replaceSocialLinks,
  replaceStorage,
  replaceTrackCover,
  restartCurrent,
  restartStreamNode,
  setAdminSession,
  skipCurrent,
  startStreamNode,
  stopStreamNode,
  updateDonationGoal,
  updateTrackMetadata,
  type AdminSayMessage,
  type FetchImpl,
  type SocialLinksReplaceRequest,
  type StorageReplaceRequest,
} from '../index';
import { validPlayerState } from '../testing/fixtures';

const id = '0197f0a1-0000-7000-8000-000000000000';
const otherId = '0197f0a2-0000-7000-8000-000000000000';

function jsonResponse<TBody>(body: TBody, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function noContentResponse(): Response {
  return new Response(null, { status: 204 });
}

function capturingFetch<TBody>(body: TBody, status = 200): {
  fetchImpl: FetchImpl;
  requestUrl: () => string | undefined;
  requestInit: () => RequestInit | undefined;
} {
  let url: string | undefined;
  let init: RequestInit | undefined;
  const fetchImpl: FetchImpl = vi.fn((requestUrl, requestInit) => {
    url = requestUrl;
    init = requestInit;
    return Promise.resolve(jsonResponse(body, status));
  });
  return { fetchImpl, requestUrl: () => url, requestInit: () => init };
}

const session = {
  username: 'operator',
  csrfToken: 'csrf-token',
  developmentFixturesEnabled: true,
};
const scanAccepted = { scanJobId: id };
const scanStatus = {
  scanJobId: id,
  status: 'completed',
  discoveredCount: 2,
  requestedAtUtc: '2026-07-10T12:00:00Z',
  startedAtUtc: '2026-07-10T12:00:01Z',
  finishedAtUtc: '2026-07-10T12:00:02Z',
  failureReason: null,
};
const track = {
  id,
  title: 'The track',
  artist: '',
  album: '',
  durationMs: 0,
  hasCachedFile: true,
  coverImageUrl: '',
  metadataSource: 'filename' as const,
};
const playlistPolicy = {
  type: 'general' as const,
  source: 'manual' as const,
  order: 'sequential' as const,
  weight: 3,
  isJingle: false,
  interrupt: false,
  avoidDuplicates: true,
  playEverySongs: null,
  playEveryMinutes: null,
  playAtMinute: null,
  schedules: [],
};
const playlist = {
  id,
  name: 'Night shift',
  description: null,
  isActive: true,
  ...playlistPolicy,
  isSystem: false,
  itemCount: 1,
};
const playlistItem = {
  id: otherId,
  trackId: id,
  title: 'The track',
  artist: '',
  position: 0,
};
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
const streamStatus = {
  status: 'live',
  desiredState: 'running',
  lastHeartbeatUtc: '2026-07-10T12:00:00Z',
  failureReason: null,
  bitrateKbps: 192,
  restartGeneration: 3,
};
const streamControl = { desiredState: 'running', restartGeneration: 4 };
const paidFixture = {
  donationPaymentId: id,
  sayPaymentId: otherId,
  sayMessageId: '0197f0a3-0000-7000-8000-000000000000',
};
const pendingSayMessage: AdminSayMessage = {
  id: 'msg-1',
  telegramUserId: 7,
  displayName: 'CyberDove',
  text: 'hi',
  amountStars: 50,
  color: '#33ccff',
  status: 'pending',
  submittedAtUtc: '2026-07-10T12:00:00Z',
  paidAtUtc: '2026-07-10T12:00:05Z',
  moderatedAtUtc: null,
  moderationReason: null,
};
afterEach(() => {
  clearAdminSession?.();
});

describe('admin authentication routes', () => {
  test('login posts exact credentials, validates the session DTO, and retains its CSRF token', async () => {
    const request = capturingFetch(session);

    const result = await loginAdmin(
      { username: 'operator', password: 'correct horse battery staple' },
      { fetchImpl: request.fetchImpl },
    );

    expect(result.developmentFixturesEnabled).toBe(true);
    expect(request.requestUrl()).toBe('/api/v0/admin/auth/login');
    expect(request.requestInit()?.method).toBe('POST');
    expect(request.requestInit()?.body).toBe(
      JSON.stringify({ username: 'operator', password: 'correct horse battery staple' }),
    );

    const mutation = capturingFetch({ queueItemId: otherId }, 202);
    await queueTrack({ trackId: id }, { fetchImpl: mutation.fetchImpl });
    expect(new Headers(mutation.requestInit()?.headers).get('x-csrf-token')).toBe('csrf-token');
  });

  test('session probe GET validates the restored session without a request body', async () => {
    const request = capturingFetch(session);

    const result = await getAdminSession({ fetchImpl: request.fetchImpl });

    expect(result.username).toBe('operator');
    expect(request.requestUrl()).toBe('/api/v0/admin/auth/session');
    expect(request.requestInit()?.method).toBe('GET');
    expect(request.requestInit()?.body).toBeUndefined();
  });

  test('logout posts exact empty JSON and resolves on 204', async () => {
    setAdminSession(session);
    let url: string | undefined;
    let init: RequestInit | undefined;
    const fetchImpl: FetchImpl = vi.fn((requestUrl, requestInit) => {
      url = requestUrl;
      init = requestInit;
      return Promise.resolve(noContentResponse());
    });

    await expect(logoutAdmin({ fetchImpl })).resolves.toBeUndefined();

    expect(url).toBe('/api/v0/admin/auth/logout');
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe('{}');
  });
});

describe('library and playback routes', () => {
  test('creates a default scan with an exact empty body and validates its accepted identifier', async () => {
    const request = capturingFetch(scanAccepted, 202);

    const result = await createLibraryScan({}, { fetchImpl: request.fetchImpl });

    expect(result.scanJobId).toBe(id);
    expect(request.requestUrl()).toBe('/api/v0/admin/library/scan');
    expect(request.requestInit()?.method).toBe('POST');
    expect(request.requestInit()?.body).toBe('{}');
  });

  test('reads a scan status by its path identifier and validates terminal fields', async () => {
    const request = capturingFetch(scanStatus);

    const result = await getLibraryScan(id, { fetchImpl: request.fetchImpl });

    expect(result.discoveredCount).toBe(2);
    expect(result.finishedAtUtc).toBe('2026-07-10T12:00:02Z');
    expect(request.requestUrl()).toBe(`/api/v0/admin/library/scan/${id}`);
    expect(request.requestInit()?.method).toBe('GET');
  });

  test('encodes track query and limit and validates cursor page projection', async () => {
    const request = capturingFetch({ items: [track], nextCursor: null });

    const result = await getTracksPage({ query: 'night drive', limit: 25 }, { fetchImpl: request.fetchImpl });

    expect(result.items[0]?.hasCachedFile).toBe(true);
    expect(result.nextCursor).toBeNull();
    expect(request.requestUrl()).toBe('/api/v0/admin/tracks?query=night+drive&limit=25');
    expect(request.requestInit()?.method).toBe('GET');
  });

  test('queues a scanned track with the exact request body and validates acceptance', async () => {
    const request = capturingFetch({ queueItemId: otherId }, 202);

    const result = await queueTrack({ trackId: id }, { fetchImpl: request.fetchImpl });

    expect(result.queueItemId).toBe(otherId);
    expect(request.requestUrl()).toBe('/api/v0/admin/playback/queue');
    expect(request.requestInit()?.method).toBe('POST');
    expect(request.requestInit()?.body).toBe(JSON.stringify({ trackId: id }));
  });
  test('updates metadata with the exact body and validates the updated projection', async () => {
    const updatedTrack = { ...track, title: 'Updated title', artist: 'Updated artist', album: 'Album' };
    const request = capturingFetch(updatedTrack);

    const result = await updateTrackMetadata(
      id,
      { title: 'Updated title', artist: 'Updated artist', album: 'Album' },
      { fetchImpl: request.fetchImpl },
    );

    expect(result.title).toBe('Updated title');
    expect(request.requestUrl()).toBe(`/api/v0/admin/tracks/${id}`);
    expect(request.requestInit()?.method).toBe('PUT');
    expect(request.requestInit()?.body).toBe(
      JSON.stringify({ title: 'Updated title', artist: 'Updated artist', album: 'Album' }),
    );
  });

  test('uploads a raw cover with its media type, CSRF token, and exact route', async () => {
    setAdminSession(session);
    const request = capturingFetch(track);
    const body = new Blob(['png bytes'], { type: 'image/png' });

    const result = await replaceTrackCover(id, body, { fetchImpl: request.fetchImpl });

    expect(result.id).toBe(id);
    expect(request.requestUrl()).toBe(`/api/v0/admin/tracks/${id}/cover`);
    expect(request.requestInit()?.method).toBe('PUT');
    expect(request.requestInit()?.body).toBe(body);
    const headers = new Headers(request.requestInit()?.headers);
    expect(headers.get('content-type')).toBe('image/png');
    expect(headers.get('x-csrf-token')).toBe('csrf-token');
  });

  test('removes a cover with DELETE and validates the returned projection', async () => {
    const request = capturingFetch({ ...track, coverImageUrl: '' });

    const result = await removeTrackCover(id, { fetchImpl: request.fetchImpl });

    expect(result.coverImageUrl).toBe('');
    expect(request.requestUrl()).toBe(`/api/v0/admin/tracks/${id}/cover`);
    expect(request.requestInit()?.method).toBe('DELETE');
  });

  test('reorders the complete queue using the exact ID set and response schema', async () => {
    const queue = validPlayerState().queue;
    const request = capturingFetch(queue);

    const result = await reorderQueue(
      { queueItemIds: queue.items.map((item) => item.queueItemId) },
      { fetchImpl: request.fetchImpl },
    );

    expect(result.items).toHaveLength(queue.items.length);
    expect(request.requestUrl()).toBe('/api/v0/admin/playback/queue/order');
    expect(request.requestInit()?.method).toBe('PUT');
    expect(request.requestInit()?.body).toBe(
      JSON.stringify({ queueItemIds: queue.items.map((item) => item.queueItemId) }),
    );
  });

  test('posts skip and restart controls as exact empty JSON commands', async () => {
    const requests: RequestInit[] = [];
    const fetchImpl: FetchImpl = vi.fn((_url, init) => {
      if (init !== undefined) {
        requests.push(init);
      }
      return Promise.resolve(new Response(null, { status: 202 }));
    });

    await skipCurrent({ fetchImpl });
    await restartCurrent({ fetchImpl });

    expect(requests.map((request) => request.method)).toEqual(['POST', 'POST']);
    expect(requests.map((request) => request.body)).toEqual(['{}', '{}']);
    expect(vi.mocked(fetchImpl).mock.calls.map(([url]) => url)).toEqual([
      '/api/v0/admin/playback/skip',
      '/api/v0/admin/playback/restart-current',
    ]);
  });

  test('plays a track now with the exact body and accepted queue identifier', async () => {
    const request = capturingFetch({ queueItemId: otherId }, 202);

    const result = await playNow({ trackId: id }, { fetchImpl: request.fetchImpl });

    expect(result.queueItemId).toBe(otherId);
    expect(request.requestUrl()).toBe('/api/v0/admin/playback/play-now');
    expect(request.requestInit()?.method).toBe('POST');
    expect(request.requestInit()?.body).toBe(JSON.stringify({ trackId: id }));
  });
});

describe('donation and social routes', () => {
  test('reads the current donation goal through its canonical response schema', async () => {
    const request = capturingFetch(validPlayerState().donationGoal);

    const result = await getDonationGoal({ fetchImpl: request.fetchImpl });

    expect(result.raisedStars).toBe(3820);
    expect(request.requestUrl()).toBe('/api/v0/admin/donation-goal');
    expect(request.requestInit()?.method).toBe('GET');
  });

  test('updates donation goal with exact title and integer stars body', async () => {
    const goal = { ...validPlayerState().donationGoal, title: 'Web10.Radio launch', goalStars: 5000 };
    const request = capturingFetch(goal);

    const result = await updateDonationGoal(
      { title: 'Web10.Radio launch', goalStars: 5000 },
      { fetchImpl: request.fetchImpl },
    );

    expect(result.goalStars).toBe(5000);
    expect(request.requestUrl()).toBe('/api/v0/admin/donation-goal');
    expect(request.requestInit()?.method).toBe('PUT');
    expect(request.requestInit()?.body).toBe(
      JSON.stringify({ title: 'Web10.Radio launch', goalStars: 5000 }),
    );
  });

  test('reads canonical social links through the admin route', async () => {
    const request = capturingFetch(validPlayerState().socials);

    const result = await getSocialLinks({ fetchImpl: request.fetchImpl });

    expect(result[0]?.kind).toBe('telegram');
    expect(request.requestUrl()).toBe('/api/v0/admin/social-links');
    expect(request.requestInit()?.method).toBe('GET');
  });

  test('replaces ordered social links with the exact array body and validates canonical response', async () => {
    const social = validPlayerState().socials[0];
    if (social === undefined) {
      throw new Error('social fixture must include one canonical link');
    }
    const request = capturingFetch([social]);
    const body = [
      {
        id: null,
        kind: 'telegram',
        name: 'Telegram',
        handle: '@web10',
        url: 'https://t.me/web10',
        glyph: null,
        color: null,
        qrImageUrl: null,
        isFeatured: true,
      },
    ] satisfies SocialLinksReplaceRequest;

    const result = await replaceSocialLinks(body, { fetchImpl: request.fetchImpl });

    expect(result[0]?.id).toBe(social.id);
    expect(request.requestUrl()).toBe('/api/v0/admin/social-links');
    expect(request.requestInit()?.method).toBe('PUT');
    expect(request.requestInit()?.body).toBe(JSON.stringify(body));
  });
});

describe('playlist routes', () => {
  test('lists playlist summaries with canonical nullable descriptions', async () => {
    const request = capturingFetch([playlist]);

    const result = await getPlaylists({ fetchImpl: request.fetchImpl });

    expect(result[0]?.description).toBeNull();
    expect(request.requestUrl()).toBe('/api/v0/admin/playlists');
    expect(request.requestInit()?.method).toBe('GET');
  });

  test('creates a playlist with the exact body and validates the created summary', async () => {
    const body = { ...playlistPolicy, name: 'Night shift', description: null, isActive: true };
    const request = capturingFetch(playlist, 201);

    const result = await createPlaylist(body, { fetchImpl: request.fetchImpl });

    expect(result.itemCount).toBe(1);
    expect(request.requestUrl()).toBe('/api/v0/admin/playlists');
    expect(request.requestInit()?.method).toBe('POST');
    expect(request.requestInit()?.body).toBe(JSON.stringify(body));
  });

  test('replaces one playlist by path id with exact mutable fields', async () => {
    const body = { ...playlistPolicy, name: 'Night shift', description: 'updated', isActive: false };
    const request = capturingFetch({ ...playlist, ...body });

    const result = await replacePlaylist(id, body, { fetchImpl: request.fetchImpl });

    expect(result.description).toBe('updated');
    expect(request.requestUrl()).toBe(`/api/v0/admin/playlists/${id}`);
    expect(request.requestInit()?.method).toBe('PUT');
    expect(request.requestInit()?.body).toBe(JSON.stringify(body));
  });

  test('lists playlist items by parent path id and validates positions', async () => {
    const request = capturingFetch([playlistItem]);

    const result = await getPlaylistItems(id, { fetchImpl: request.fetchImpl });

    expect(result[0]?.position).toBe(0);
    expect(request.requestUrl()).toBe(`/api/v0/admin/playlists/${id}/items`);
    expect(request.requestInit()?.method).toBe('GET');
  });

  test('creates a playlist item with an exact track request body', async () => {
    const request = capturingFetch(playlistItem, 201);

    const result = await createPlaylistItem(id, { trackId: id }, { fetchImpl: request.fetchImpl });

    expect(result.trackId).toBe(id);
    expect(request.requestUrl()).toBe(`/api/v0/admin/playlists/${id}/items`);
    expect(request.requestInit()?.method).toBe('POST');
    expect(request.requestInit()?.body).toBe(JSON.stringify({ trackId: id }));
  });

  test('replaces all playlist items with exact ordered rows', async () => {
    const body = { items: [{ id: null, trackId: id }] };
    const request = capturingFetch([playlistItem]);

    const result = await replacePlaylistItems(id, body, { fetchImpl: request.fetchImpl });

    expect(result[0]?.id).toBe(otherId);
    expect(request.requestUrl()).toBe(`/api/v0/admin/playlists/${id}/items`);
    expect(request.requestInit()?.method).toBe('PUT');
    expect(request.requestInit()?.body).toBe(JSON.stringify(body));
  });
});

describe('storage and stream-node routes', () => {
  test('reads environment default and additional storage through the storage schema', async () => {
    const request = capturingFetch(storage);

    const result = await getStorage({ fetchImpl: request.fetchImpl });

    expect(result.defaultBackend.localRoot).toBe('/storage');
    expect(request.requestUrl()).toBe('/api/v0/admin/storage');
    expect(request.requestInit()?.method).toBe('GET');
  });

  test('replaces additional storage rows with the exact body and canonical response', async () => {
    const body = {
      additionalBackends: [
        {
          id: null,
          name: 'archive',
          type: 's3',
          localRoot: null,
          s3Bucket: 'web10-archive',
          isEnabled: true,
        },
      ],
    } satisfies StorageReplaceRequest;
    const request = capturingFetch(storage);

    const result = await replaceStorage(body, { fetchImpl: request.fetchImpl });

    expect(result.additionalBackends[0]?.s3Bucket).toBe('web10-archive');
    expect(request.requestUrl()).toBe('/api/v0/admin/storage');
    expect(request.requestInit()?.method).toBe('PUT');
    expect(request.requestInit()?.body).toBe(JSON.stringify(body));
  });

  test('reads stream status through exact lowercase status and control fields', async () => {
    const request = capturingFetch(streamStatus);

    const result = await getStreamNodeStatus({ fetchImpl: request.fetchImpl });

    expect(result.status).toBe('live');
    expect(request.requestUrl()).toBe('/api/v0/admin/stream-node/status');
    expect(request.requestInit()?.method).toBe('GET');
  });

  test.each([
    ['start', startStreamNode],
    ['stop', stopStreamNode],
    ['restart', restartStreamNode],
  ])('posts exact empty JSON to stream-node %s and validates accepted control', async (action, call) => {
    const request = capturingFetch(streamControl, 202);

    const result = await call({ fetchImpl: request.fetchImpl });

    expect(result.restartGeneration).toBe(4);
    expect(request.requestUrl()).toBe(`/api/v0/admin/stream-node/${action}`);
    expect(request.requestInit()?.method).toBe('POST');
    expect(request.requestInit()?.body).toBe('{}');
  });
});

describe('paid fixture and say moderation routes', () => {
  test('posts an exact fixture key and validates all returned payment/message identifiers', async () => {
    const request = capturingFetch(paidFixture);

    const result = await createPaidVerticalSliceFixture(
      { fixtureKey: 'demo-fixture' },
      { fetchImpl: request.fetchImpl },
    );

    expect(result.sayMessageId).toBe(paidFixture.sayMessageId);
    expect(request.requestUrl()).toBe('/api/v0/admin/dev/fixtures/paid-vertical-slice');
    expect(request.requestInit()?.method).toBe('POST');
    expect(request.requestInit()?.body).toBe(JSON.stringify({ fixtureKey: 'demo-fixture' }));
  });

  test('queries moderated messages by the requested lowercase status', async () => {
    const request = capturingFetch([pendingSayMessage]);

    const messages = await getSayMessages('pending', { fetchImpl: request.fetchImpl });

    expect(messages[0]?.status).toBe('pending');
    expect(request.requestUrl()).toBe('/api/v0/admin/say-messages?status=pending');
    expect(request.requestInit()?.method).toBe('GET');
  });

  test('approves a say message with exact empty JSON and resolves on 204', async () => {
    setAdminSession(session);
    let url: string | undefined;
    let init: RequestInit | undefined;
    const fetchImpl: FetchImpl = vi.fn((requestUrl, requestInit) => {
      url = requestUrl;
      init = requestInit;
      return Promise.resolve(noContentResponse());
    });

    await expect(approveSayMessage('msg-1', { fetchImpl })).resolves.toBeUndefined();

    expect(url).toBe('/api/v0/admin/say-messages/msg-1/approve');
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe('{}');
  });

  test('rejects a say message with the exact reason body and resolves on 204', async () => {
    setAdminSession(session);
    let url: string | undefined;
    let init: RequestInit | undefined;
    const fetchImpl: FetchImpl = vi.fn((requestUrl, requestInit) => {
      url = requestUrl;
      init = requestInit;
      return Promise.resolve(noContentResponse());
    });

    await expect(rejectSayMessage('msg-1', 'off-topic', { fetchImpl })).resolves.toBeUndefined();

    expect(url).toBe('/api/v0/admin/say-messages/msg-1/reject');
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe(JSON.stringify({ reason: 'off-topic' }));
  });
});
