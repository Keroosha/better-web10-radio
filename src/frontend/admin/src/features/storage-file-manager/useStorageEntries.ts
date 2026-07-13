import { useCallback, useEffect, useRef, useState } from 'react';

import { getStorageEntries, type StorageEntriesQuery, type StorageEntry } from '@web10/shared';

import { errorMessage } from '../../shared/lib/errorMessage';

export interface StorageEntriesController {
  readonly entries: StorageEntry[];
  readonly cursor: string | null;
  readonly loading: boolean;
  readonly error: string | null;
  readonly loadMore: () => void;
  readonly reload: () => void;
}

function entryKey(entry: Pick<StorageEntry, 'path' | 'kind'>): string {
  return `${entry.kind}\u0000${entry.path}`;
}

/**
 * Paginated, abortable listing of one backend's entries at a path. `enabled: false`
 * (the "My Computer" root) leaves the listing empty and issues no request. A generation
 * counter drops results from a superseded path so switching folders never races.
 */
export function useStorageEntries(
  storageBackendId: string | null,
  path: string,
  enabled: boolean,
): StorageEntriesController {
  const [entries, setEntries] = useState<StorageEntry[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const abort = useRef<AbortController | null>(null);
  const generation = useRef(0);

  const loadPage = useCallback(
    async (nextCursor: string | null, append: boolean): Promise<void> => {
      if (!enabled) return;
      abort.current?.abort();
      const controller = new AbortController();
      abort.current = controller;
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
          const deduped = new Map(values.map((entry) => [entryKey(entry), entry]));
          return [...deduped.values()];
        });
        setCursor(page.nextCursor);
      } catch (cause) {
        if (!controller.signal.aborted && currentGeneration === generation.current) {
          setError(errorMessage(cause, 'Не удалось прочитать папку'));
        }
      } finally {
        if (!controller.signal.aborted && currentGeneration === generation.current) setLoading(false);
      }
    },
    [enabled, path, storageBackendId],
  );

  useEffect(() => {
    generation.current += 1;
    setEntries([]);
    setCursor(null);
    setError(null);
    if (enabled) void loadPage(null, false);
    return () => {
      generation.current += 1;
      abort.current?.abort();
    };
  }, [enabled, loadPage]);

  const loadMore = useCallback((): void => {
    if (cursor !== null) void loadPage(cursor, true);
  }, [cursor, loadPage]);

  const reload = useCallback((): void => {
    generation.current += 1;
    void loadPage(null, false);
  }, [loadPage]);

  return { entries, cursor, loading, error, loadMore, reload };
}
