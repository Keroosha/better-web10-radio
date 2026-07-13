import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import { getStorageEntries, uploadStorageFile, type StorageEntry } from '@web10/shared';

import { StorageFileManager } from './StorageFileManager';
import { ToastProvider } from '../../shared/ui/toast';

vi.mock('@web10/shared', async () => {
  const actual = await vi.importActual('@web10/shared');
  return {
    ...actual,
    getStorageEntries: vi.fn(),
    uploadStorageFile: vi.fn(),
  };
});

const rootFolder: StorageEntry = {
  path: 'album',
  name: 'album',
  kind: 'folder',
  sizeBytes: null,
  lastModifiedUtc: null,
  contentType: null,
};

const albumFile: StorageEntry = {
  path: 'album/track.txt',
  name: 'track.txt',
  kind: 'file',
  sizeBytes: 5,
  lastModifiedUtc: '2026-07-13T00:00:00Z',
  contentType: 'text/plain',
};

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function renderManager(): void {
  render(
    <ToastProvider>
      <StorageFileManager storageBackendId={null} storageName="S3" enabled onBack={() => undefined} />
    </ToastProvider>,
  );
}

test('lists the root and navigates into a folder', async () => {
  vi.mocked(getStorageEntries).mockImplementation(async (query) => {
    const path = query?.path ?? '';
    return { path, items: path === '' ? [rootFolder] : [albumFile], nextCursor: null };
  });

  renderManager();

  await screen.findByRole('button', { name: '📁 album' });
  fireEvent.click(screen.getByRole('button', { name: '📁 album' }));
  await screen.findByRole('button', { name: '📄 track.txt' });
  expect(vi.mocked(getStorageEntries)).toHaveBeenLastCalledWith(
    { storageBackendId: null, path: 'album', limit: 100 },
    expect.objectContaining({ signal: expect.any(AbortSignal) }),
  );
});

test('joins directory upload paths to the current folder', async () => {
  vi.mocked(getStorageEntries).mockResolvedValue({ path: '', items: [], nextCursor: null });
  vi.mocked(uploadStorageFile).mockResolvedValue(albumFile);
  renderManager();

  const file = new File(['hello'], 'track.txt', { type: 'text/plain' });
  Object.defineProperty(file, 'webkitRelativePath', { value: 'album/track.txt' });
  fireEvent.change(screen.getByLabelText('Загрузить папку'), { target: { files: [file] } });

  await waitFor(() => expect(vi.mocked(uploadStorageFile)).toHaveBeenCalled());
  expect(vi.mocked(uploadStorageFile)).toHaveBeenCalledWith(
    { storageBackendId: null, path: 'album/track.txt' },
    file,
    expect.objectContaining({ signal: expect.any(AbortSignal) }),
  );
});
