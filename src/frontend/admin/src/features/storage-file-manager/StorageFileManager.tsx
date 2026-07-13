import { useCallback, useEffect, useMemo, useRef, useState, type ReactElement } from 'react';

import {
  ApiError,
  apiRawResponse,
  createLibraryScan,
  deleteStorageEntries,
  getLibraryScan,
  getStorageEntries,
  previewStorageDelete,
  storageContentUrl,
  uploadStorageFile,
  type StorageDeleteImpact,
  type StorageDeleteSelection,
  type StorageEntriesQuery,
  type StorageEntry,
} from '@web10/shared';

import { errorMessage } from '../../shared/lib/errorMessage';
import { Popup } from '../../shared/ui/Popup';
import { useToast } from '../../shared/ui/toast';
import { COLORS } from '../../shared/ui/tokens';

interface StorageFileManagerProps {
  readonly storageBackendId: string | null;
  readonly storageName: string;
  readonly enabled: boolean;
  readonly onBack: () => void;
}

interface DeleteDialogState {
  readonly impact: StorageDeleteImpact;
  readonly entries: StorageDeleteSelection[];
}

function selectionKey(entry: Pick<StorageEntry, 'path' | 'kind'>): string {
  return `${entry.kind}\u0000${entry.path}`;
}

function isSafeStoragePath(path: string): boolean {
  if (path.length === 0 || new TextEncoder().encode(path).byteLength > 1024) return false;
  if (path.startsWith('/') || path.endsWith('/') || path.includes('\\') || path.includes('\u0000')) return false;
  return path.split('/').every((segment) => segment !== '' && segment !== '.' && segment !== '..');
}

function formatBytes(value: number | null): string {
  if (value === null) return '—';
  if (value < 1024) return `${value} Б`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} КБ`;
  if (value < 1024 * 1024 * 1024) return `${(value / (1024 * 1024)).toFixed(1)} МБ`;
  return `${(value / (1024 * 1024 * 1024)).toFixed(1)} ГБ`;
}

function formatDate(value: string | null): string {
  if (value === null) return '—';
  return new Intl.DateTimeFormat('ru-RU', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value));
}

export function StorageFileManager({ storageBackendId, storageName, enabled, onBack }: StorageFileManagerProps): ReactElement {
  const { showToast } = useToast();
  const [path, setPath] = useState('');
  const [entries, setEntries] = useState<StorageEntry[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const folderInputElementRef = useRef<HTMLInputElement | null>(null);
  const listAbort = useRef<AbortController | null>(null);
  const previewAbort = useRef<AbortController | null>(null);
  const uploadAbort = useRef<AbortController | null>(null);
  const scanAbort = useRef<AbortController | null>(null);
  const generation = useRef(0);
  const [selected, setSelected] = useState<Set<string>>(() => new Set());
  const [sortBy, setSortBy] = useState<'name' | 'size' | 'date'>('name');
  const [selectedFile, setSelectedFile] = useState<StorageEntry | null>(null);
  const [textPreview, setTextPreview] = useState<string | null>(null);
  const [uploadProgress, setUploadProgress] = useState<{ completed: number; total: number; failed: number } | null>(null);
  const [scanStatus, setScanStatus] = useState<string | null>(null);
  const [deleteDialog, setDeleteDialog] = useState<DeleteDialogState | null>(null);
  const [deleting, setDeleting] = useState(false);

  const loadPage = useCallback(
    async (nextCursor: string | null, append: boolean): Promise<void> => {
      listAbort.current?.abort();
      const controller = new AbortController();
      listAbort.current = controller;
      const currentGeneration = generation.current;
      setLoading(true);
      setError(null);
      try {
        const query: StorageEntriesQuery = {
          storageBackendId,
          path,
          limit: 100,
          ...(nextCursor === null ? {} : { cursor: nextCursor }),
        };
        const page = await getStorageEntries(query, { signal: controller.signal });
        if (controller.signal.aborted || currentGeneration !== generation.current) return;
        setEntries((current) => {
          const values = append ? [...current, ...page.items] : page.items;
          const deduped = new Map(values.map((entry) => [selectionKey(entry), entry]));
          return [...deduped.values()];
        });
        setCursor(page.nextCursor);
      } catch (cause) {
        if (!controller.signal.aborted && currentGeneration === generation.current) setError(errorMessage(cause, 'Не удалось прочитать папку'));
      } finally {
        if (!controller.signal.aborted && currentGeneration === generation.current) setLoading(false);
      }
    },
    [path, storageBackendId],
  );

  useEffect(() => {
    generation.current += 1;
    setEntries([]);
    setCursor(null);
    setSelected(new Set());
    setSelectedFile(null);
    void loadPage(null, false);
    return () => {
      generation.current += 1;
      listAbort.current?.abort();
      previewAbort.current?.abort();
      uploadAbort.current?.abort();
      scanAbort.current?.abort();
    };
  }, [loadPage]);

  useEffect(() => {
    if (selectedFile === null || !selectedFile.contentType?.startsWith('text/')) {
      setTextPreview(null);
      return;
    }
    previewAbort.current?.abort();
    const controller = new AbortController();
    previewAbort.current = controller;
    setTextPreview(null);
    void apiRawResponse(storageContentUrl({ storageBackendId, path: selectedFile.path }), {
      method: 'GET',
      admin: true,
      headers: { Range: 'bytes=0-1048575' },
      signal: controller.signal,
    })
      .then((response) => response.text())
      .then((text) => {
        if (!controller.signal.aborted) setTextPreview(text);
      })
      .catch(() => {
        if (!controller.signal.aborted) setTextPreview('Не удалось прочитать текстовый фрагмент.');
      });
    return () => controller.abort();
  }, [selectedFile, storageBackendId]);

  const sortedEntries = useMemo(() => {
    if (cursor !== null) return entries;
    return [...entries].sort((left, right) => {
      if (left.kind !== right.kind) return left.kind === 'folder' ? -1 : 1;
      if (sortBy === 'size') return (left.sizeBytes ?? -1) - (right.sizeBytes ?? -1);
      if (sortBy === 'date') return (left.lastModifiedUtc ?? '').localeCompare(right.lastModifiedUtc ?? '');
      return left.name.localeCompare(right.name, 'ru');
    });
  }, [cursor, entries, sortBy]);

  const selectedEntries = useMemo(
    () => entries.filter((entry) => selected.has(selectionKey(entry))),
    [entries, selected],
  );

  const toggleSelected = (entry: StorageEntry): void => {
    const key = selectionKey(entry);
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  const uploadFiles = useCallback(
    async (files: File[]): Promise<void> => {
      const work = files.filter((file) => {
        const relative = file.webkitRelativePath || file.name;
        const target = path === '' ? relative : `${path}/${relative}`;
        return isSafeStoragePath(target);
      });
      if (work.length !== files.length) showToast('Часть путей загрузки недопустима');
      if (work.length === 0) return;
      uploadAbort.current?.abort();
      const controller = new AbortController();
      uploadAbort.current = controller;
      let nextIndex = 0;
      let completed = 0;
      let failed = 0;
      setUploadProgress({ completed: 0, total: work.length, failed: 0 });
      const worker = async (): Promise<void> => {
        while (!controller.signal.aborted) {
          const index = nextIndex;
          nextIndex += 1;
          if (index >= work.length) return;
          const file = work[index];
          if (file === undefined) return;
          const relative = file.webkitRelativePath || file.name;
          const target = path === '' ? relative : `${path}/${relative}`;
          try {
            await uploadStorageFile({ storageBackendId, path: target }, file, { signal: controller.signal });
            completed += 1;
          } catch {
            failed += 1;
          }
          setUploadProgress({ completed, total: work.length, failed });
        }
      };
      await Promise.all([worker(), worker(), worker()]);
      if (!controller.signal.aborted) {
        showToast(failed === 0 ? `Загружено файлов: ${completed}` : `Завершено: ${completed}, ошибок: ${failed}`);
        setUploadProgress(null);
        await loadPage(null, false);
      }
    },
    [loadPage, path, showToast, storageBackendId],
  );

  const scan = async (): Promise<void> => {
    if (!enabled) return;
    scanAbort.current?.abort();
    const controller = new AbortController();
    scanAbort.current = controller;
    setScanStatus('queued');
    try {
      const accepted = await createLibraryScan(storageBackendId === null ? {} : { storageBackendId }, { signal: controller.signal });
      for (let attempt = 0; attempt < 60; attempt += 1) {
        const status = await getLibraryScan(accepted.scanJobId, { signal: controller.signal });
        setScanStatus(status.status);
        if (status.status === 'completed' || status.status === 'failed') return;
        await new Promise<void>((resolve) => setTimeout(resolve, 1500));
      }
      setScanStatus('timeout');
    } catch (cause) {
      if (!controller.signal.aborted) {
        setScanStatus('failed');
        showToast(errorMessage(cause, 'Ошибка сканирования'));
      }
    }
  };

  const requestDelete = async (): Promise<void> => {
    if (selectedEntries.length === 0) return;
    previewAbort.current?.abort();
    const controller = new AbortController();
    previewAbort.current = controller;
    try {
      const impact = await previewStorageDelete(
        { storageBackendId, entries: selectedEntries.map(({ path: entryPath, kind }) => ({ path: entryPath, kind })) },
        { signal: controller.signal },
      );
      if (!controller.signal.aborted) setDeleteDialog({ impact, entries: selectedEntries.map(({ path: entryPath, kind }) => ({ path: entryPath, kind })) });
    } catch (cause) {
      if (!controller.signal.aborted) showToast(errorMessage(cause, 'Не удалось рассчитать влияние удаления'));
    }
  };

  const confirmDelete = async (): Promise<void> => {
    if (deleteDialog === null || deleting) return;
    setDeleting(true);
    try {
      const result = await deleteStorageEntries({ storageBackendId, entries: deleteDialog.entries, impactToken: deleteDialog.impact.impactToken });
      setDeleteDialog(null);
      setSelected(new Set());
      showToast(`Удалено файлов: ${result.deletedFileCount}`);
      await loadPage(null, false);
    } catch (cause) {
      const retryable = cause instanceof ApiError && (cause.code === 'storage.delete_impact_changed' || cause.code === 'storage.delete_failed');
      const message = retryable ? 'Содержимое изменилось. Обновите влияние и повторите.' : errorMessage(cause, 'Не удалось удалить содержимое');
      showToast(message);
      await loadPage(null, false);
      if (retryable) await requestDelete();
    } finally {
      setDeleting(false);
    }
  };

  const breadcrumbs = path === '' ? [''] : ['', ...path.split('/')];
  const folderInputRef = useCallback((node: HTMLInputElement | null): void => {
    folderInputElementRef.current = node;
    if (node !== null) node.setAttribute('webkitdirectory', '');
  }, []);

  return (
    <section>
      <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8, flexWrap: 'wrap', alignItems: 'center', marginBottom: 10 }}>
        <div>
          <button type="button" onClick={onBack}>← Назад к хранилищам</button>
          <strong style={{ marginLeft: 8 }}>{storageName}</strong>
        </div>
        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
          <label>
            <input ref={fileInputRef} type="file" multiple aria-label="Загрузить файлы" hidden onChange={(event) => void uploadFiles(Array.from(event.target.files ?? []))} />
            <button type="button" onClick={() => fileInputRef.current?.click()}>Загрузить файлы</button>
          </label>
          <label>
            <input ref={folderInputRef} type="file" multiple aria-label="Загрузить папку" hidden onChange={(event) => void uploadFiles(Array.from(event.target.files ?? []))} />
            <button type="button" onClick={() => folderInputElementRef.current?.click()}>Загрузить папку</button>
          </label>
          <button type="button" disabled={selectedEntries.length === 0} onClick={() => void requestDelete()}>Удалить выбранное</button>
          <button type="button" disabled={loading} onClick={() => void loadPage(null, false)}>Обновить</button>
          <button type="button" disabled={!enabled} title={!enabled ? 'Включите хранилище для сканирования' : undefined} onClick={() => void scan()}>Сканировать хранилище</button>
        </div>
      </div>
      {scanStatus !== null ? <p role="status">Сканирование: {scanStatus}</p> : null}
      {uploadProgress !== null ? <p role="status">Загрузка: {uploadProgress.completed}/{uploadProgress.total}{uploadProgress.failed > 0 ? `, ошибок ${uploadProgress.failed}` : ''}</p> : null}
      <nav aria-label="Хлебные крошки" style={{ marginBottom: 8 }}>
        {breadcrumbs.map((crumb, index) => {
          const crumbPath = breadcrumbs.slice(1, index + 1).join('/');
          return <span key={`${crumb}-${index}`}>{index > 0 ? ' / ' : ''}<button type="button" onClick={() => setPath(crumbPath)}>{crumb === '' ? 'Корень' : crumb}</button></span>;
        })}
      </nav>
      <div style={{ display: 'flex', gap: 6, alignItems: 'center', marginBottom: 8 }}>
        <span style={{ color: COLORS.subtle }}>Сортировка:</span>
        {(['name', 'size', 'date'] as const).map((value) => <button key={value} type="button" disabled={cursor !== null} onClick={() => setSortBy(value)}>{value === 'name' ? 'Имя' : value === 'size' ? 'Размер' : 'Дата'}</button>)}
      </div>
      {loading && entries.length === 0 ? <p>Загрузка…</p> : null}
      {error !== null ? <p role="alert" style={{ color: '#a4441a' }}>{error}</p> : null}
      {!loading && error === null && entries.length === 0 ? <p>Папка пуста.</p> : null}
      <div role="table" aria-label="Файлы хранилища">
        {sortedEntries.map((entry) => (
          <div
            key={selectionKey(entry)}
            role="row"
            tabIndex={0}
            onDoubleClick={() => (entry.kind === 'folder' ? setPath(entry.path) : setSelectedFile(entry))}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                if (entry.kind === 'folder') setPath(entry.path);
                else setSelectedFile(entry);
              }
            }}
            style={{ display: 'grid', gridTemplateColumns: '28px 1fr 100px 150px auto', gap: 8, padding: '6px 4px', borderBottom: '1px solid #d8e5ef', alignItems: 'center' }}
          >
            <input type="checkbox" aria-label={`Выбрать ${entry.name}`} checked={selected.has(selectionKey(entry))} onChange={() => toggleSelected(entry)} onClick={(event) => event.stopPropagation()} />
            <button type="button" style={{ textAlign: 'left' }} onClick={() => entry.kind === 'folder' ? setPath(entry.path) : setSelectedFile(entry)}>{entry.kind === 'folder' ? '📁' : '📄'} {entry.name}</button>
            <span>{formatBytes(entry.sizeBytes)}</span>
            <span>{formatDate(entry.lastModifiedUtc)}</span>
            {entry.kind === 'file' ? <a href={storageContentUrl({ storageBackendId, path: entry.path, download: true })}>Скачать</a> : <span />}
          </div>
        ))}
      </div>
      {cursor !== null ? <button type="button" disabled={loading} onClick={() => void loadPage(cursor, true)}>Загрузить ещё</button> : null}
      {selectedFile !== null ? <Popup title={`Файл: ${selectedFile.name}`} onClose={() => setSelectedFile(null)} width={620}>
        <div style={{ padding: 12 }}>
          <p>{formatBytes(selectedFile.sizeBytes)} · {selectedFile.contentType ?? 'application/octet-stream'}</p>
          {selectedFile.contentType?.startsWith('audio/') ? <audio controls src={storageContentUrl({ storageBackendId, path: selectedFile.path })} /> : null}
          {selectedFile.contentType?.startsWith('video/') ? <video controls style={{ maxWidth: '100%' }} src={storageContentUrl({ storageBackendId, path: selectedFile.path })} /> : null}
          {selectedFile.contentType !== null && ['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(selectedFile.contentType) ? <img src={storageContentUrl({ storageBackendId, path: selectedFile.path })} alt={selectedFile.name} style={{ maxWidth: '100%' }} /> : null}
          {selectedFile.contentType?.startsWith('text/') ? <pre style={{ maxHeight: 300, overflow: 'auto', whiteSpace: 'pre-wrap' }}>{textPreview ?? 'Чтение…'}</pre> : null}
          <a href={storageContentUrl({ storageBackendId, path: selectedFile.path, download: true })}>Скачать</a>
        </div>
      </Popup> : null}
      {deleteDialog !== null ? <Popup title="⚠ Подтверждение удаления" warning onClose={() => { if (!deleting) setDeleteDialog(null); }} width={560}>
        <div style={{ padding: 12 }}>
          <p>Файлов: {deleteDialog.impact.fileCount}, папок: {deleteDialog.impact.folderCount}, размер: {formatBytes(deleteDialog.impact.totalBytes)}.</p>
          <p>Отслеживаемых файлов: {deleteDialog.impact.trackedFileCount}; треков будет удалено: {deleteDialog.impact.tracksToDeleteCount}.</p>
          {deleteDialog.impact.playlistMemberships.length > 0 ? <p>Сначала треки будут удалены из плейлистов: {deleteDialog.impact.playlistMemberships.map((item) => `${item.playlistName} (${item.trackCount})`).join(', ')}, затем удалится содержимое хранилища.</p> : null}
          {deleteDialog.impact.currentTrack !== null ? <p>Текущий трек «{deleteDialog.impact.currentTrack.title}» — «{deleteDialog.impact.currentTrack.artist}»; воспроизведение переключится до удаления.</p> : null}
          {deleteDialog.impact.sampleTracks.length > 0 ? <ul>{deleteDialog.impact.sampleTracks.map((track) => <li key={track.trackId}>{track.title} — {track.artist}</li>)}</ul> : null}
          {deleteDialog.impact.sampleTracksTruncated ? <p>ещё больше треков</p> : null}
          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}><button type="button" disabled={deleting} onClick={() => setDeleteDialog(null)}>Отмена</button><button type="button" className="default" disabled={deleting} onClick={() => void confirmDelete()}>{deleting ? 'Удаление…' : 'Удалить'}</button></div>
        </div>
      </Popup> : null}
    </section>
  );
}
