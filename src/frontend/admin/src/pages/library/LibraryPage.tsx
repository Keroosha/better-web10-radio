import { useEffect, useMemo, useState, type ReactElement } from 'react';

import {
  createLibraryScan,
  formatDuration,
  getLibraryScan,
  getStorage,
  getTracksPage,
  playNow,
  queueTrack,
  type AdminTrack,
  type LibraryScanRequest,
  type LibraryScanStatus,
  type Storage,
} from '@web10/shared';

import { TrackPopup } from '../../widgets/track-popup/TrackPopup';
import { errorMessage } from '../../shared/lib/errorMessage';
import { useToast } from '../../shared/ui/toast';
import { COLORS, iconButton } from '../../shared/ui/tokens';
import type { LibraryGroupBy } from '../../shared/lib/view';

interface LibraryPageProps {
  readonly groupBy: LibraryGroupBy;
}

interface TrackGroup {
  readonly name: string;
  readonly tracks: readonly AdminTrack[];
}


const PAGE_LIMIT = 100;

async function loadAllTracks(query: string, storageBackendId: string, signal: AbortSignal): Promise<readonly AdminTrack[]> {
  const tracks: AdminTrack[] = [];
  let cursor: string | null = null;
  do {
    const page = await getTracksPage(
      {
        query,
        limit: PAGE_LIMIT,
        ...(storageBackendId === '' ? {} : { storageBackendId }),
        ...(cursor === null ? {} : { cursor }),
      },
      { signal },
    );
    tracks.push(...page.items);
    cursor = page.nextCursor;
  } while (cursor !== null);
  return tracks;
}

async function waitForScan(scanJobId: string): Promise<LibraryScanStatus> {
  for (let attempt = 0; attempt < 600; attempt += 1) {
    const { promise, resolve } = Promise.withResolvers<void>();
    setTimeout(resolve, 1500);
    await promise;
    const status = await getLibraryScan(scanJobId);
    if (status.status === 'completed' || status.status === 'failed') {
      return status;
    }
  }
  throw new Error('Сканирование не завершилось за 15 минут');
}

/** Библиотека: выбор хранилища, поиск, группировка по альбомам/исполнителям. */
export function LibraryPage({ groupBy: navGroupBy }: LibraryPageProps): ReactElement {
  const { showToast } = useToast();
  const [storage, setStorage] = useState<Storage | null>(null);
  const [storageId, setStorageId] = useState<string>('');
  const [query, setQuery] = useState('');
  const [groupBy, setGroupBy] = useState<LibraryGroupBy>(navGroupBy);
  const [tracks, setTracks] = useState<readonly AdminTrack[]>([]);
  const [searching, setSearching] = useState(false);
  const [openGroup, setOpenGroup] = useState<string | null>(null);
  const [popupTrack, setPopupTrack] = useState<AdminTrack | null>(null);
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => setGroupBy(navGroupBy), [navGroupBy]);

  useEffect(() => {
    void getStorage()
      .then(setStorage)
      .catch(() => setStorage(null));
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;
    setSearching(true);
    const timer = setTimeout(() => {
      void loadAllTracks(query.trim(), storageId, controller.signal)
        .then((items) => {
          if (active) {
            setTracks(items);
            setSearching(false);
          }
        })
        .catch(() => {
          if (active) {
            setSearching(false);
          }
        });
    }, 300);
    return () => {
      active = false;
      controller.abort();
      clearTimeout(timer);
    };
  }, [query, storageId, reloadKey]);

  const groups: TrackGroup[] = useMemo(() => {
    const map = new Map<string, AdminTrack[]>();
    for (const track of tracks) {
      const key = (groupBy === 'album' ? track.album : track.artist) || 'Без названия';
      const bucket = map.get(key);
      if (bucket === undefined) {
        map.set(key, [track]);
      } else {
        bucket.push(track);
      }
    }
    return [...map.entries()].map(([name, tracks]) => ({ name, tracks }));
  }, [tracks, groupBy]);

  const openTracks = groups.find((group) => group.name === openGroup)?.tracks ?? [];

  const scan = (): void => {
    if (storageId === '' && storage === null) {
      showToast('Список хранилищ ещё загружается');
      return;
    }

    const requests: LibraryScanRequest[] =
      storageId === ''
        ? [
            {},
            ...(storage?.additionalBackends
              .filter((backend) => backend.isEnabled)
              .map((backend) => ({ storageBackendId: backend.id })) ?? []),
          ]
        : [{ storageBackendId: storageId }];

    void Promise.all(requests.map((request) => createLibraryScan(request)))
      .then(async (accepted) => {
        showToast(`Сканирование запущено: хранилищ ${accepted.length}`);
        return Promise.all(accepted.map((job) => waitForScan(job.scanJobId)));
      })
      .then((statuses) => {
        const failed = statuses.find((status) => status.status === 'failed');
        if (failed !== undefined) {
          showToast(`Сканирование не удалось: ${failed.failureReason ?? 'неизвестная ошибка'}`);
          return;
        }
        const discovered = statuses.reduce((total, status) => total + status.discoveredCount, 0);
        showToast(`Найдено треков: ${discovered}`);
        setReloadKey((key) => key + 1);
      })
      .catch((cause) => showToast(errorMessage(cause, 'Ошибка сканирования')));
  };

  const toggleBg = (active: boolean): string => (active ? COLORS.selection : '');

  return (
    <div>
      <div style={{ display: 'flex', gap: '10px', alignItems: 'flex-end', flexWrap: 'wrap', marginBottom: '14px' }}>
        <div className="field-row-stacked" style={{ minWidth: '180px' }}>
          <label htmlFor="lib-storage">Хранилище</label>
          <select id="lib-storage" value={storageId} onChange={(event) => setStorageId(event.target.value)}>
            <option value="">Все хранилища</option>
            {storage?.additionalBackends.map((backend) => (
              <option key={backend.id} value={backend.id}>
                {backend.name}
              </option>
            ))}
          </select>
        </div>
        <div className="field-row-stacked" style={{ flex: 1, minWidth: '200px' }}>
          <label htmlFor="lib-search">Поиск</label>
          <input
            id="lib-search"
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Название, исполнитель, альбом…"
          />
        </div>
        {searching ? (
          <div style={{ display: 'flex', alignItems: 'center', gap: '6px', color: COLORS.subtle, fontSize: '12px', paddingBottom: '3px' }}>
            Поиск…
          </div>
        ) : null}
        <div style={{ display: 'flex', gap: '2px' }}>
          <button type="button" onClick={() => setGroupBy('album')} style={{ background: toggleBg(groupBy === 'album') }}>
            По альбомам
          </button>
          <button type="button" onClick={() => setGroupBy('artist')} style={{ background: toggleBg(groupBy === 'artist') }}>
            По исполнителям
          </button>
        </div>
        <button type="button" onClick={scan}>
          ⟳ Сканировать
        </button>
      </div>

      {groups.length === 0 ? (
        <div style={{ padding: '40px', textAlign: 'center', color: '#89a' }}>
          В этом хранилище нет треков по запросу.
        </div>
      ) : null}

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill,minmax(150px,1fr))', gap: '12px' }}>
        {groups.map((group) => {
          const expanded = group.name === openGroup;
          const cover = group.tracks[0]?.coverImageUrl;
          return (
            <div
              key={group.name}
              onClick={() => setOpenGroup(expanded ? null : group.name)}
              style={{
                cursor: 'pointer',
                border: `1px solid ${expanded ? '#5b9bd5' : '#d3e2f0'}`,
                borderRadius: '8px',
                background: expanded ? 'linear-gradient(#dcecfb,#bcdcf7)' : '#f6f9fd',
                padding: '10px',
                textAlign: 'center',
              }}
            >
              <div
                style={{
                  width: '100%',
                  aspectRatio: '1',
                  borderRadius: '6px',
                  background: cover ? `center / cover no-repeat url("${cover}")` : 'linear-gradient(135deg,#c3a6ff,#ffd6a6)',
                  boxShadow: '0 2px 6px rgba(0,50,80,.2)',
                  marginBottom: '8px',
                }}
              />
              <div style={{ fontWeight: 600, fontSize: '13px', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {group.name}
              </div>
              <div style={{ fontSize: '11px', color: '#789' }}>{group.tracks.length} тр.</div>
            </div>
          );
        })}
      </div>

      {openGroup !== null && openTracks.length > 0 ? (
        <div style={{ marginTop: '16px', border: '1px solid #cddff0', borderRadius: '8px', padding: '12px', background: '#fafcff' }}>
          <div style={{ fontWeight: 'bold', fontSize: '14px', marginBottom: '8px' }}>{openGroup}</div>
          <table className="interactive" style={{ width: '100%' }}>
            <thead>
              <tr>
                <th style={{ textAlign: 'left' }}>Название</th>
                <th style={{ textAlign: 'left' }}>Исполнитель</th>
                <th>Длит.</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {openTracks.map((track) => (
                <tr key={track.id}>
                  <td>{track.title}</td>
                  <td style={{ color: COLORS.subtle }}>{track.artist}</td>
                  <td style={{ textAlign: 'center', color: '#789' }}>{formatDuration(track.durationMs)}</td>
                  <td style={{ textAlign: 'right' }}>
                    <span style={{ display: 'inline-flex', gap: '4px', justifyContent: 'flex-end' }}>
                      <button
                        type="button"
                        title="Играть сейчас"
                        style={iconButton}
                        onClick={() =>
                          playNow({ trackId: track.id })
                            .then(() => showToast('Играет сейчас'))
                            .catch(() => showToast('Ошибка'))
                        }
                      >
                        ▶
                      </button>
                      <button
                        type="button"
                        title="В очередь"
                        style={iconButton}
                        onClick={() =>
                          queueTrack({ trackId: track.id })
                            .then(() => showToast('Добавлено в очередь'))
                            .catch(() => showToast('Ошибка'))
                        }
                      >
                        ＋
                      </button>
                      <button type="button" title="Метаданные и плейлисты" style={iconButton} onClick={() => setPopupTrack(track)}>
                        ⋯
                      </button>
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}

      {popupTrack !== null ? (
        <TrackPopup
          track={popupTrack}
          onClose={() => setPopupTrack(null)}
          onSaved={() => setReloadKey((key) => key + 1)}
        />
      ) : null}
    </div>
  );
}
