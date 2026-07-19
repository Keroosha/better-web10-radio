import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type ReactElement,
  type ReactNode,
} from 'react';

import {
  ApiError,
  apiRawResponse,
  createLibraryScan,
  createStorageFolder,
  deleteStorageEntries,
  getStorage,
  getStorageCacheSettings,
  previewStorageDelete,
  replaceStorage,
  storageContentUrl,
  updateStorageCacheSettings,
  uploadStorageFile,
  type Storage,
  type StorageAdditionalBackend,
  type StorageAdditionalBackendReplace,
  type StorageCacheSettings,
  type StorageDeleteImpact,
  type StorageDeleteSelection,
  type StorageEntry,
} from '@web10/shared';

import { useStorageEntries } from '../../features/storage-file-manager/useStorageEntries';
import { errorMessage } from '../../shared/lib/errorMessage';
import { useApiResource } from '../../shared/lib/useApiResource';
import { Popup } from '../../shared/ui/Popup';
import { ResourceView } from '../../shared/ui/ResourceView';
import { useToast } from '../../shared/ui/toast';
import { COLORS, ellipsis, formGrid } from '../../shared/ui/tokens';

const DEFAULT_DRIVE_KEY = '__default__';

interface Drive {
  readonly driveKey: string;
  readonly backendId: string | null;
  readonly name: string;
  readonly type: 'local' | 's3';
  readonly path: string;
  readonly enabled: boolean;
  readonly isDefault: boolean;
}

interface Loc {
  readonly driveKey: string | null;
  readonly path: string;
}

interface DetailAction {
  readonly label: string;
  readonly primary?: boolean;
  readonly onClick?: () => void;
  readonly href?: string;
  readonly disabled?: boolean;
}

interface Details {
  readonly icon: string;
  readonly thumb: string;
  readonly title: string;
  readonly line1: string;
  readonly line2: string;
  readonly actions: DetailAction[];
}

interface DeleteDialogState {
  readonly impact: StorageDeleteImpact;
  readonly entries: StorageDeleteSelection[];
}

export interface StorageUpload {
  readonly file: File;
  readonly relativePath: string;
}

export interface DroppedFileEntry {
  readonly kind: 'file';
  readonly relativePath: string;
  readonly readFile: () => Promise<File>;
}

export interface DroppedDirectoryEntry {
  readonly kind: 'directory';
  readonly readChildren: () => Promise<readonly DroppedEntry[]>;
}

export type DroppedEntry = DroppedFileEntry | DroppedDirectoryEntry;

function uploadsFromFiles(files: readonly File[]): StorageUpload[] {
  return files.map((file) => ({ file, relativePath: file.webkitRelativePath || file.name }));
}

function readEntryFile(entry: FileSystemFileEntry): Promise<File> {
  const deferred = Promise.withResolvers<File>();
  entry.file(deferred.resolve, deferred.reject);
  return deferred.promise;
}

function readDirectoryBatch(reader: FileSystemDirectoryReader): Promise<FileSystemEntry[]> {
  const deferred = Promise.withResolvers<FileSystemEntry[]>();
  reader.readEntries(deferred.resolve, deferred.reject);
  return deferred.promise;
}

async function readDirectoryEntries(entry: FileSystemDirectoryEntry): Promise<FileSystemEntry[]> {
  const reader = entry.createReader();
  const entries: FileSystemEntry[] = [];

  for (;;) {
    const batch = await readDirectoryBatch(reader);
    if (batch.length === 0) return entries;
    entries.push(...batch);
  }
}

function isFileEntry(entry: FileSystemEntry): entry is FileSystemFileEntry {
  return entry.isFile;
}

function isDirectoryEntry(entry: FileSystemEntry): entry is FileSystemDirectoryEntry {
  return entry.isDirectory;
}

function dropRelativePath(entry: FileSystemEntry): string {
  const fullPath = entry.fullPath.startsWith('/') ? entry.fullPath.slice(1) : entry.fullPath;
  return fullPath === '' ? entry.name : fullPath;
}

function toDroppedEntry(entry: FileSystemEntry): DroppedEntry | null {
  if (isFileEntry(entry)) {
    return { kind: 'file', relativePath: dropRelativePath(entry), readFile: () => readEntryFile(entry) };
  }

  if (isDirectoryEntry(entry)) {
    return {
      kind: 'directory',
      readChildren: async () => {
        const children: DroppedEntry[] = [];
        for (const child of await readDirectoryEntries(entry)) {
          const dropped = toDroppedEntry(child);
          if (dropped !== null) children.push(dropped);
        }
        return children;
      },
    };
  }

  return null;
}

export async function collectDroppedUploads(entries: readonly DroppedEntry[]): Promise<StorageUpload[]> {
  const uploads: StorageUpload[] = [];

  const collect = async (entry: DroppedEntry): Promise<void> => {
    if (entry.kind === 'file') {
      uploads.push({ file: await entry.readFile(), relativePath: entry.relativePath });
      return;
    }

    for (const child of await entry.readChildren()) {
      await collect(child);
    }
  };

  for (const entry of entries) {
    await collect(entry);
  }

  return uploads;
}

async function uploadsFromDrop(dataTransfer: DataTransfer): Promise<StorageUpload[]> {
  const entries: DroppedEntry[] = [];

  for (const item of Array.from(dataTransfer.items)) {
    const entry = item.webkitGetAsEntry?.() ?? null;
    if (entry !== null) {
      const dropped = toDroppedEntry(entry);
      if (dropped !== null) entries.push(dropped);
    }
  }

  return entries.length === 0 ? uploadsFromFiles(Array.from(dataTransfer.files)) : collectDroppedUploads(entries);
}

const ROOT: Loc = { driveKey: null, path: '' };
const SELECT_BG = 'linear-gradient(#dcecfb,#bcdcf7)';
const NAV_BG = 'linear-gradient(#e3f0fb,#c9e2fb)';
const DRIVE_THUMB = 'linear-gradient(#eaf3fb,#cfe6fb)';
const FOLDER_THUMB = 'linear-gradient(#ffe9b0,#ffd166)';

const toolbarBtn: CSSProperties = { minWidth: 0, padding: '2px 9px' };
const commandBtn: CSSProperties = { minWidth: 0, padding: '2px 10px' };
const actionBtn: CSSProperties = { minWidth: 0, padding: '3px 10px' };

function driveIcon(type: 'local' | 's3'): string {
  return type === 'local' ? '🖴' : '☁';
}

function fileIcon(contentType: string | null): string {
  if (contentType === null) return '📄';
  if (contentType.startsWith('audio/')) return '🎵';
  if (contentType.startsWith('video/')) return '🎬';
  if (contentType.startsWith('image/')) return '🖼';
  if (contentType.startsWith('text/')) return '📃';
  return '📄';
}

function fileThumbGradient(contentType: string | null): string {
  if (contentType === null) return 'linear-gradient(135deg,#e9eff7,#d3e0ee)';
  if (contentType.startsWith('audio/')) return 'linear-gradient(135deg,#ffd1e8,#cfe6ff)';
  if (contentType.startsWith('video/')) return 'linear-gradient(135deg,#e6d6ff,#ffe6cf)';
  if (contentType.startsWith('text/')) return 'linear-gradient(135deg,#eef2f7,#dbe6f2)';
  return 'linear-gradient(135deg,#e9eff7,#d3e0ee)';
}

function isImageType(contentType: string | null): boolean {
  return contentType !== null && ['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(contentType);
}

function entryKeyOf(entry: Pick<StorageEntry, 'path' | 'kind'>): string {
  return `${entry.kind}:${entry.path}`;
}

function typeLabel(type: 'local' | 's3'): string {
  return type === 'local' ? 'Локальное' : 'S3';
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

function isSafeStoragePath(path: string): boolean {
  if (path.length === 0 || new TextEncoder().encode(path).byteLength > 1024) return false;
  if (path.startsWith('/') || path.endsWith('/') || path.includes('\\') || path.includes('\u0000')) return false;
  return path.split('/').every((segment) => segment !== '' && segment !== '.' && segment !== '..');
}

function buildDrives(storage: Storage): Drive[] {
  const def: Drive = {
    driveKey: DEFAULT_DRIVE_KEY,
    backendId: null,
    name: 'Хранилище по умолчанию',
    type: storage.defaultBackend.type,
    path: storage.defaultBackend.localRoot ?? storage.defaultBackend.s3Bucket ?? '',
    enabled: true,
    isDefault: true,
  };
  const extra = storage.additionalBackends.map<Drive>((backend) => ({
    driveKey: backend.id,
    backendId: backend.id,
    name: backend.name,
    type: backend.type,
    path: backend.localRoot ?? backend.s3Bucket ?? '',
    enabled: backend.isEnabled,
    isDefault: false,
  }));
  return [def, ...extra];
}

function toReplaceItem(backend: StorageAdditionalBackend): StorageAdditionalBackendReplace {
  return {
    id: backend.id,
    name: backend.name,
    type: backend.type,
    localRoot: backend.localRoot,
    s3Bucket: backend.s3Bucket,
    isEnabled: backend.isEnabled,
  };
}

/** Хранилища: мини-Проводник Windows 7 над реальным API хранилищ (ПРАВИЛА-UI.md §11). */
export function StoragePage(): ReactElement {
  const load = useCallback((): Promise<Storage> => getStorage(), []);
  const resource = useApiResource(load);
  return (
    <ResourceView resource={resource}>{(storage) => <StorageExplorer initial={storage} />}</ResourceView>
  );
}

function StorageExplorer({ initial }: { readonly initial: Storage }): ReactElement {
  const { showToast } = useToast();
  const [storage, setStorage] = useState<Storage>(initial);
  const drives = useMemo(() => buildDrives(storage), [storage]);

  const [nav, setNav] = useState<{ readonly hist: Loc[]; readonly idx: number }>({ hist: [ROOT], idx: 0 });
  const loc = nav.hist[nav.idx] ?? ROOT;
  const [driveSel, setDriveSel] = useState<string | null>(null);
  const [entryKeys, setEntryKeys] = useState<ReadonlySet<string>>(() => new Set());
  const [search, setSearch] = useState('');

  const activeDrive = loc.driveKey === null ? null : drives.find((drive) => drive.driveKey === loc.driveKey) ?? null;
  const activeBackendId = activeDrive?.backendId ?? null;
  const browser = useStorageEntries(activeBackendId, loc.path, activeDrive !== null);
  const { entries, cursor, loading, error, loadMore, reload } = browser;

  const [addOpen, setAddOpen] = useState(false);
  const [folderCreateOpen, setFolderCreateOpen] = useState(false);
  const [cacheOpen, setCacheOpen] = useState(false);
  const [deleteDriveId, setDeleteDriveId] = useState<string | null>(null);
  const [deleteDialog, setDeleteDialog] = useState<DeleteDialogState | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [previewFile, setPreviewFile] = useState<StorageEntry | null>(null);
  const [textPreview, setTextPreview] = useState<string | null>(null);
  const [uploadProgress, setUploadProgress] = useState<{ completed: number; total: number; failed: number } | null>(null);
  const [dragOver, setDragOver] = useState(false);

  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const folderInputRef = useRef<HTMLInputElement | null>(null);
  const uploadAbort = useRef<AbortController | null>(null);
  const deleteAbort = useRef<AbortController | null>(null);
  const previewAbort = useRef<AbortController | null>(null);

  useEffect(() => {
    return () => {
      uploadAbort.current?.abort();
      deleteAbort.current?.abort();
      previewAbort.current?.abort();
    };
  }, []);

  const clearSelection = useCallback((): void => {
    setDriveSel(null);
    setEntryKeys(new Set());
  }, []);
  const selectDrive = useCallback((driveKey: string): void => {
    setDriveSel(driveKey);
    setEntryKeys(new Set());
  }, []);
  const selectEntry = useCallback((entry: StorageEntry, additive: boolean): void => {
    const key = entryKeyOf(entry);
    if (additive) {
      setEntryKeys((prev) => {
        const next = new Set(prev);
        if (next.has(key)) next.delete(key);
        else next.add(key);
        return next;
      });
    } else {
      setEntryKeys(new Set([key]));
    }
    setDriveSel(null);
  }, []);

  const pushLoc = useCallback(
    (next: Loc): void => {
      setNav((current) => {
        const hist = current.hist.slice(0, current.idx + 1);
        hist.push(next);
        return { hist, idx: hist.length - 1 };
      });
      clearSelection();
      setSearch('');
    },
    [clearSelection],
  );

  const goBack = useCallback((): void => {
    setNav((current) => (current.idx > 0 ? { ...current, idx: current.idx - 1 } : current));
    clearSelection();
  }, [clearSelection]);
  const goForward = useCallback((): void => {
    setNav((current) => (current.idx < current.hist.length - 1 ? { ...current, idx: current.idx + 1 } : current));
    clearSelection();
  }, [clearSelection]);
  const goUp = useCallback((): void => {
    if (loc.path !== '') pushLoc({ driveKey: loc.driveKey, path: loc.path.split('/').slice(0, -1).join('/') });
    else if (loc.driveKey !== null) pushLoc(ROOT);
  }, [loc, pushLoc]);

  const setStorageFromReplace = useCallback(
    async (additional: StorageAdditionalBackendReplace[], message: string): Promise<void> => {
      try {
        const updated = await replaceStorage({ additionalBackends: additional });
        setStorage(updated);
        showToast(message);
      } catch (cause) {
        showToast(errorMessage(cause, 'Не удалось сохранить хранилища'));
      }
    },
    [showToast],
  );

  const toggleDrive = useCallback(
    (backendId: string): void => {
      const target = storage.additionalBackends.find((backend) => backend.id === backendId);
      void setStorageFromReplace(
        storage.additionalBackends.map((backend) =>
          toReplaceItem(backend.id === backendId ? { ...backend, isEnabled: !backend.isEnabled } : backend),
        ),
        target?.isEnabled === true ? 'Хранилище выключено' : 'Хранилище включено',
      );
    },
    [setStorageFromReplace, storage.additionalBackends],
  );

  const deleteDrive = useCallback(
    (backendId: string): void => {
      if (loc.driveKey === backendId) setNav({ hist: [ROOT], idx: 0 });
      clearSelection();
      void setStorageFromReplace(
        storage.additionalBackends.filter((backend) => backend.id !== backendId).map(toReplaceItem),
        'Хранилище удалено',
      );
    },
    [loc.driveKey, setStorageFromReplace, storage.additionalBackends],
  );

  const addDrive = useCallback(
    async (name: string, type: 'local' | 's3', path: string): Promise<void> => {
      const item: StorageAdditionalBackendReplace = {
        id: null,
        name,
        type,
        localRoot: type === 'local' ? path : null,
        s3Bucket: type === 's3' ? path : null,
        isEnabled: true,
      };
      await setStorageFromReplace([...storage.additionalBackends.map(toReplaceItem), item], 'Хранилище добавлено');
    },
    [setStorageFromReplace, storage.additionalBackends],
  );

  const scanAll = useCallback((): void => {
    const enabledDrives = drives.filter((drive) => drive.enabled);
    void Promise.all(
      enabledDrives.map((drive) =>
        createLibraryScan(drive.backendId === null ? {} : { storageBackendId: drive.backendId }),
      ),
    )
      .then(() => showToast(`Сканирование запущено: хранилищ ${enabledDrives.length}`))
      .catch((cause) => showToast(errorMessage(cause, 'Ошибка сканирования')));
  }, [drives, showToast]);

  const scanDrive = useCallback(
    (drive: Drive): void => {
      if (!drive.enabled) return;
      createLibraryScan(drive.backendId === null ? {} : { storageBackendId: drive.backendId })
        .then(() => showToast(`Сканирование «${drive.name}»…`))
        .catch((cause) => showToast(errorMessage(cause, 'Ошибка сканирования')));
    },
    [showToast],
  );

  const uploadFiles = useCallback(
    async (uploads: readonly StorageUpload[]): Promise<void> => {
      if (activeDrive === null) return;
      const backendId = activeDrive.backendId;
      const base = loc.path;
      const work = uploads.filter((upload) => {
        const target = base === '' ? upload.relativePath : `${base}/${upload.relativePath}`;
        return isSafeStoragePath(target);
      });
      if (work.length !== uploads.length) showToast('Часть путей загрузки недопустима');
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
          const upload = work[index];
          if (upload === undefined) return;
          const target = base === '' ? upload.relativePath : `${base}/${upload.relativePath}`;
          try {
            await uploadStorageFile({ storageBackendId: backendId, path: target }, upload.file, { signal: controller.signal });
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
        reload();
      }
    },
    [activeDrive, loc.path, reload, showToast],
  );

  const createFolder = useCallback(
    async (name: string): Promise<boolean> => {
      if (activeDrive === null) return false;
      const folderName = name.trim();
      if (
        folderName === '' ||
        folderName === '.' ||
        folderName === '..' ||
        folderName.includes('/') ||
        folderName.includes('\\')
      ) {
        showToast('Укажите имя папки без разделителей пути');
        return false;
      }
      const path = loc.path === '' ? folderName : `${loc.path}/${folderName}`;
      try {
        await createStorageFolder({ storageBackendId: activeDrive.backendId, path });
        showToast(`Создана папка «${folderName}»`);
        reload();
        return true;
      } catch (cause) {
        showToast(errorMessage(cause, 'Не удалось создать папку'));
        return false;
      }
    },
    [activeDrive, loc.path, reload, showToast],
  );

  const requestDelete = useCallback(
    async (selections: StorageDeleteSelection[]): Promise<void> => {
      if (selections.length === 0) return;
      deleteAbort.current?.abort();
      const controller = new AbortController();
      deleteAbort.current = controller;
      try {
        const impact = await previewStorageDelete(
          { storageBackendId: activeBackendId, entries: selections },
          { signal: controller.signal },
        );
        if (!controller.signal.aborted) setDeleteDialog({ impact, entries: selections });
      } catch (cause) {
        if (!controller.signal.aborted) showToast(errorMessage(cause, 'Не удалось подготовить удаление'));
      }
    },
    [activeBackendId, showToast],
  );

  const confirmDelete = useCallback(async (): Promise<void> => {
    if (deleteDialog === null || deleting) return;
    setDeleting(true);
    try {
      const result = await deleteStorageEntries({
        storageBackendId: activeBackendId,
        entries: deleteDialog.entries,
        impactToken: deleteDialog.impact.impactToken,
      });
      setDeleteDialog(null);
      clearSelection();
      showToast(`Удалено файлов: ${result.deletedFileCount}, папок: ${result.deletedFolderCount}`);
      reload();
    } catch (cause) {
      const retryable =
        cause instanceof ApiError &&
        (cause.code === 'storage.delete_impact_changed' || cause.code === 'storage.delete_failed');
      showToast(
        retryable
          ? 'Содержимое изменилось. Выберите папку ещё раз и подтвердите удаление.'
          : errorMessage(cause, 'Не удалось удалить содержимое'),
      );
      reload();
      if (retryable) await requestDelete(deleteDialog.entries);
    } finally {
      setDeleting(false);
    }
  }, [activeBackendId, deleteDialog, deleting, reload, requestDelete, showToast]);

  useEffect(() => {
    if (previewFile === null || previewFile.contentType?.startsWith('text/') !== true) {
      setTextPreview(null);
      return;
    }
    previewAbort.current?.abort();
    const controller = new AbortController();
    previewAbort.current = controller;
    setTextPreview(null);
    void apiRawResponse(storageContentUrl({ storageBackendId: activeBackendId, path: previewFile.path }), {
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
  }, [previewFile, activeBackendId]);

  const query = search.trim().toLowerCase();
  const driveTiles = useMemo(
    () => (query === '' ? drives : drives.filter((drive) => drive.name.toLowerCase().includes(query))),
    [drives, query],
  );
  const visibleEntries = useMemo(
    () => (query === '' ? entries : entries.filter((entry) => entry.name.toLowerCase().includes(query))),
    [entries, query],
  );
  const folders = useMemo(
    () => visibleEntries.filter((entry) => entry.kind === 'folder').sort((a, b) => a.name.localeCompare(b.name, 'ru')),
    [visibleEntries],
  );
  const files = useMemo(
    () => visibleEntries.filter((entry) => entry.kind === 'file').sort((a, b) => a.name.localeCompare(b.name, 'ru')),
    [visibleEntries],
  );

  const selDrive = driveSel === null ? null : drives.find((drive) => drive.driveKey === driveSel) ?? null;
  const selectedEntries = useMemo(
    () => entries.filter((entry) => entryKeys.has(entryKeyOf(entry))),
    [entries, entryKeys],
  );
  const ctxDrive = selDrive ?? activeDrive;

  const crumbs: { readonly name: string; readonly loc: Loc }[] = [{ name: 'Мой компьютер', loc: ROOT }];
  if (activeDrive !== null) {
    crumbs.push({ name: activeDrive.name, loc: { driveKey: activeDrive.driveKey, path: '' } });
    if (loc.path !== '') {
      let acc = '';
      for (const segment of loc.path.split('/')) {
        acc = acc === '' ? segment : `${acc}/${segment}`;
        crumbs.push({ name: segment, loc: { driveKey: activeDrive.driveKey, path: acc } });
      }
    }
  }

  const details = computeDetails({
    activeDrive,
    selDrive,
    selectedEntries,
    drives,
    entryCounts: { folders: folders.length, files: files.length },
    backendId: activeBackendId,
    open: (next) => pushLoc(next),
    scan: scanDrive,
    toggle: toggleDrive,
    requestDeleteDrive: (id) => setDeleteDriveId(id),
    preview: (entry) => setPreviewFile(entry),
    requestDeleteEntries: (items) => void requestDelete(items.map((entry) => ({ path: entry.path, kind: entry.kind }))),
  });

  const s3Default = storage.defaultBackend.type === 's3';
  const statusText =
    uploadProgress !== null
      ? `Загрузка: ${uploadProgress.completed}/${uploadProgress.total}${uploadProgress.failed > 0 ? `, ошибок ${uploadProgress.failed}` : ''}`
      : activeDrive !== null
        ? 'Ctrl+клик — выделить несколько · перетащите файлы для загрузки'
        : 'Двойной клик — открыть, одиночный — свойства объекта';

  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        height: '100%',
        minHeight: 0,
        border: '1px solid #b9cbe0',
        borderRadius: '4px',
        overflow: 'hidden',
        background: '#fff',
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: '6px',
          padding: '6px 8px',
          borderBottom: '1px solid #d7e3f0',
          background: 'linear-gradient(#f7fbff,#eef4fb)',
          flex: 'none',
        }}
      >
        <button type="button" title="Назад" style={toolbarBtn} disabled={nav.idx <= 0} onClick={goBack}>
          ◀
        </button>
        <button
          type="button"
          title="Вперёд"
          style={toolbarBtn}
          disabled={nav.idx >= nav.hist.length - 1}
          onClick={goForward}
        >
          ▶
        </button>
        <button type="button" title="Вверх" style={toolbarBtn} disabled={loc.driveKey === null} onClick={goUp}>
          ⤴
        </button>
        <nav
          aria-label="Адресная строка"
          style={{
            flex: 1,
            minWidth: 0,
            display: 'flex',
            alignItems: 'center',
            gap: '3px',
            height: '24px',
            padding: '0 8px',
            background: '#fff',
            border: '1px solid #9bb6cd',
            borderRadius: '3px',
            overflow: 'hidden',
          }}
        >
          <span style={{ fontSize: '14px', flex: 'none' }}>🖥</span>
          {crumbs.map((crumb, index) => (
            <span key={`${crumb.loc.driveKey ?? 'root'}-${crumb.loc.path}-${index}`} style={{ display: 'contents' }}>
              {index > 0 ? <span style={{ color: '#9bb', fontSize: '12px', flex: 'none' }}>›</span> : null}
              <button
                type="button"
                onClick={() => pushLoc(crumb.loc)}
                style={{
                  minWidth: 0,
                  padding: '1px 4px',
                  fontSize: '12px',
                  color: '#12354a',
                  whiteSpace: 'nowrap',
                  background: 'transparent',
                  border: 'none',
                  cursor: 'pointer',
                }}
              >
                {crumb.name}
              </button>
            </span>
          ))}
        </nav>
        <input
          type="search"
          aria-label="Поиск"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          placeholder="Поиск"
          style={{ width: '140px', flex: 'none' }}
        />
      </div>

      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: '6px',
          padding: '5px 8px',
          borderBottom: '1px solid #e3ecf5',
          background: '#fbfdff',
          flex: 'none',
          flexWrap: 'wrap',
        }}
      >
        <button type="button" style={commandBtn} onClick={scanAll}>
          ⟳ Сканировать всё
        </button>
        {ctxDrive !== null ? (
          <button type="button" style={commandBtn} disabled={!ctxDrive.enabled} onClick={() => scanDrive(ctxDrive)}>
            ⟳ Сканировать «{ctxDrive.name}»
          </button>
        ) : null}
        {activeDrive !== null ? (
          <button type="button" style={commandBtn} disabled={!activeDrive.enabled} onClick={() => setFolderCreateOpen(true)}>
            📁 Создать папку
          </button>
        ) : null}
        {activeDrive !== null ? (
          <>
            <label>
              <input
                ref={fileInputRef}
                type="file"
                multiple
                aria-label="Загрузить файлы"
                hidden
                onChange={(event) => void uploadFiles(uploadsFromFiles(Array.from(event.target.files ?? [])))}
              />
              <button type="button" style={commandBtn} onClick={() => fileInputRef.current?.click()}>
                ⭱ Загрузить файлы
              </button>
            </label>
            <label>
              <input
                ref={(node) => {
                  folderInputRef.current = node;
                  if (node !== null) node.setAttribute('webkitdirectory', '');
                }}
                type="file"
                multiple
                aria-label="Загрузить папку"
                hidden
                onChange={(event) => void uploadFiles(uploadsFromFiles(Array.from(event.target.files ?? [])))}
              />
              <button type="button" style={commandBtn} onClick={() => folderInputRef.current?.click()}>
                🗀 Загрузить папку
              </button>
            </label>
          </>
        ) : null}
        {s3Default ? (
          <button type="button" style={commandBtn} onClick={() => setCacheOpen(true)}>
            ⚙ Кэш S3
          </button>
        ) : null}
        <button type="button" className="default" style={commandBtn} onClick={() => setAddOpen(true)}>
          ＋ Добавить хранилище
        </button>
        <span style={{ flex: 1 }} />
        <span style={{ fontSize: '11px', color: '#89a' }}>{statusText}</span>
      </div>

      <div style={{ flex: 1, display: 'flex', minHeight: 0 }}>
        <ul
          className="tree-view"
          style={{
            flex: 'none',
            width: '172px',
            overflow: 'auto',
            margin: 0,
            borderRight: '1px solid #e0e9f2',
            fontSize: '13px',
            background: '#fbfdff',
            padding: '6px 4px',
          }}
        >
          <li
            onClick={() => pushLoc(ROOT)}
            style={{
              cursor: 'pointer',
              padding: '3px 6px',
              borderRadius: '3px',
              background: loc.driveKey === null ? NAV_BG : 'transparent',
              display: 'flex',
              alignItems: 'center',
              gap: '6px',
            }}
          >
            <span style={{ flex: 'none' }}>🖥</span>Мой компьютер
          </li>
          <li style={{ marginTop: '2px' }}>
            <ul style={{ marginLeft: '10px' }}>
              {drives.map((drive) => (
                <li
                  key={drive.driveKey}
                  onClick={() => pushLoc({ driveKey: drive.driveKey, path: '' })}
                  style={{
                    cursor: 'pointer',
                    padding: '3px 6px',
                    margin: '1px 0',
                    borderRadius: '3px',
                    background: loc.driveKey === drive.driveKey ? NAV_BG : 'transparent',
                    display: 'flex',
                    alignItems: 'center',
                    gap: '6px',
                    opacity: drive.enabled ? 1 : 0.55,
                  }}
                >
                  <span style={{ flex: 'none' }}>{driveIcon(drive.type)}</span>
                  <span style={{ flex: 1, minWidth: 0, ...ellipsis }}>{drive.name}</span>
                </li>
              ))}
            </ul>
          </li>
        </ul>

        <div
          onClick={() => clearSelection()}
          onDragOver={
            activeDrive !== null
              ? (event) => {
                  event.preventDefault();
                  if (!dragOver) setDragOver(true);
                }
              : undefined
          }
          onDragLeave={activeDrive !== null ? () => setDragOver(false) : undefined}
          onDrop={
            activeDrive !== null
              ? (event) => {
                  event.preventDefault();
                  setDragOver(false);
                  void uploadsFromDrop(event.dataTransfer)
                    .then((uploads) => uploadFiles(uploads))
                    .catch((cause) => showToast(errorMessage(cause, 'Не удалось прочитать папку')));
                }
              : undefined
          }
          style={{
            flex: 1,
            minWidth: 0,
            overflow: 'auto',
            padding: '12px',
            background: '#fff',
            outline: dragOver ? '2px dashed #5b9bd5' : 'none',
            outlineOffset: '-6px',
          }}
        >
          {activeDrive === null ? (
            <RootContent
              drives={driveTiles}
              selectedKey={selDrive?.driveKey ?? null}
              onSelect={(drive) => selectDrive(drive.driveKey)}
              onOpen={(drive) => pushLoc({ driveKey: drive.driveKey, path: '' })}
            />
          ) : (
            <DriveContent
              backendId={activeBackendId}
              folders={folders}
              files={files}
              selectedKeys={entryKeys}
              loading={loading}
              error={error}
              cursor={cursor}
              searching={query !== ''}
              onSelect={selectEntry}
              onOpenFolder={(entry) => pushLoc({ driveKey: activeDrive.driveKey, path: entry.path })}
              onOpenFile={(entry) => setPreviewFile(entry)}
              onLoadMore={loadMore}
            />
          )}
        </div>
      </div>

      <DetailsPane details={details} />

      {addOpen ? (
        <AddStorageForm
          onClose={() => setAddOpen(false)}
          onCreate={async (name, type, path) => {
            await addDrive(name, type, path);
            setAddOpen(false);
          }}
        />
      ) : null}

      {cacheOpen ? <CacheSettingsPopup onClose={() => setCacheOpen(false)} /> : null}

      {folderCreateOpen && activeDrive !== null ? (
        <CreateFolderPopup onClose={() => setFolderCreateOpen(false)} onCreate={createFolder} />
      ) : null}

      {deleteDriveId !== null ? (
        <Popup title="⚠ Удаление хранилища" width={420} onClose={() => setDeleteDriveId(null)}>
          <div className="has-space">
            <p style={{ marginTop: 0 }}>
              Удалить хранилище <strong>{drives.find((drive) => drive.driveKey === deleteDriveId)?.name ?? ''}</strong>?
            </p>
            <p style={{ color: '#a4441a', fontSize: '13px' }}>
              Его треки станут недоступны в эфире и плейлистах. Продолжить?
            </p>
            <div style={{ display: 'flex', gap: '8px', justifyContent: 'flex-end', marginTop: '12px' }}>
              <button type="button" onClick={() => setDeleteDriveId(null)}>
                Отмена
              </button>
              <button
                type="button"
                className="default"
                onClick={() => {
                  deleteDrive(deleteDriveId);
                  setDeleteDriveId(null);
                }}
              >
                Всё равно удалить
              </button>
            </div>
          </div>
        </Popup>
      ) : null}

      {deleteDialog !== null ? (
        <DeleteImpactPopup
          dialog={deleteDialog}
          deleting={deleting}
          onCancel={() => {
            if (!deleting) setDeleteDialog(null);
          }}
          onConfirm={() => void confirmDelete()}
        />
      ) : null}

      {previewFile !== null ? (
        <FilePreviewPopup
          entry={previewFile}
          backendId={activeBackendId}
          textPreview={textPreview}
          onClose={() => setPreviewFile(null)}
        />
      ) : null}
    </div>
  );
}

interface ComputeDetailsArgs {
  readonly activeDrive: Drive | null;
  readonly selDrive: Drive | null;
  readonly selectedEntries: StorageEntry[];
  readonly drives: Drive[];
  readonly entryCounts: { readonly folders: number; readonly files: number };
  readonly backendId: string | null;
  readonly open: (loc: Loc) => void;
  readonly scan: (drive: Drive) => void;
  readonly toggle: (backendId: string) => void;
  readonly requestDeleteDrive: (backendId: string) => void;
  readonly preview: (entry: StorageEntry) => void;
  readonly requestDeleteEntries: (entries: StorageEntry[]) => void;
}

function computeDetails(args: ComputeDetailsArgs): Details {
  const { activeDrive, selDrive, selectedEntries, drives, entryCounts, backendId } = args;

  if (selectedEntries.length > 1) {
    const fileCount = selectedEntries.filter((entry) => entry.kind === 'file').length;
    const folderCount = selectedEntries.length - fileCount;
    return {
      icon: '🗂',
      thumb: DRIVE_THUMB,
      title: `Выбрано объектов: ${selectedEntries.length}`,
      line1: `Файлов: ${fileCount} · папок: ${folderCount}`,
      line2: 'Массовые действия над выбранным',
      actions: [
        {
          label: `✕ Удалить выбранное (${selectedEntries.length})`,
          onClick: () => args.requestDeleteEntries(selectedEntries),
        },
      ],
    };
  }

  const single = selectedEntries.length === 1 ? selectedEntries[0] ?? null : null;
  if (single !== null && single.kind === 'file') {
    return {
      icon: fileIcon(single.contentType),
      thumb: DRIVE_THUMB,
      title: single.name,
      line1: `${single.contentType ?? 'application/octet-stream'} · ${formatBytes(single.sizeBytes)}`,
      line2: formatDate(single.lastModifiedUtc),
      actions: [
        { label: '↗ Открыть', primary: true, onClick: () => args.preview(single) },
        {
          label: '⭳ Скачать',
          href: storageContentUrl({ storageBackendId: backendId, path: single.path, download: true }),
        },
        { label: '✕ Удалить', onClick: () => args.requestDeleteEntries([single]) },
      ],
    };
  }
  if (single !== null && single.kind === 'folder') {
    return {
      icon: '📁',
      thumb: FOLDER_THUMB,
      title: single.name,
      line1: `Папка · ${single.path}`,
      line2: 'Двойной клик — открыть содержимое',
      actions: [
        {
          label: 'Открыть',
          primary: true,
          onClick: () => args.open({ driveKey: activeDrive?.driveKey ?? null, path: single.path }),
        },
        { label: '✕ Удалить', onClick: () => args.requestDeleteEntries([single]) },
      ],
    };
  }

  const drive = selDrive ?? activeDrive;
  if (drive !== null) {
    const actions: DetailAction[] = [
      { label: 'Открыть', primary: selDrive !== null, onClick: () => args.open({ driveKey: drive.driveKey, path: '' }) },
      { label: '⟳ Сканировать', disabled: !drive.enabled, onClick: () => args.scan(drive) },
    ];
    if (!drive.isDefault && drive.backendId !== null) {
      const backendId = drive.backendId;
      actions.push({ label: drive.enabled ? 'Выключить' : 'Включить', onClick: () => args.toggle(backendId) });
      actions.push({ label: '✕ Удалить', onClick: () => args.requestDeleteDrive(backendId) });
    }
    const line2 =
      activeDrive !== null && selDrive === null
        ? `Папок: ${entryCounts.folders} · файлов: ${entryCounts.files}`
        : `${drive.enabled ? 'Включено' : 'Выключено'}${drive.isDefault ? ' · по умолчанию' : ''}`;
    return {
      icon: driveIcon(drive.type),
      thumb: DRIVE_THUMB,
      title: drive.name,
      line1: `${typeLabel(drive.type)} · ${drive.path}`,
      line2,
      actions,
    };
  }

  return {
    icon: '🖥',
    thumb: DRIVE_THUMB,
    title: 'Мой компьютер',
    line1: `Хранилищ: ${drives.length}`,
    line2: 'Источники музыки. Хранилище по умолчанию задаётся окружением и не удаляется.',
    actions: [],
  };
}

function DetailsPane({ details }: { readonly details: Details }): ReactElement {
  return (
    <div
      style={{
        flex: 'none',
        borderTop: '1px solid #cfdcea',
        background: 'linear-gradient(#f2f7fc,#e7eff8)',
        padding: '10px 12px',
        display: 'flex',
        gap: '14px',
        alignItems: 'center',
        minHeight: '80px',
      }}
    >
      <div
        style={{
          width: '58px',
          height: '58px',
          flex: 'none',
          borderRadius: '6px',
          background: details.thumb,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          fontSize: '30px',
          boxShadow: '0 1px 4px rgba(0,50,80,.2)',
        }}
      >
        {details.icon}
      </div>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontWeight: 'bold', fontSize: '14px', ...ellipsis }}>{details.title}</div>
        <div style={{ fontSize: '12px', color: '#567', ...ellipsis }}>{details.line1}</div>
        <div style={{ fontSize: '11px', color: '#89a', ...ellipsis }}>{details.line2}</div>
      </div>
      <div
        style={{
          display: 'flex',
          gap: '6px',
          flexWrap: 'wrap',
          flex: 'none',
          justifyContent: 'flex-end',
          maxWidth: '340px',
        }}
      >
        {details.actions.map((action) =>
          action.href !== undefined ? (
            <a
              key={action.label}
              href={action.href}
              className="default"
              style={{ ...actionBtn, textDecoration: 'none', textAlign: 'center' }}
            >
              {action.label}
            </a>
          ) : (
            <button
              key={action.label}
              type="button"
              className={action.primary === true ? 'default' : undefined}
              style={actionBtn}
              disabled={action.disabled === true}
              onClick={action.onClick}
            >
              {action.label}
            </button>
          ),
        )}
      </div>
    </div>
  );
}

interface RootContentProps {
  readonly drives: Drive[];
  readonly selectedKey: string | null;
  readonly onSelect: (drive: Drive) => void;
  readonly onOpen: (drive: Drive) => void;
}

function RootContent({ drives, selectedKey, onSelect, onOpen }: RootContentProps): ReactElement {
  if (drives.length === 0) {
    return <div style={{ padding: '40px', textAlign: 'center', color: '#89a' }}>Ничего не найдено по запросу.</div>;
  }
  return (
    <>
      <div style={{ fontSize: '11px', color: '#789', marginBottom: '8px' }}>Диски и хранилища ({drives.length})</div>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill,minmax(240px,1fr))', gap: '12px' }}>
        {drives.map((drive) => {
          const selected = drive.driveKey === selectedKey;
          return (
            <div
              key={drive.driveKey}
              role="option"
              aria-selected={selected}
              tabIndex={0}
              aria-label={drive.name}
              onClick={(event) => {
                event.stopPropagation();
                onSelect(drive);
              }}
              onDoubleClick={() => onOpen(drive)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') onOpen(drive);
              }}
              style={{
                cursor: 'pointer',
                color: '#1a3a52',
                border: `1px solid ${selected ? '#5b9bd5' : '#d3e2f0'}`,
                borderRadius: '8px',
                background: selected ? SELECT_BG : '#f6f9fd',
                padding: '12px',
                display: 'flex',
                gap: '12px',
                alignItems: 'center',
              }}
            >
              <div
                style={{
                  width: '46px',
                  flex: 'none',
                  textAlign: 'center',
                  fontSize: '32px',
                  filter: drive.enabled ? 'none' : 'grayscale(1) opacity(.55)',
                }}
              >
                {driveIcon(drive.type)}
              </div>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ display: 'flex', gap: '6px', alignItems: 'baseline' }}>
                  <strong style={ellipsis}>{drive.name}</strong>
                  {drive.isDefault ? (
                    <span style={{ fontSize: '10px', color: '#b8860b', flex: 'none' }}>по умолчанию</span>
                  ) : null}
                </div>
                <div style={{ fontSize: '11px', color: '#567', ...ellipsis }}>
                  {typeLabel(drive.type)} · {drive.path}
                </div>
                <div style={{ fontSize: '11px', color: '#89a' }}>{drive.enabled ? 'включено' : 'выключено'}</div>
              </div>
            </div>
          );
        })}
      </div>
    </>
  );
}

interface DriveContentProps {
  readonly backendId: string | null;
  readonly folders: StorageEntry[];
  readonly files: StorageEntry[];
  readonly selectedKeys: ReadonlySet<string>;
  readonly loading: boolean;
  readonly error: string | null;
  readonly cursor: string | null;
  readonly searching: boolean;
  readonly onSelect: (entry: StorageEntry, additive: boolean) => void;
  readonly onOpenFolder: (entry: StorageEntry) => void;
  readonly onOpenFile: (entry: StorageEntry) => void;
  readonly onLoadMore: () => void;
}

const tileGrid: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'repeat(auto-fill,minmax(160px,1fr))',
  gap: '12px',
};

function DriveContent(props: DriveContentProps): ReactElement {
  const { backendId, folders, files, selectedKeys, loading, error, cursor, searching } = props;
  const empty = folders.length === 0 && files.length === 0;
  const isSelected = (entry: StorageEntry): boolean => selectedKeys.has(entryKeyOf(entry));

  return (
    <>
      {error !== null ? (
        <p role="alert" style={{ color: '#a4441a' }}>
          {error}
        </p>
      ) : null}
      {loading && empty ? <p>Загрузка…</p> : null}
      {!loading && error === null && empty ? (
        <div style={{ padding: '40px', textAlign: 'center', color: '#89a' }}>
          {searching ? 'Ничего не найдено по запросу.' : 'Папка пуста.'}
        </div>
      ) : null}

      {folders.length > 0 ? (
        <>
          <div style={{ fontSize: '11px', color: '#789', margin: '0 0 8px' }}>Папки ({folders.length})</div>
          <div style={{ ...tileGrid, marginBottom: files.length > 0 ? '16px' : 0 }}>
            {folders.map((entry) => (
              <Tile
                key={entry.path}
                name={entry.name}
                selected={isSelected(entry)}
                onSelect={(additive) => props.onSelect(entry, additive)}
                onOpen={() => props.onOpenFolder(entry)}
              >
                <FolderThumb />
              </Tile>
            ))}
          </div>
        </>
      ) : null}

      {files.length > 0 ? (
        <>
          <div style={{ fontSize: '11px', color: '#789', margin: '0 0 8px' }}>Файлы ({files.length})</div>
          <div style={tileGrid}>
            {files.map((entry) => (
              <Tile
                key={entry.path}
                name={entry.name}
                subtitle={formatBytes(entry.sizeBytes)}
                selected={isSelected(entry)}
                onSelect={(additive) => props.onSelect(entry, additive)}
                onOpen={() => props.onOpenFile(entry)}
              >
                <FileThumb entry={entry} backendId={backendId} />
              </Tile>
            ))}
          </div>
        </>
      ) : null}

      {cursor !== null ? (
        <div style={{ marginTop: '12px' }}>
          <button type="button" disabled={loading} onClick={props.onLoadMore}>
            Загрузить ещё
          </button>
        </div>
      ) : null}
    </>
  );
}

function FolderThumb(): ReactElement {
  return (
    <div
      style={{ height: '84px', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '52px', lineHeight: 1 }}
    >
      📁
    </div>
  );
}

function FileThumb({ entry, backendId }: { readonly entry: StorageEntry; readonly backendId: string | null }): ReactElement {
  if (isImageType(entry.contentType)) {
    return (
      <div
        style={{ height: '84px', borderRadius: '6px', overflow: 'hidden', background: '#eef4fb', boxShadow: 'inset 0 0 0 1px rgba(0,50,80,.08)' }}
      >
        <img
          src={storageContentUrl({ storageBackendId: backendId, path: entry.path })}
          alt=""
          loading="lazy"
          style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }}
        />
      </div>
    );
  }
  return (
    <div
      style={{
        height: '84px',
        borderRadius: '6px',
        background: fileThumbGradient(entry.contentType),
        boxShadow: 'inset 0 1px 0 rgba(255,255,255,.55)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        fontSize: '38px',
      }}
    >
      {fileIcon(entry.contentType)}
    </div>
  );
}

interface TileProps {
  readonly name: string;
  readonly subtitle?: string;
  readonly selected: boolean;
  readonly onSelect: (additive: boolean) => void;
  readonly onOpen: () => void;
  readonly children: ReactNode;
}

function Tile({ name, subtitle, selected, onSelect, onOpen, children }: TileProps): ReactElement {
  return (
    <div
      role="option"
      aria-selected={selected}
      tabIndex={0}
      aria-label={name}
      onClick={(event) => {
        event.stopPropagation();
        onSelect(event.ctrlKey || event.metaKey);
      }}
      onDoubleClick={onOpen}
      onKeyDown={(event) => {
        if (event.key === 'Enter') onOpen();
      }}
      style={{
        cursor: 'pointer',
        color: '#1a3a52',
        border: `1px solid ${selected ? '#5b9bd5' : '#e2ebf5'}`,
        borderRadius: '8px',
        background: selected ? SELECT_BG : '#fff',
        padding: '8px',
        boxShadow: selected ? 'inset 0 0 0 1px #5b9bd5' : '0 1px 2px rgba(0,50,80,.06)',
      }}
    >
      {children}
      <div style={{ fontWeight: 600, fontSize: '12px', marginTop: '6px', textAlign: 'center', ...ellipsis }}>{name}</div>
      {subtitle !== undefined ? (
        <div style={{ fontSize: '11px', color: '#789', textAlign: 'center' }}>{subtitle}</div>
      ) : null}
    </div>
  );
}

interface CreateFolderPopupProps {
  readonly onClose: () => void;
  readonly onCreate: (name: string) => Promise<boolean>;
}

function CreateFolderPopup({ onClose, onCreate }: CreateFolderPopupProps): ReactElement {
  const { showToast } = useToast();
  const [name, setName] = useState('');
  const [creating, setCreating] = useState(false);

  const create = async (): Promise<void> => {
    const folderName = name.trim();
    if (
      folderName === '' ||
      folderName === '.' ||
      folderName === '..' ||
      folderName.includes('/') ||
      folderName.includes('\\')
    ) {
      showToast('Укажите имя папки без разделителей пути');
      return;
    }
    setCreating(true);
    try {
      if (await onCreate(folderName)) onClose();
    } finally {
      setCreating(false);
    }
  };

  return (
    <Popup title="Новая папка" width={400} onClose={onClose}>
      <div style={{ padding: '18px' }}>
        <label htmlFor="storage-folder-name">Имя папки</label>
        <input
          id="storage-folder-name"
          autoFocus
          value={name}
          onChange={(event) => setName(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') void create();
          }}
          placeholder="Например, Новая музыка"
          style={{ boxSizing: 'border-box', display: 'block', marginTop: '6px', width: '100%' }}
        />
        <div style={{ display: 'flex', gap: '8px', justifyContent: 'flex-end', marginTop: '16px' }}>
          <button type="button" disabled={creating} onClick={onClose}>
            Отмена
          </button>
          <button type="button" className="default" disabled={creating} onClick={() => void create()}>
            {creating ? 'Создание…' : 'Создать'}
          </button>
        </div>
      </div>
    </Popup>
  );
}

interface AddStorageFormProps {
  readonly onClose: () => void;
  readonly onCreate: (name: string, type: 'local' | 's3', path: string) => Promise<void>;
}

function AddStorageForm({ onClose, onCreate }: AddStorageFormProps): ReactElement {
  const { showToast } = useToast();
  const [name, setName] = useState('');
  const [type, setType] = useState<'local' | 's3'>('s3');
  const [path, setPath] = useState('');
  const [creating, setCreating] = useState(false);

  const create = async (): Promise<void> => {
    const trimmed = name.trim();
    if (trimmed === '') {
      showToast('Укажите название хранилища');
      return;
    }
    setCreating(true);
    try {
      const value = path.trim();
      await onCreate(trimmed, type, value !== '' ? value : type === 'local' ? '/srv/media' : 's3://bucket');
    } finally {
      setCreating(false);
    }
  };

  return (
    <Popup title="Новое хранилище" width={430} onClose={onClose}>
      <div style={{ padding: '18px' }}>
        <div style={{ ...formGrid, gridTemplateColumns: 'auto 1fr', gap: '12px' }}>
          <label htmlFor="sf-name">Название</label>
          <input
            id="sf-name"
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder="Например, S3 «podcasts»"
          />
          <label htmlFor="sf-type">Тип</label>
          <select
            id="sf-type"
            value={type}
            onChange={(event) => setType(event.target.value === 'local' ? 'local' : 's3')}
          >
            <option value="local">Локальное</option>
            <option value="s3">S3</option>
          </select>
          <label htmlFor="sf-path">{type === 'local' ? 'Путь' : 'Bucket / URL'}</label>
          <input
            id="sf-path"
            value={path}
            onChange={(event) => setPath(event.target.value)}
            placeholder={type === 'local' ? '/srv/media' : 's3://bucket'}
          />
        </div>
        <div style={{ display: 'flex', gap: '8px', justifyContent: 'flex-end', marginTop: '16px' }}>
          <button type="button" onClick={onClose}>
            Отмена
          </button>
          <button type="button" className="default" onClick={() => void create()} disabled={creating}>
            {creating ? 'Создание…' : 'Создать'}
          </button>
        </div>
      </div>
    </Popup>
  );
}

const GIB = 1024 * 1024 * 1024;

function CacheSettingsPopup({ onClose }: { readonly onClose: () => void }): ReactElement {
  const { showToast } = useToast();
  const load = useCallback((): Promise<StorageCacheSettings> => getStorageCacheSettings(), []);
  const resource = useApiResource(load);

  return (
    <Popup title="Кэш S3" width={460} onClose={onClose}>
      <div style={{ padding: '18px' }}>
        <p style={{ margin: '0 0 12px', fontSize: '12px', color: COLORS.subtle, maxWidth: '60ch' }}>
          Локальный кэш S3-треков ограничен по размеру: сверх лимита давно игравшие копии удаляются, а воспроизведение
          продолжается по pre-signed ссылкам — диск сервера не забивается.
        </p>
        <ResourceView resource={resource}>
          {(settings) => (
            <CacheSettingsForm
              settings={settings}
              onSaved={() => {
                showToast('Настройки кэша сохранены');
                onClose();
              }}
            />
          )}
        </ResourceView>
      </div>
    </Popup>
  );
}

interface CacheSettingsFormProps {
  readonly settings: StorageCacheSettings;
  readonly onSaved: () => void;
}

function CacheSettingsForm({ settings, onSaved }: CacheSettingsFormProps): ReactElement {
  const { showToast } = useToast();
  const [maxGib, setMaxGib] = useState(String(Math.max(1, Math.round(settings.s3CacheMaxBytes / GIB))));
  const [ttlSeconds, setTtlSeconds] = useState(String(settings.presignTtlSeconds));
  const [saving, setSaving] = useState(false);

  const save = async (): Promise<void> => {
    const gib = Number(maxGib);
    const ttl = Number(ttlSeconds);
    if (!Number.isFinite(gib) || gib < 1 || !Number.isFinite(ttl) || ttl < 60 || ttl > 604800) {
      showToast('Проверьте значения: кэш ≥ 1 ГиБ, TTL 60–604800 с');
      return;
    }
    setSaving(true);
    try {
      await updateStorageCacheSettings({ s3CacheMaxBytes: Math.round(gib * GIB), presignTtlSeconds: Math.round(ttl) });
      onSaved();
    } catch (cause) {
      showToast(cause instanceof Error ? cause.message : 'Не удалось сохранить настройки кэша');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div style={{ ...formGrid, gridTemplateColumns: 'auto 1fr', gap: '10px', alignItems: 'center' }}>
      <label htmlFor="cache-max">Лимит кэша, ГиБ</label>
      <input id="cache-max" type="number" min={1} value={maxGib} onChange={(event) => setMaxGib(event.target.value)} />
      <label htmlFor="cache-ttl">TTL pre-signed, сек</label>
      <input
        id="cache-ttl"
        type="number"
        min={60}
        max={604800}
        value={ttlSeconds}
        onChange={(event) => setTtlSeconds(event.target.value)}
      />
      <div />
      <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
        <button type="button" className="default" onClick={() => void save()} disabled={saving}>
          {saving ? 'Сохранение…' : 'Сохранить'}
        </button>
      </div>
    </div>
  );
}

interface DeleteImpactPopupProps {
  readonly dialog: DeleteDialogState;
  readonly deleting: boolean;
  readonly onCancel: () => void;
  readonly onConfirm: () => void;
}

function DeleteImpactPopup({ dialog, deleting, onCancel, onConfirm }: DeleteImpactPopupProps): ReactElement {
  const { impact } = dialog;
  return (
    <Popup title="⚠ Подтверждение удаления" width={560} onClose={onCancel}>
      <div style={{ padding: '12px' }}>
        <p>
          Будут удалены: файлов {impact.fileCount}, папок {impact.folderCount}, всего {formatBytes(impact.totalBytes)}.
        </p>
        {impact.tracksToDeleteCount > 0 ? <p>Из библиотеки исчезнет треков: {impact.tracksToDeleteCount}.</p> : null}
        {impact.playlistMemberships.length > 0 ? (
          <p>
            Из плейлистов исчезнут треки: {impact.playlistMemberships.map((item) => `${item.playlistName} (${item.trackCount})`).join(', ')}.
          </p>
        ) : null}
        {impact.currentTrack !== null ? (
          <p>
            Сейчас играет «{impact.currentTrack.title}» — «{impact.currentTrack.artist}». Перед удалением он будет пропущен.
          </p>
        ) : null}
        {impact.sampleTracks.length > 0 ? (
          <>
            <p>Будут удалены треки:</p>
            <ul>
              {impact.sampleTracks.map((track) => (
                <li key={track.trackId}>
                  {track.title} — {track.artist}
                </li>
              ))}
            </ul>
          </>
        ) : null}
        {impact.sampleTracksTruncated ? <p>И ещё несколько треков.</p> : null}
        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '8px' }}>
          <button type="button" disabled={deleting} onClick={onCancel}>
            Отмена
          </button>
          <button type="button" className="default" disabled={deleting} onClick={onConfirm}>
            {deleting ? 'Удаление…' : 'Удалить'}
          </button>
        </div>
      </div>
    </Popup>
  );
}

interface FilePreviewPopupProps {
  readonly entry: StorageEntry;
  readonly backendId: string | null;
  readonly textPreview: string | null;
  readonly onClose: () => void;
}

function FilePreviewPopup({ entry, backendId, textPreview, onClose }: FilePreviewPopupProps): ReactElement {
  const contentType = entry.contentType;
  const src = storageContentUrl({ storageBackendId: backendId, path: entry.path });
  return (
    <Popup title={`Файл: ${entry.name}`} width={620} onClose={onClose}>
      <div style={{ padding: '12px' }}>
        <p>
          {formatBytes(entry.sizeBytes)} · {contentType ?? 'application/octet-stream'}
        </p>
        {contentType?.startsWith('audio/') === true ? <audio controls src={src} /> : null}
        {contentType?.startsWith('video/') === true ? <video controls style={{ maxWidth: '100%' }} src={src} /> : null}
        {contentType !== null && ['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(contentType) ? (
          <img src={src} alt={entry.name} style={{ maxWidth: '100%' }} />
        ) : null}
        {contentType?.startsWith('text/') === true ? (
          <pre style={{ maxHeight: '300px', overflow: 'auto', whiteSpace: 'pre-wrap' }}>{textPreview ?? 'Чтение…'}</pre>
        ) : null}
        <div style={{ marginTop: '10px' }}>
          <a href={storageContentUrl({ storageBackendId: backendId, path: entry.path, download: true })}>Скачать</a>
        </div>
      </div>
    </Popup>
  );
}
