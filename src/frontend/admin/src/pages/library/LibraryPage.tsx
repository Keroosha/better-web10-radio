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

interface TracksState {
  readonly tracks: readonly AdminTrack[];
  readonly hasMore: boolean;
}

interface TrackGroup {
  readonly name: string;
  readonly tracks: readonly AdminTrack[];
}

const PAGE_LIMIT = 100;

/** Библиотека: выбор хранилища, поиск, группировка по альбомам/исполнителям. */
export function LibraryPage({ groupBy: navGroupBy }: LibraryPageProps): ReactElement {
  const { showToast } = useToast();
  const [storage, setStorage] = useState<Storage | null>(null);
  const [storageId, setStorageId] = useState<string>('');
  const [query, setQuery] = useState('');
  const [groupBy, setGroupBy] = useState<LibraryGroupBy>(navGroupBy);
  const [state, setState] = useState<TracksState>({ tracks: [], hasMore: false });
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
      void getTracksPage(
        {
          query: query.trim(),
          limit: PAGE_LIMIT,
          ...(storageId === '' ? {} : { storageBackendId: storageId }),
        },
        { signal: controller.signal },
      )
        .then((page) => {
          if (active) {
            setState({ tracks: page.items, hasMore: page.nextCursor !== null });
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
    for (const track of state.tracks) {
      const key = (groupBy === 'album' ? track.album : track.artist) || 'Без названия';
      const bucket = map.get(key);
      if (bucket === undefined) {
        map.set(key, [track]);
      } else {
        bucket.push(track);
      }
    }
    return [...map.entries()].map(([name, tracks]) => ({ name, tracks }));
  }, [state.tracks, groupBy]);

  const openTracks = groups.find((group) => group.name === openGroup)?.tracks ?? [];

  const scan = (): void => {
    createLibraryScan(storageId === '' ? {} : { storageBackendId: storageId })
      .then((accepted) => {
        showToast('Сканирование запущено…');
        void pollScan(accepted.scanJobId);
      })
      .catch((cause) => showToast(errorMessage(cause, 'Ошибка сканирования')));
  };

  const pollScan = async (scanJobId: string): Promise<void> => {
    for (let attempt = 0; attempt < 60; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 1500));
      const status = await getLibraryScan(scanJobId).catch(() => null);
      if (status === null) {
        return;
      }
      if (status.status === 'completed') {
        showToast(`Найдено треков: ${status.discoveredCount}`);
        setReloadKey((key) => key + 1);
        return;
      }
      if (status.status === 'failed') {
        showToast(`Сканирование не удалось: ${status.failureReason ?? ''}`);
        return;
      }
    }
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
      {state.hasMore ? (
        <p style={{ fontSize: '11px', color: COLORS.muted, margin: '0 0 8px' }}>
          Показаны первые {PAGE_LIMIT} треков — уточните поиск, чтобы увидеть остальные.
        </p>
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
