import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import {
  getStorage,
  getTracksPage,
  type AdminTrack,
  type Storage,
} from '@web10/shared';

import { LibraryPage } from './LibraryPage';
import { ToastProvider } from '../../shared/ui/toast';

vi.mock('@web10/shared', async () => {
  const actual = await vi.importActual('@web10/shared');
  return {
    ...actual,
    createLibraryScan: vi.fn(),
    getLibraryScan: vi.fn(),
    getStorage: vi.fn(),
    getTracksPage: vi.fn(),
    playNow: vi.fn(),
    queueTrack: vi.fn(),
  };
});

const STORAGE_ID = '11111111-1111-1111-1111-111111111111';
const SECOND_CURSOR = 'second-page';

const storage: Storage = {
  defaultBackend: {
    type: 'local',
    localRoot: '/srv/media',
    s3Bucket: null,
    s3Region: null,
    s3ServiceUrl: null,
    s3ForcePathStyle: false,
  },
  additionalBackends: [
    { id: STORAGE_ID, name: 'Archive', type: 'local', localRoot: '/srv/archive', s3Bucket: null, isEnabled: true },
  ],
};

const firstTrack: AdminTrack = {
  id: '018f0aaa-0000-7000-8000-000000000020',
  title: 'First page track',
  artist: 'First Artist',
  album: 'First Album',
  durationMs: 184000,
  hasCachedFile: true,
  coverImageUrl: '',
  metadataSource: 'embedded',
  storageBackendId: STORAGE_ID,
};

const lastTrack: AdminTrack = {
  ...firstTrack,
  id: '018f0aaa-0000-7000-8000-000000000021',
  title: 'Last page track',
  artist: 'Last Artist',
  album: 'Last Album',
};

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

test('loads every cursor page before grouping the library', async () => {
  vi.mocked(getStorage).mockResolvedValue(storage);
  vi.mocked(getTracksPage).mockImplementation(async (query) => {
    if (query?.cursor === SECOND_CURSOR) {
      return { items: [lastTrack], nextCursor: null };
    }
    return { items: [firstTrack], nextCursor: SECOND_CURSOR };
  });

  render(
    <ToastProvider>
      <LibraryPage groupBy="artist" />
    </ToastProvider>,
  );

  expect(await screen.findByText('Last Artist')).toBeDefined();
  expect(screen.getByText('First Artist')).toBeDefined();
  await waitFor(() => expect(vi.mocked(getTracksPage)).toHaveBeenCalledTimes(2));
  expect(vi.mocked(getTracksPage)).toHaveBeenNthCalledWith(
    1,
    { query: '', limit: 100 },
    expect.objectContaining({ signal: expect.any(AbortSignal) }),
  );
  expect(vi.mocked(getTracksPage)).toHaveBeenNthCalledWith(
    2,
    { query: '', limit: 100, cursor: SECOND_CURSOR },
    expect.objectContaining({ signal: expect.any(AbortSignal) }),
  );
  expect(screen.queryByText(/Показаны первые/)).toBeNull();
});
