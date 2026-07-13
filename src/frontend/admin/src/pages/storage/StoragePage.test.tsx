import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import {
  createLibraryScan,
  deleteStorageEntries,
  getStorage,
  getStorageEntries,
  previewStorageDelete,
  replaceStorage,
  uploadStorageFile,
  type Storage,
  type StorageDeleteImpact,
  type StorageEntry,
} from '@web10/shared';

import { collectDroppedUploads, StoragePage, type DroppedEntry } from './StoragePage';
import { ToastProvider } from '../../shared/ui/toast';

vi.mock('@web10/shared', async () => {
  const actual = await vi.importActual('@web10/shared');
  return {
    ...actual,
    getStorage: vi.fn(),
    getStorageEntries: vi.fn(),
    replaceStorage: vi.fn(),
    createLibraryScan: vi.fn(),
    uploadStorageFile: vi.fn(),
    previewStorageDelete: vi.fn(),
    deleteStorageEntries: vi.fn(),
  };
});

const S3_ID = '11111111-1111-1111-1111-111111111111';

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
    { id: S3_ID, name: 'S3 archive', type: 's3', localRoot: null, s3Bucket: 's3://web1-archive', isEnabled: true },
  ],
};

const albumFolder: StorageEntry = {
  path: 'album',
  name: 'album',
  kind: 'folder',
  sizeBytes: null,
  lastModifiedUtc: null,
  contentType: null,
};

const trackFile: StorageEntry = {
  path: 'album/track.txt',
  name: 'track.txt',
  kind: 'file',
  sizeBytes: 5,
  lastModifiedUtc: '2026-07-13T00:00:00Z',
  contentType: 'text/plain',
};

const impact: StorageDeleteImpact = {
  fileCount: 1,
  folderCount: 0,
  totalBytes: 5,
  trackedFileCount: 0,
  tracksToDeleteCount: 0,
  playlistMemberships: [],
  sampleTracks: [],
  sampleTracksTruncated: false,
  currentTrack: null,
  impactToken: 'token-1',
};

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function renderPage(): void {
  render(
    <ToastProvider>
      <StoragePage />
    </ToastProvider>,
  );
}

test('lists drives as tiles at the My Computer root', async () => {
  vi.mocked(getStorage).mockResolvedValue(storage);
  vi.mocked(getStorageEntries).mockResolvedValue({ path: '', items: [], nextCursor: null });

  renderPage();

  await screen.findByText('Диски и хранилища (2)');
  expect(screen.getByRole('option', { name: 'Хранилище по умолчанию' })).toBeDefined();
  expect(screen.getByRole('option', { name: 'S3 archive' })).toBeDefined();
  // Root issues no listing request until a drive is opened.
  expect(vi.mocked(getStorageEntries)).not.toHaveBeenCalled();
});

test('opens a drive and navigates into a folder', async () => {
  vi.mocked(getStorage).mockResolvedValue(storage);
  vi.mocked(getStorageEntries).mockImplementation(async (query) => {
    const path = query?.path ?? '';
    return { path, items: path === '' ? [albumFolder] : [trackFile], nextCursor: null };
  });

  renderPage();

  fireEvent.doubleClick(await screen.findByRole('option', { name: 'S3 archive' }));
  await screen.findByText('album');
  expect(vi.mocked(getStorageEntries)).toHaveBeenCalledWith(
    { storageBackendId: S3_ID, path: '', limit: 100 },
    expect.objectContaining({ signal: expect.any(AbortSignal) }),
  );

  fireEvent.doubleClick(screen.getByText('album'));
  await screen.findByText('track.txt');
  expect(vi.mocked(getStorageEntries)).toHaveBeenLastCalledWith(
    { storageBackendId: S3_ID, path: 'album', limit: 100 },
    expect.objectContaining({ signal: expect.any(AbortSignal) }),
  );
});

test('collects nested dropped folder files with their relative paths', async () => {
  const cover = new File(['cover'], 'cover.png', { type: 'image/png' });
  const track = new File(['track'], 'track.flac', { type: 'audio/flac' });
  const entries: DroppedEntry[] = [
    {
      kind: 'directory',
      readChildren: async () => [
        { kind: 'file', relativePath: 'album/cover.png', readFile: async () => cover },
        {
          kind: 'directory',
          readChildren: async () => [{ kind: 'file', relativePath: 'album/disc-1/track.flac', readFile: async () => track }],
        },
      ],
    },
  ];

  await expect(collectDroppedUploads(entries)).resolves.toEqual([
    { file: cover, relativePath: 'album/cover.png' },
    { file: track, relativePath: 'album/disc-1/track.flac' },
  ]);
});

test('joins a directory upload path to the current folder', async () => {
  vi.mocked(getStorage).mockResolvedValue(storage);
  vi.mocked(getStorageEntries).mockResolvedValue({ path: '', items: [], nextCursor: null });
  vi.mocked(uploadStorageFile).mockResolvedValue(trackFile);

  renderPage();

  fireEvent.doubleClick(await screen.findByRole('option', { name: 'Хранилище по умолчанию' }));
  const input = await screen.findByLabelText('Загрузить папку');

  const file = new File(['hello'], 'track.txt', { type: 'text/plain' });
  Object.defineProperty(file, 'webkitRelativePath', { value: 'album/track.txt' });
  fireEvent.change(input, { target: { files: [file] } });

  await waitFor(() => expect(vi.mocked(uploadStorageFile)).toHaveBeenCalled());
  expect(vi.mocked(uploadStorageFile)).toHaveBeenCalledWith(
    { storageBackendId: null, path: 'album/track.txt' },
    file,
    expect.objectContaining({ signal: expect.any(AbortSignal) }),
  );
});

test('selecting a file offers a delete that previews impact', async () => {
  vi.mocked(getStorage).mockResolvedValue(storage);
  vi.mocked(getStorageEntries).mockImplementation(async (query) => ({
    path: query?.path ?? '',
    items: [trackFile],
    nextCursor: null,
  }));
  vi.mocked(previewStorageDelete).mockResolvedValue(impact);
  vi.mocked(deleteStorageEntries).mockResolvedValue({
    deletedFileCount: 1,
    deletedFolderCount: 0,
    detachedPlaylistItemCount: 0,
    deletedTrackCount: 0,
    playbackAdvanced: false,
  });

  renderPage();

  fireEvent.doubleClick(await screen.findByRole('option', { name: 'S3 archive' }));
  fireEvent.click(await screen.findByText('track.txt'));

  fireEvent.click(await screen.findByRole('button', { name: '✕ Удалить' }));

  await waitFor(() => expect(vi.mocked(previewStorageDelete)).toHaveBeenCalled());
  expect(vi.mocked(previewStorageDelete)).toHaveBeenCalledWith(
    { storageBackendId: S3_ID, entries: [{ path: 'album/track.txt', kind: 'file' }] },
    expect.objectContaining({ signal: expect.any(AbortSignal) }),
  );
  await screen.findByText('⚠ Подтверждение удаления');

  fireEvent.click(screen.getByRole('button', { name: 'Удалить' }));
  await waitFor(() => expect(vi.mocked(deleteStorageEntries)).toHaveBeenCalled());
  expect(vi.mocked(deleteStorageEntries)).toHaveBeenCalledWith({
    storageBackendId: S3_ID,
    entries: [{ path: 'album/track.txt', kind: 'file' }],
    impactToken: 'token-1',
  });
});

test('Ctrl+Click selects multiple files and bulk-deletes them', async () => {
  const fileA: StorageEntry = { ...trackFile, path: 'a.txt', name: 'a.txt' };
  const fileB: StorageEntry = { ...trackFile, path: 'b.txt', name: 'b.txt' };
  vi.mocked(getStorage).mockResolvedValue(storage);
  vi.mocked(getStorageEntries).mockImplementation(async (query) => ({
    path: query?.path ?? '',
    items: [fileA, fileB],
    nextCursor: null,
  }));
  vi.mocked(previewStorageDelete).mockResolvedValue({ ...impact, fileCount: 2 });

  renderPage();

  fireEvent.doubleClick(await screen.findByRole('option', { name: 'S3 archive' }));
  fireEvent.click(await screen.findByText('a.txt'));
  fireEvent.click(screen.getByText('b.txt'), { ctrlKey: true });

  fireEvent.click(await screen.findByRole('button', { name: /Удалить выбранное/ }));

  await waitFor(() => expect(vi.mocked(previewStorageDelete)).toHaveBeenCalled());
  expect(vi.mocked(previewStorageDelete)).toHaveBeenCalledWith(
    {
      storageBackendId: S3_ID,
      entries: [
        { path: 'a.txt', kind: 'file' },
        { path: 'b.txt', kind: 'file' },
      ],
    },
    expect.objectContaining({ signal: expect.any(AbortSignal) }),
  );
});

test('adds a storage backend through the form', async () => {
  vi.mocked(getStorage).mockResolvedValue(storage);
  vi.mocked(getStorageEntries).mockResolvedValue({ path: '', items: [], nextCursor: null });
  vi.mocked(replaceStorage).mockResolvedValue(storage);

  renderPage();

  fireEvent.click(await screen.findByRole('button', { name: '＋ Добавить хранилище' }));
  fireEvent.change(screen.getByLabelText('Название'), { target: { value: 'S3 podcasts' } });
  fireEvent.change(screen.getByLabelText('Тип'), { target: { value: 'local' } });
  fireEvent.change(screen.getByLabelText('Путь'), { target: { value: '/srv/podcasts' } });
  fireEvent.click(screen.getByRole('button', { name: 'Создать' }));

  await waitFor(() => expect(vi.mocked(replaceStorage)).toHaveBeenCalled());
  expect(vi.mocked(replaceStorage)).toHaveBeenCalledWith({
    additionalBackends: [
      { id: S3_ID, name: 'S3 archive', type: 's3', localRoot: null, s3Bucket: 's3://web1-archive', isEnabled: true },
      { id: null, name: 'S3 podcasts', type: 'local', localRoot: '/srv/podcasts', s3Bucket: null, isEnabled: true },
    ],
  });
});

test('scan-all issues a library scan for every backend', async () => {
  vi.mocked(getStorage).mockResolvedValue(storage);
  vi.mocked(getStorageEntries).mockResolvedValue({ path: '', items: [], nextCursor: null });
  vi.mocked(createLibraryScan).mockResolvedValue({ scanJobId: '22222222-2222-2222-2222-222222222222' });

  renderPage();

  fireEvent.click(await screen.findByRole('button', { name: '⟳ Сканировать всё' }));
  await waitFor(() => expect(vi.mocked(createLibraryScan)).toHaveBeenCalledWith({}));
});
