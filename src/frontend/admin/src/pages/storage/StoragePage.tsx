import { useCallback, useEffect, useRef, useState, type ReactElement } from 'react';

import { getStorage, replaceStorage, type Storage } from '@web10/shared';

type BackendType = 'local' | 's3';

interface StorageBackendDraft {
  readonly key: string;
  readonly id: string | null;
  readonly name: string;
  readonly type: BackendType;
  readonly localRoot: string;
  readonly s3Bucket: string;
  readonly isEnabled: boolean;
}

type SaveState = 'idle' | 'pending' | 'saved';

function toDraft(backend: Storage['additionalBackends'][number]): StorageBackendDraft {
  return {
    key: backend.id,
    id: backend.id,
    name: backend.name,
    type: backend.type,
    localRoot: backend.localRoot ?? '',
    s3Bucket: backend.s3Bucket ?? '',
    isEnabled: backend.isEnabled,
  };
}

function validationError(backends: readonly StorageBackendDraft[]): string | null {
  if (backends.length > 20) {
    return 'At most 20 additional storage backends can be configured.';
  }

  for (const backend of backends) {
    if (backend.type === 'local') {
      const root = backend.localRoot.trim();
      if (root.length === 0 || !root.startsWith('/')) {
        return `Local root for ${backend.name.trim() || 'this backend'} must be an absolute path.`;
      }
    } else if (backend.s3Bucket.trim().length === 0) {
      return `S3 bucket for ${backend.name.trim() || 'this backend'} is required.`;
    }

    if (backend.name.trim().length === 0) {
      return 'Every additional backend needs a name.';
    }
  }

  return null;
}

/** Configures non-secret additional storage backends; the environment default is read-only. */
export function StoragePage(): ReactElement {
  const [storage, setStorage] = useState<Storage | null>(null);
  const [backends, setBackends] = useState<readonly StorageBackendDraft[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saveState, setSaveState] = useState<SaveState>('idle');
  const nextKey = useRef(0);

  useEffect(() => {
    let active = true;

    void getStorage()
      .then((response) => {
        if (active) {
          setStorage(response);
          setBackends(response.additionalBackends.map(toDraft));
          setLoadError(null);
        }
      })
      .catch((cause) => {
        if (active) {
          setLoadError(cause instanceof Error ? cause.message : String(cause));
        }
      })
      .finally(() => {
        if (active) {
          setIsLoading(false);
        }
      });

    return () => {
      active = false;
    };
  }, []);

  const updateBackend = useCallback(
    (key: string, changes: Partial<Omit<StorageBackendDraft, 'key' | 'id'>>) => {
      setBackends((current) =>
        current.map((backend) => (backend.key === key ? { ...backend, ...changes } : backend)),
      );
      setSaveError(null);
      setSaveState('idle');
    },
    [],
  );

  const addBackend = useCallback(() => {
    setBackends((current) => {
      if (current.length === 20) {
        return current;
      }

      nextKey.current += 1;
      return [
        ...current,
        {
          key: `new-${nextKey.current}`,
          id: null,
          name: '',
          type: 'local',
          localRoot: '',
          s3Bucket: '',
          isEnabled: true,
        },
      ];
    });
    setSaveError(null);
    setSaveState('idle');
  }, []);

  const removeBackend = useCallback((key: string) => {
    setBackends((current) => current.filter((backend) => backend.key !== key));
    setSaveError(null);
    setSaveState('idle');
  }, []);

  const moveBackend = useCallback((key: string, direction: -1 | 1) => {
    setBackends((current) => {
      const index = current.findIndex((backend) => backend.key === key);
      const destination = index + direction;
      if (index < 0 || destination < 0 || destination >= current.length) {
        return current;
      }

      const reordered = [...current];
      const [backend] = reordered.splice(index, 1);
      if (backend === undefined) {
        return current;
      }
      reordered.splice(destination, 0, backend);
      return reordered;
    });
    setSaveError(null);
    setSaveState('idle');
  }, []);

  const save = useCallback(async () => {
    const error = validationError(backends);
    if (error !== null) {
      setSaveError(error);
      setSaveState('idle');
      return;
    }

    setSaveState('pending');
    setSaveError(null);

    try {
      const response = await replaceStorage({
        additionalBackends: backends.map((backend) => ({
          id: backend.id,
          name: backend.name.trim(),
          type: backend.type,
          localRoot: backend.type === 'local' ? backend.localRoot.trim() : null,
          s3Bucket: backend.type === 's3' ? backend.s3Bucket.trim() : null,
          isEnabled: backend.isEnabled,
        })),
      });
      setStorage(response);
      setBackends(response.additionalBackends.map(toDraft));
      setSaveState('saved');
    } catch (cause) {
      setSaveError(cause instanceof Error ? cause.message : String(cause));
      setSaveState('idle');
    }
  }, [backends]);

  if (isLoading) {
    return (
      <section>
        <h2>Storage</h2>
        <p className="admin-muted">Loading storage…</p>
      </section>
    );
  }

  if (loadError !== null || storage === null) {
    return (
      <section>
        <h2>Storage</h2>
        <p role="alert" className="admin-error">
          Failed to load storage: {loadError ?? 'Storage data was unavailable.'}
        </p>
      </section>
    );
  }

  const { defaultBackend } = storage;

  return (
    <section>
      <h2>Storage</h2>
      <p className="admin-muted">
        The configured default backend is supplied by the environment and cannot be changed here.
      </p>

      <section aria-labelledby="default-storage-heading">
        <h3 id="default-storage-heading">Configured default backend (read-only)</h3>
        <dl>
          <dt>Type</dt>
          <dd>{defaultBackend.type}</dd>
          <dt>Local root</dt>
          <dd>{defaultBackend.localRoot ?? '—'}</dd>
          <dt>S3 bucket</dt>
          <dd>{defaultBackend.s3Bucket ?? '—'}</dd>
          <dt>S3 region</dt>
          <dd>{defaultBackend.s3Region ?? '—'}</dd>
          <dt>S3 service URL</dt>
          <dd>{defaultBackend.s3ServiceUrl ?? '—'}</dd>
          <dt>S3 force path style</dt>
          <dd>{defaultBackend.s3ForcePathStyle ? 'Yes' : 'No'}</dd>
        </dl>
      </section>

      <section aria-labelledby="additional-storage-heading">
        <h3 id="additional-storage-heading">Additional backends</h3>
        {backends.length === 0 ? <p className="admin-muted">No additional backends configured.</p> : null}

        {backends.map((backend, index) => {
          const isNewestDraft = backend.id === null && index === backends.length - 1;
          const editorLabel = isNewestDraft ? 'new backend' : backend.name || 'backend';
          const enabledLabel = backend.name || 'new backend';

          return (
            <fieldset key={backend.key} style={{ margin: '12px 0', maxWidth: '520px' }}>
              <legend>{backend.name || 'New backend'}</legend>

              <label htmlFor={`name-${backend.key}`}>
                Name for {editorLabel}
              </label>
              <input
                id={`name-${backend.key}`}
                value={backend.name}
                disabled={saveState === 'pending'}
                onChange={(event) => updateBackend(backend.key, { name: event.target.value })}
              />

              <label htmlFor={`type-${backend.key}`}>
                Backend type for {editorLabel}
              </label>
              <select
                id={`type-${backend.key}`}
                value={backend.type}
                disabled={saveState === 'pending'}
                onChange={(event) => {
                  const type = event.target.value;
                  if (type === 'local' || type === 's3') {
                    updateBackend(backend.key, { type });
                  }
                }}
              >
                <option value="local">Local</option>
                <option value="s3">S3</option>
              </select>

              {backend.type === 'local' ? (
                <>
                  <label htmlFor={`root-${backend.key}`}>
                    Local root for {editorLabel}
                  </label>
                  <input
                    id={`root-${backend.key}`}
                    value={backend.localRoot}
                    disabled={saveState === 'pending'}
                    onChange={(event) => updateBackend(backend.key, { localRoot: event.target.value })}
                  />
                </>
              ) : (
                <>
                  <label htmlFor={`bucket-${backend.key}`}>
                    S3 bucket for {editorLabel}
                  </label>
                  <input
                    id={`bucket-${backend.key}`}
                    value={backend.s3Bucket}
                    disabled={saveState === 'pending'}
                    onChange={(event) => updateBackend(backend.key, { s3Bucket: event.target.value })}
                  />
                </>
              )}

              <div>
                <input
                  id={`enabled-${backend.key}`}
                  type="checkbox"
                  checked={backend.isEnabled}
                  disabled={saveState === 'pending'}
                  onChange={(event) => updateBackend(backend.key, { isEnabled: event.target.checked })}
                />
                <label htmlFor={`enabled-${backend.key}`}>Enabled for {enabledLabel}</label>
              </div>

              <div style={{ display: 'flex', gap: '6px' }}>
                <button
                  type="button"
                  disabled={saveState === 'pending' || index === 0}
                  onClick={() => moveBackend(backend.key, -1)}
                >
                  Move up
                </button>
                <button
                  type="button"
                  disabled={saveState === 'pending' || index === backends.length - 1}
                  onClick={() => moveBackend(backend.key, 1)}
                >
                  Move down
                </button>
                <button
                  type="button"
                  disabled={saveState === 'pending'}
                  onClick={() => removeBackend(backend.key)}
                >
                  Remove {backend.name || 'backend'}
                </button>
              </div>
            </fieldset>
          );
        })}

        <div style={{ display: 'flex', gap: '8px', marginTop: '4px' }}>
          <button type="button" disabled={saveState === 'pending' || backends.length >= 20} onClick={addBackend}>
            Add backend
          </button>
          <button type="button" className="default" disabled={saveState === 'pending'} onClick={() => void save()}>
            {saveState === 'pending' ? 'Saving storage backends…' : 'Save storage backends'}
          </button>
        </div>

        {saveError !== null ? (
          <p role="alert" className="admin-error">
            {saveError}
          </p>
        ) : null}
        {saveState === 'saved' ? <p role="status">Saved</p> : null}
      </section>
    </section>
  );
}
