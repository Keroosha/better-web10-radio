import { useEffect, useState, type ReactElement } from 'react';

import {
  createLibraryScan,
  getLibraryScan,
  getStorage,
  type LibraryScanStatus,
  type Storage,
} from '@web10/shared';

type StorageState =
  | { readonly status: 'loading' }
  | { readonly status: 'error'; readonly message: string }
  | { readonly status: 'ready'; readonly storage: Storage };

function messageFrom(cause: Error): string {
  return cause.message || 'The request could not be completed.';
}

/** Starts one storage scan and follows its pinned job status through a terminal result. */
export function LibraryScanPage(): ReactElement {
  const [storageState, setStorageState] = useState<StorageState>({ status: 'loading' });
  const [selectedStorageBackendId, setSelectedStorageBackendId] = useState('');
  const [scanJobId, setScanJobId] = useState<string | null>(null);
  const [scan, setScan] = useState<LibraryScanStatus | null>(null);
  const [isStarting, setIsStarting] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;

    void getStorage()
      .then((storage) => {
        if (active) {
          setStorageState({ status: 'ready', storage });
        }
      })
      .catch((cause) => {
        if (active) {
          setStorageState({
            status: 'error',
            message: cause instanceof Error ? messageFrom(cause) : 'Storage choices could not be loaded.',
          });
        }
      });

    return () => {
      active = false;
    };
  }, []);
  useEffect(() => {
    if (scanJobId === null) {
      return;
    }

    const controller = new AbortController();
    let active = true;
    let timeoutId: number | null = null;

    const poll = (): void => {
      void getLibraryScan(scanJobId, { signal: controller.signal })
        .then((nextScan) => {
          if (!active) {
            return;
          }

          setScan(nextScan);
          if (nextScan.status === 'completed' || nextScan.status === 'failed') {
            return;
          }

          timeoutId = window.setTimeout(poll, 1000);
        })
        .catch((cause) => {
          if (active) {
            setActionError(cause instanceof Error ? messageFrom(cause) : 'The scan status could not be loaded.');
          }
        });
    };

    poll();

    return () => {
      active = false;
      controller.abort();
      if (timeoutId !== null) {
        window.clearTimeout(timeoutId);
      }
    };
  }, [scanJobId]);

  const startScan = async (): Promise<void> => {
    if (isStarting) {
      return;
    }

    setIsStarting(true);
    setActionError(null);
    setScan(null);
    setScanJobId(null);

    try {
      const accepted = await createLibraryScan(
        selectedStorageBackendId === '' ? {} : { storageBackendId: selectedStorageBackendId },
      );
      setScanJobId(accepted.scanJobId);
    } catch (cause) {
      setActionError(cause instanceof Error ? messageFrom(cause) : 'The scan could not be started.');
    } finally {
      setIsStarting(false);
    }
  };

  return (
    <section>
      <h2>Library scan</h2>
      <p className="admin-muted">
        Scan the default storage or an enabled additional backend for playable tracks.
      </p>

      {storageState.status === 'loading' ? <p className="admin-muted">Loading storage choices…</p> : null}
      {storageState.status === 'error' ? <p role="alert">Failed to load storage choices: {storageState.message}</p> : null}

      {storageState.status === 'ready' ? (
        <div style={{ display: 'grid', gap: '10px', maxWidth: '520px' }}>
          <div className="group">
            <label htmlFor="library-storage-backend">Storage backend</label>
            <select
              id="library-storage-backend"
              value={selectedStorageBackendId}
              onChange={(event) => setSelectedStorageBackendId(event.target.value)}
              disabled={isStarting}
            >
              <option value="">Default ({storageState.storage.defaultBackend.type})</option>
              {storageState.storage.additionalBackends.map((backend) => (
                <option key={backend.id} value={backend.id} disabled={!backend.isEnabled}>
                  {backend.name} ({backend.type}){backend.isEnabled ? '' : ' — disabled'}
                </option>
              ))}
            </select>
          </div>
          {storageState.storage.additionalBackends.length === 0 ? (
            <p className="admin-muted" style={{ margin: 0 }}>No additional storage backends are configured.</p>
          ) : null}
          <div>
            <button type="button" className="default" onClick={() => void startScan()} disabled={isStarting}>
              {isStarting ? 'Starting scan…' : 'Start scan'}
            </button>
          </div>
        </div>
      ) : null}

      {scan === null && actionError === null && storageState.status === 'ready' ? (
        <p className="admin-muted" style={{ marginTop: '14px' }}>No scan has been started yet.</p>
      ) : null}

      {actionError !== null ? <p role="alert">{actionError}</p> : null}
      {scan !== null ? (
        <div aria-live="polite" style={{ marginTop: '14px' }}>
          <p>
            Scan status: <strong>{scan.status}</strong>
          </p>
          <p>{scan.discoveredCount} {scan.discoveredCount === 1 ? 'track' : 'tracks'} found</p>
          {scan.status === 'failed' ? (
            <p role="alert">{scan.failureReason ?? 'The scan failed without a reported reason.'}</p>
          ) : null}
          {scan.status === 'queued' || scan.status === 'running' ? <p>Checking scan progress every second…</p> : null}
        </div>
      ) : null}
    </section>
  );
}
