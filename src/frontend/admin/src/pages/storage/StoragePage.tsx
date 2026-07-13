import { useCallback, useState, type ReactElement } from 'react';

import {
  createLibraryScan,
  getStorage,
  replaceStorage,
  getStorageCacheSettings,
  updateStorageCacheSettings,
  type Storage,
  type StorageAdditionalBackend,
  type StorageCacheSettings,
  type StorageReplaceRequest,
} from '@web10/shared';

import { useApiResource } from '../../shared/lib/useApiResource';
import { ResourceView } from '../../shared/ui/ResourceView';
import { Popup } from '../../shared/ui/Popup';
import { errorMessage } from '../../shared/lib/errorMessage';
import { useToast } from '../../shared/ui/toast';
import { COLORS, formGrid } from '../../shared/ui/tokens';
import { StorageFileManager } from '../../features/storage-file-manager/StorageFileManager';

type Backend = StorageAdditionalBackend;
interface FileManagerTarget {
  readonly storageBackendId: string | null;
  readonly storageName: string;
  readonly enabled: boolean;
}

function toReplaceItem(backend: Backend): StorageReplaceRequest['additionalBackends'][number] {
  return {
    // A new backend from the add form carries an empty id; the replace contract wants null.
    id: backend.id === '' ? null : backend.id,
    name: backend.name,
    type: backend.type,
    localRoot: backend.localRoot,
    s3Bucket: backend.s3Bucket,
    isEnabled: backend.isEnabled,
  };
}

/** Хранилища: карточки источников + добавление и удаление через попапы (ПРАВИЛА §6). */
export function StoragePage(): ReactElement {
  const { showToast } = useToast();
  const [reloadKey, setReloadKey] = useState(0);
  const load = useCallback((): Promise<Storage> => getStorage(), [reloadKey]);
  const resource = useApiResource(load, reloadKey);
  const [deleteTarget, setDeleteTarget] = useState<Backend | null>(null);
  const [fileManagerTarget, setFileManagerTarget] = useState<FileManagerTarget | null>(null);
  const [formOpen, setFormOpen] = useState(false);

  const persist = async (backends: Backend[], message: string): Promise<void> => {
    try {
      await replaceStorage({ additionalBackends: backends.map(toReplaceItem) });
      showToast(message);
      setReloadKey((key) => key + 1);
    } catch (cause) {
      showToast(cause instanceof Error ? cause.message : 'Не удалось сохранить хранилища');
    }
  };

  const scan = (backendId: string, name: string): void => {
    createLibraryScan({ storageBackendId: backendId })
      .then(() => showToast(`Сканирование «${name}»…`))
      .catch((cause) => showToast(errorMessage(cause, 'Ошибка')));
  };

  const scanAll = (): void => {
    createLibraryScan({})
      .then(() => showToast('Сканирование запущено…'))
      .catch((cause) => showToast(errorMessage(cause, 'Ошибка')));
  };

  return (
    <div>
      <ResourceView resource={resource}>
        {(storage) =>
          fileManagerTarget !== null ? (
            <StorageFileManager
              storageBackendId={fileManagerTarget.storageBackendId}
              storageName={fileManagerTarget.storageName}
              enabled={fileManagerTarget.enabled}
              onBack={() => setFileManagerTarget(null)}
            />
          ) : (
            <>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '10px', flexWrap: 'wrap', gap: '8px' }}>
              <p style={{ margin: 0, fontSize: '12px', color: COLORS.subtle, maxWidth: '60ch' }}>
                Источники музыки. Хранилище по умолчанию задаётся окружением и не удаляется.
              </p>
              <div style={{ display: 'flex', gap: '6px', flexWrap: 'wrap' }}>
                <button type="button" onClick={scanAll}>
                  ⟳ Сканировать всё
                </button>
                <button type="button" className="default" onClick={() => setFormOpen(true)}>
                  ＋ Добавить хранилище
                </button>
              </div>
            </div>
            <div style={{ display: 'grid', gap: '10px' }}>
              <StorageCard
                icon={storage.defaultBackend.type === 'local' ? '🖴' : '☁'}
                name="Хранилище по умолчанию"
                defaultLabel="по умолчанию"
                typeLabel={storage.defaultBackend.type === 'local' ? 'Локальное' : 'S3'}
                path={storage.defaultBackend.localRoot ?? storage.defaultBackend.s3Bucket ?? ''}
                enabled
                onOpenFiles={() => setFileManagerTarget({ storageBackendId: null, storageName: 'Хранилище по умолчанию', enabled: true })}
              />
              {storage.additionalBackends.map((backend) => (
                <StorageCard
                  key={backend.id}
                  icon={backend.type === 'local' ? '🖴' : '☁'}
                  name={backend.name}
                  defaultLabel=""
                  typeLabel={backend.type === 'local' ? 'Локальное' : 'S3'}
                  path={backend.localRoot ?? backend.s3Bucket ?? ''}
                  enabled={backend.isEnabled}
                  onOpenFiles={() => setFileManagerTarget({ storageBackendId: backend.id, storageName: backend.name, enabled: backend.isEnabled })}
                  onScan={() => scan(backend.id, backend.name)}
                  onToggle={() =>
                    void persist(
                      storage.additionalBackends.map((item) =>
                        item.id === backend.id ? { ...item, isEnabled: !item.isEnabled } : item,
                      ),
                      backend.isEnabled ? 'Хранилище выключено' : 'Хранилище включено',
                    )
                  }
                  onDelete={() => setDeleteTarget(backend)}
                />
              ))}
            </div>

            {storage.defaultBackend.type === 's3' ? <CacheSettingsCard /> : null}

            {formOpen ? (
              <StorageForm
                onClose={() => setFormOpen(false)}
                onCreate={async (backend) => {
                  await persist([...storage.additionalBackends, backend], 'Хранилище добавлено');
                  setFormOpen(false);
                }}
              />
            ) : null}

            {deleteTarget !== null ? (
              <Popup title="⚠ Удаление хранилища" warning width={420} onClose={() => setDeleteTarget(null)}>
                <div className="has-space">
                  <p style={{ marginTop: 0 }}>
                    Удалить хранилище <strong>{deleteTarget.name}</strong>?
                  </p>
                  <p style={{ color: '#a4441a', fontSize: '13px' }}>
                    Его треки станут недоступны в эфире и плейлистах. Продолжить?
                  </p>
                  <div style={{ display: 'flex', gap: '8px', justifyContent: 'flex-end', marginTop: '12px' }}>
                    <button type="button" onClick={() => setDeleteTarget(null)}>
                      Отмена
                    </button>
                    <button
                      type="button"
                      className="default"
                      onClick={() => {
                        void persist(
                          storage.additionalBackends.filter((item) => item.id !== deleteTarget.id),
                          'Хранилище удалено',
                        );
                        setDeleteTarget(null);
                      }}
                    >
                      Всё равно удалить
                    </button>
                  </div>
                </div>
              </Popup>
            ) : null}
          </>
        )}
      </ResourceView>
    </div>
  );
}

interface StorageCardProps {
  readonly icon: string;
  readonly name: string;
  readonly defaultLabel: string;
  readonly typeLabel: string;
  readonly path: string;
  readonly enabled: boolean;
  readonly onScan?: () => void;
  readonly onOpenFiles?: () => void;
  readonly onToggle?: () => void;
  readonly onDelete?: () => void;
}

function StorageCard(props: StorageCardProps): ReactElement {
  return (
    <div style={{ border: '1px solid #cddff0', borderRadius: '8px', padding: '12px', background: '#fafcff', display: 'flex', gap: '14px', alignItems: 'center', flexWrap: 'wrap' }}>
      <div style={{ width: '40px', height: '40px', borderRadius: '6px', background: 'linear-gradient(#eaf3fb,#cfe6fb)', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '18px' }}>
        {props.icon}
      </div>
      <div style={{ flex: 1, minWidth: '180px' }}>
        <div style={{ display: 'flex', gap: '8px', alignItems: 'baseline' }}>
          <strong>{props.name}</strong>
          {props.defaultLabel !== '' ? <span style={{ fontSize: '11px', color: '#b8860b' }}>{props.defaultLabel}</span> : null}
        </div>
        <div style={{ fontSize: '12px', color: COLORS.subtle }}>
          {props.typeLabel} · {props.path}
        </div>
      </div>
      <div style={{ display: 'flex', gap: '6px', flexWrap: 'wrap' }}>
        {props.onOpenFiles !== undefined ? (
          <button type="button" onClick={props.onOpenFiles} style={{ minWidth: 0, padding: '3px 10px' }}>
            Открыть файлы
          </button>
        ) : null}
        {props.onScan !== undefined ? (
          <button type="button" onClick={props.onScan} style={{ minWidth: 0, padding: '3px 10px' }}>
            ⟳ Сканировать
          </button>
        ) : null}
        {props.onToggle !== undefined ? (
          <button type="button" onClick={props.onToggle} style={{ minWidth: 0, padding: '3px 10px' }}>
            {props.enabled ? 'включено' : 'выключено'}
          </button>
        ) : null}
        {props.onDelete !== undefined ? (
          <button type="button" onClick={props.onDelete} style={{ minWidth: 0, padding: '3px 10px' }}>
            ✕ Удалить
          </button>
        ) : null}
      </div>
    </div>
  );
}

interface StorageFormProps {
  readonly onClose: () => void;
  readonly onCreate: (backend: Backend) => Promise<void>;
}

function StorageForm({ onClose, onCreate }: StorageFormProps): ReactElement {
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
      await onCreate({
        id: '',
        name: trimmed,
        type,
        localRoot: type === 'local' ? path.trim() || '/srv/media' : null,
        s3Bucket: type === 's3' ? path.trim() || 's3://bucket' : null,
        isEnabled: true,
      });
    } finally {
      setCreating(false);
    }
  };

  return (
    <Popup title="Добавить хранилище" onClose={onClose}>
      <div style={{ padding: '18px' }}>
        <div style={{ ...formGrid, gridTemplateColumns: 'auto 1fr', gap: '12px' }}>
          <label htmlFor="sf-name">Название</label>
          <input id="sf-name" value={name} onChange={(event) => setName(event.target.value)} placeholder="Например, S3 «podcasts»" />
          <label htmlFor="sf-type">Тип</label>
          <select id="sf-type" value={type} onChange={(event) => setType(event.target.value === 'local' ? 'local' : 's3')}>
            <option value="local">Локальное</option>
            <option value="s3">S3</option>
          </select>
          <label htmlFor="sf-path">{type === 'local' ? 'Путь' : 'Bucket / URL'}</label>
          <input id="sf-path" value={path} onChange={(event) => setPath(event.target.value)} placeholder={type === 'local' ? '/srv/media' : 's3://bucket'} />
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

function CacheSettingsCard(): ReactElement {
  const { showToast } = useToast();
  const [reloadKey, setReloadKey] = useState(0);
  const load = useCallback((): Promise<StorageCacheSettings> => getStorageCacheSettings(), [reloadKey]);
  const resource = useApiResource(load, reloadKey);

  return (
    <div style={{ border: '1px solid #cddff0', borderRadius: '8px', padding: '12px', background: '#fafcff', marginTop: '10px' }}>
      <strong>Кэш S3</strong>
      <p style={{ margin: '4px 0 10px', fontSize: '12px', color: COLORS.subtle, maxWidth: '60ch' }}>
        Локальный кэш S3-треков ограничен по размеру: сверх лимита давно игравшие копии удаляются, а
        воспроизведение продолжается по pre-signed ссылкам — диск сервера не забивается.
      </p>
      <ResourceView resource={resource}>
        {(settings) => (
          <CacheSettingsForm
            settings={settings}
            onSaved={() => {
              showToast('Настройки кэша сохранены');
              setReloadKey((key) => key + 1);
            }}
          />
        )}
      </ResourceView>
    </div>
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
      await updateStorageCacheSettings({
        s3CacheMaxBytes: Math.round(gib * GIB),
        presignTtlSeconds: Math.round(ttl),
      });
      onSaved();
    } catch (cause) {
      showToast(cause instanceof Error ? cause.message : 'Не удалось сохранить настройки кэша');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div style={{ ...formGrid, gridTemplateColumns: 'auto 1fr', gap: '10px', maxWidth: '440px', alignItems: 'center' }}>
      <label htmlFor="cache-max">Лимит кэша, ГиБ</label>
      <input id="cache-max" type="number" min={1} value={maxGib} onChange={(event) => setMaxGib(event.target.value)} />
      <label htmlFor="cache-ttl">TTL pre-signed, сек</label>
      <input id="cache-ttl" type="number" min={60} max={604800} value={ttlSeconds} onChange={(event) => setTtlSeconds(event.target.value)} />
      <div />
      <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
        <button type="button" className="default" onClick={() => void save()} disabled={saving}>
          {saving ? 'Сохранение…' : 'Сохранить'}
        </button>
      </div>
    </div>
  );
}
