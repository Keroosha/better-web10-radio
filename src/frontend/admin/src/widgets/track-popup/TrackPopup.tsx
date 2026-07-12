import { useEffect, useState, type ReactElement } from 'react';

import {
  getPlaylistItems,
  getPlaylists,
  replacePlaylistItems,
  updateTrackMetadata,
  type AdminTrack,
  type Playlist,
} from '@web10/shared';

import { Popup } from '../../shared/ui/Popup';
import { useToast } from '../../shared/ui/toast';
import { COLORS, formGrid } from '../../shared/ui/tokens';

interface TrackPopupProps {
  readonly track: AdminTrack;
  readonly onClose: () => void;
  readonly onSaved: () => void;
}

interface Membership {
  readonly playlist: Playlist;
  readonly trackIds: readonly string[];
}

/** Трек — метаданные (ID3) и членство в плейлистах (ПРАВИЛА §6). */
export function TrackPopup({ track, onClose, onSaved }: TrackPopupProps): ReactElement {
  const { showToast } = useToast();
  const [tab, setTab] = useState<'id3' | 'playlists'>('id3');
  const [title, setTitle] = useState(track.title);
  const [artist, setArtist] = useState(track.artist);
  const [album, setAlbum] = useState(track.album);
  const [savingId3, setSavingId3] = useState(false);
  const [memberships, setMemberships] = useState<Membership[] | null>(null);

  useEffect(() => {
    let active = true;
    void getPlaylists()
      .then((playlists) =>
        Promise.all(
          playlists
            .filter((playlist) => playlist.source === 'manual')
            .map((playlist) =>
              getPlaylistItems(playlist.id).then((items) => ({
                playlist,
                trackIds: items.map((item) => item.trackId),
              })),
            ),
        ),
      )
      .then((loaded) => {
        if (active) {
          setMemberships(loaded);
        }
      })
      .catch(() => {
        if (active) {
          setMemberships([]);
        }
      });
    return () => {
      active = false;
    };
  }, [track.id]);

  const saveId3 = async (): Promise<void> => {
    setSavingId3(true);
    try {
      await updateTrackMetadata(track.id, { title: title.trim(), artist: artist.trim(), album: album.trim() || null });
      showToast('Метаданные сохранены');
      onSaved();
      onClose();
    } catch (cause) {
      showToast(cause instanceof Error ? cause.message : 'Не удалось сохранить');
    } finally {
      setSavingId3(false);
    }
  };

  const toggleMembership = async (entry: Membership): Promise<void> => {
    const inPlaylist = entry.trackIds.includes(track.id);
    const nextIds = inPlaylist
      ? entry.trackIds.filter((id) => id !== track.id)
      : [...entry.trackIds, track.id];
    setMemberships(
      (current) =>
        current?.map((item) => (item.playlist.id === entry.playlist.id ? { ...item, trackIds: nextIds } : item)) ?? null,
    );
    try {
      await replacePlaylistItems(entry.playlist.id, { items: nextIds.map((id) => ({ id: null, trackId: id })) });
      showToast(inPlaylist ? 'Убрано из плейлиста' : 'Добавлено в плейлист');
    } catch (cause) {
      showToast(cause instanceof Error ? cause.message : 'Ошибка');
    }
  };

  return (
    <Popup title={`Трек — ${track.title}`} onClose={onClose}>
      <div style={{ padding: '18px' }}>
        <menu role="tablist" style={{ marginBottom: 0 }}>
          <button type="button" role="tab" aria-selected={tab === 'id3'} onClick={() => setTab('id3')}>
            ID3 метаданные
          </button>
          <button type="button" role="tab" aria-selected={tab === 'playlists'} onClick={() => setTab('playlists')}>
            Плейлисты
          </button>
        </menu>
        <article
          role="tabpanel"
          style={{ background: '#fff', border: '1px solid #b6cbe0', borderRadius: '0 6px 6px 6px', padding: '16px' }}
        >
          {tab === 'id3' ? (
            <>
              <div style={formGrid}>
                <label htmlFor="id3-title">Название</label>
                <input id="id3-title" value={title} onChange={(event) => setTitle(event.target.value)} />
                <label htmlFor="id3-artist">Исполнитель</label>
                <input id="id3-artist" value={artist} onChange={(event) => setArtist(event.target.value)} />
                <label htmlFor="id3-album">Альбом</label>
                <input id="id3-album" value={album} onChange={(event) => setAlbum(event.target.value)} />
              </div>
              <div style={{ marginTop: '14px' }}>
                <button type="button" className="default" onClick={() => void saveId3()} disabled={savingId3}>
                  {savingId3 ? 'Сохранение…' : 'Сохранить метаданные'}
                </button>
              </div>
            </>
          ) : (
            <>
              <p style={{ margin: '0 0 12px', fontSize: '12px', color: '#33475a' }}>
                Нажмите «＋ Добавить», чтобы поместить трек в плейлист, или «✓ В плейлисте», чтобы убрать. Динамические и
                системные плейлисты собираются автоматически.
              </p>
              {memberships === null ? <p className="admin-muted">Загрузка…</p> : null}
              {memberships !== null && memberships.length === 0 ? (
                <p style={{ color: COLORS.muted, fontSize: '12px' }}>Нет ручных плейлистов.</p>
              ) : null}
              <div style={{ display: 'flex', flexDirection: 'column', gap: '8px', maxHeight: '240px', overflowY: 'auto' }}>
                {memberships?.map((entry) => {
                  const inPlaylist = entry.trackIds.includes(track.id);
                  return (
                    <div
                      key={entry.playlist.id}
                      style={{
                        display: 'flex',
                        alignItems: 'center',
                        gap: '12px',
                        padding: '9px 12px',
                        border: `1px solid ${inPlaylist ? '#a9d6a9' : '#dbe6f2'}`,
                        borderRadius: '6px',
                        background: inPlaylist ? '#f1fbf1' : '#fff',
                      }}
                    >
                      <div style={{ flex: 1, minWidth: 0 }}>
                        <div style={{ fontWeight: 600, fontSize: '13px', color: '#1a3a52' }}>{entry.playlist.name}</div>
                        <div style={{ fontSize: '11px', color: '#789' }}>{entry.trackIds.length} тр. · ручной</div>
                      </div>
                      <button
                        type="button"
                        onClick={() => void toggleMembership(entry)}
                        style={{ minWidth: 0, padding: '5px 14px', background: inPlaylist ? 'linear-gradient(#dff5df,#bde9bd)' : undefined }}
                      >
                        {inPlaylist ? '✓ В плейлисте' : '＋ Добавить'}
                      </button>
                    </div>
                  );
                })}
              </div>
            </>
          )}
        </article>
      </div>
    </Popup>
  );
}
