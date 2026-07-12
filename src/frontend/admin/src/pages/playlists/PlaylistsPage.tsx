import { useEffect, useRef, useState, type ReactElement } from 'react';

import {
  createPlaylist,
  getPlaylistItems,
  getPlaylists,
  replacePlaylist,
  replacePlaylistItems,
  type Playlist,
  type PlaylistItem,
  type PlaylistMutationRequest,
  type PlaylistOrder,
  type PlaylistSchedule,
  type PlaylistSource,
  type PlaylistType,
} from '@web10/shared';

import { errorMessage } from '../../shared/lib/errorMessage';
import { useToast } from '../../shared/ui/toast';
import { COLORS, ellipsis, formGrid, iconButton, panel } from '../../shared/ui/tokens';

interface PlaylistForm {
  name: string;
  description: string;
  isActive: boolean;
  type: PlaylistType;
  source: PlaylistSource;
  order: PlaylistOrder;
  weight: number;
  isJingle: boolean;
  interrupt: boolean;
  avoidDuplicates: boolean;
  playEverySongs: number | null;
  playEveryMinutes: number | null;
  playAtMinute: number | null;
  schedules: readonly PlaylistSchedule[];
}

function formFrom(playlist: Playlist): PlaylistForm {
  return {
    name: playlist.name,
    description: playlist.description ?? '',
    isActive: playlist.isActive,
    type: playlist.type,
    source: playlist.source,
    order: playlist.order,
    weight: playlist.weight,
    isJingle: playlist.isJingle,
    interrupt: playlist.interrupt,
    avoidDuplicates: playlist.avoidDuplicates,
    playEverySongs: playlist.playEverySongs,
    playEveryMinutes: playlist.playEveryMinutes,
    playAtMinute: playlist.playAtMinute,
    schedules: playlist.schedules,
  };
}

/** Плейлисты: список + редактор политики и ручного порядка треков. */
export function PlaylistsPage(): ReactElement {
  const { showToast } = useToast();
  const [playlists, setPlaylists] = useState<Playlist[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [form, setForm] = useState<PlaylistForm | null>(null);
  const [items, setItems] = useState<PlaylistItem[]>([]);
  const [saving, setSaving] = useState(false);
  const [reloadKey, setReloadKey] = useState(0);
  const dragIndex = useRef<number | null>(null);

  useEffect(() => {
    void getPlaylists()
      .then((loaded) => {
        setPlaylists(loaded);
        setSelectedId((current) => (current !== null && loaded.some((p) => p.id === current) ? current : loaded[0]?.id ?? null));
      })
      .catch((cause) => showToast(errorMessage(cause, 'Не удалось загрузить плейлисты')));
  }, [reloadKey, showToast]);

  const selected = playlists.find((p) => p.id === selectedId) ?? null;

  useEffect(() => {
    // `selected === null` is the "new playlist" mode — keep the draft set by newPlaylist().
    if (selected === null) {
      return;
    }
    setForm(formFrom(selected));
    if (selected.source === 'manual') {
      void getPlaylistItems(selected.id).then(setItems).catch(() => setItems([]));
    } else {
      setItems([]);
    }
  }, [selected]);

  const setField = <K extends keyof PlaylistForm>(key: K, value: PlaylistForm[K]): void => {
    setForm((current) => (current === null ? current : { ...current, [key]: value }));
  };

  const newPlaylist = (): void => {
    setSelectedId(null);
    setForm({
      name: 'Новый плейлист',
      description: '',
      isActive: false,
      type: 'general',
      source: 'manual',
      order: 'sequential',
      weight: 3,
      isJingle: false,
      interrupt: false,
      avoidDuplicates: true,
      playEverySongs: null,
      playEveryMinutes: null,
      playAtMinute: null,
      schedules: [],
    });
    setItems([]);
  };

  const save = async (): Promise<void> => {
    if (form === null) {
      return;
    }
    const name = form.name.trim();
    if (name.length < 1 || name.length > 120) {
      showToast('Название: 1–120 символов');
      return;
    }
    const body: PlaylistMutationRequest = {
      name,
      description: form.description.trim() === '' ? null : form.description.trim(),
      isActive: form.isActive,
      type: form.type,
      source: form.source,
      order: form.order,
      weight: form.weight,
      isJingle: form.isJingle,
      interrupt: form.interrupt,
      avoidDuplicates: form.avoidDuplicates,
      playEverySongs: form.type === 'oncePerSongs' ? form.playEverySongs ?? 1 : null,
      playEveryMinutes: form.type === 'oncePerMinutes' ? form.playEveryMinutes ?? 1 : null,
      playAtMinute: form.type === 'oncePerHour' ? form.playAtMinute ?? 0 : null,
      schedules: [...form.schedules],
    };
    setSaving(true);
    try {
      const saved = selected === null ? await createPlaylist(body) : await replacePlaylist(selected.id, body);
      if (selected !== null && selected.source === 'manual') {
        await replacePlaylistItems(selected.id, { items: items.map((item) => ({ id: item.id, trackId: item.trackId })) });
      }
      showToast('Плейлист сохранён');
      setSelectedId(saved.id);
      setReloadKey((key) => key + 1);
    } catch (cause) {
      showToast(cause instanceof Error ? cause.message : 'Не удалось сохранить');
    } finally {
      setSaving(false);
    }
  };

  const onDrop = (index: number): void => {
    const from = dragIndex.current;
    dragIndex.current = null;
    if (from === null || from === index) {
      return;
    }
    setItems((current) => {
      const next = [...current];
      const [moved] = next.splice(from, 1);
      if (moved === undefined) {
        return current;
      }
      next.splice(index, 0, moved);
      return next;
    });
  };

  return (
    <div style={{ display: 'flex', gap: '16px', flexWrap: 'wrap', alignItems: 'flex-start' }}>
      <div style={{ flex: 'none', width: '230px' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '6px' }}>
          <strong>Плейлисты</strong>
          <button type="button" onClick={newPlaylist} style={{ minWidth: 0, padding: '2px 10px' }}>
            ＋ Новый
          </button>
        </div>
        <ul className="tree-view has-container">
          {playlists.map((playlist) => (
            <li
              key={playlist.id}
              onClick={() => setSelectedId(playlist.id)}
              style={{ cursor: 'pointer', padding: '6px', borderRadius: '4px', background: playlist.id === selectedId ? COLORS.selection : 'transparent' }}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <span style={{ fontWeight: 600 }}>{playlist.name}</span>
                <span style={{ fontSize: '10px', color: playlist.isActive ? COLORS.live : '#999' }}>
                  {playlist.isSystem ? 'система' : playlist.isActive ? 'активен' : 'выкл'}
                </span>
              </div>
              <div style={{ fontSize: '11px', color: '#89a' }}>
                {playlist.source === 'allStorage' ? 'все треки' : `${playlist.itemCount} тр.`}
              </div>
            </li>
          ))}
        </ul>
      </div>

      {form !== null ? (
        <div style={{ ...panel, flex: 1, minWidth: '320px' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', marginBottom: '10px' }}>
            <strong style={{ fontSize: '15px' }}>{selected?.name ?? 'Новый плейлист'}</strong>
            {selected?.isSystem === true ? <span style={{ fontSize: '11px', color: '#b8860b' }}>системный</span> : null}
          </div>
          <div style={formGrid}>
            <label htmlFor="pl-name">Название</label>
            <input id="pl-name" value={form.name} maxLength={120} onChange={(event) => setField('name', event.target.value)} />
            <label htmlFor="pl-desc">Описание</label>
            <textarea id="pl-desc" rows={2} value={form.description} onChange={(event) => setField('description', event.target.value)} />
            <label htmlFor="pl-active">Активен</label>
            <div>
              <input id="pl-active" type="checkbox" checked={form.isActive} onChange={(event) => setField('isActive', event.target.checked)} />{' '}
              <span style={{ fontSize: '12px', color: COLORS.subtle }}>в ротации эфира</span>
            </div>
            <label htmlFor="pl-source">Источник</label>
            <select id="pl-source" value={form.source} disabled={selected?.isSystem === true} onChange={(event) => setField('source', event.target.value === 'allStorage' ? 'allStorage' : 'manual')}>
              <option value="manual">Ручной список</option>
              <option value="allStorage">Все треки хранилища</option>
            </select>
            <label htmlFor="pl-type">Тип</label>
            <select id="pl-type" value={form.type} onChange={(event) => setField('type', event.target.value as PlaylistType)}>
              <option value="general">Обычный</option>
              <option value="oncePerSongs">Раз в N треков</option>
              <option value="oncePerMinutes">Раз в N минут</option>
              <option value="oncePerHour">Раз в час</option>
            </select>
            {form.type === 'oncePerSongs' ? (
              <>
                <label htmlFor="pl-every-songs">Каждые N треков</label>
                <input id="pl-every-songs" type="number" min={1} value={form.playEverySongs ?? 1} onChange={(event) => setField('playEverySongs', Number(event.target.value))} />
              </>
            ) : null}
            {form.type === 'oncePerMinutes' ? (
              <>
                <label htmlFor="pl-every-min">Каждые N минут</label>
                <input id="pl-every-min" type="number" min={1} value={form.playEveryMinutes ?? 1} onChange={(event) => setField('playEveryMinutes', Number(event.target.value))} />
              </>
            ) : null}
            {form.type === 'oncePerHour' ? (
              <>
                <label htmlFor="pl-at-min">Минута часа</label>
                <input id="pl-at-min" type="number" min={0} max={59} value={form.playAtMinute ?? 0} onChange={(event) => setField('playAtMinute', Number(event.target.value))} />
              </>
            ) : null}
            <label htmlFor="pl-order">Порядок</label>
            <select id="pl-order" value={form.order} onChange={(event) => setField('order', event.target.value as PlaylistOrder)}>
              <option value="sequential">Последовательно</option>
              <option value="shuffle">Перемешать</option>
              <option value="random">Случайно</option>
            </select>
            <label htmlFor="pl-weight">Вес (1–25)</label>
            <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
              <input id="pl-weight" type="range" min={1} max={25} value={form.weight} onChange={(event) => setField('weight', Number(event.target.value))} style={{ flex: 1 }} />
              <span style={{ width: '22px', textAlign: 'right' }}>{form.weight}</span>
            </div>
          </div>
          <fieldset style={{ marginTop: '12px' }}>
            <legend>Политика воспроизведения</legend>
            <label style={{ marginRight: '14px' }}>
              <input type="checkbox" checked={form.isJingle} onChange={(event) => setField('isJingle', event.target.checked)} /> Джингл
            </label>
            <label style={{ marginRight: '14px' }}>
              <input type="checkbox" checked={form.interrupt} onChange={(event) => setField('interrupt', event.target.checked)} /> Прерывать трек
            </label>
            <label>
              <input type="checkbox" checked={form.avoidDuplicates} onChange={(event) => setField('avoidDuplicates', event.target.checked)} /> Избегать повторов
            </label>
          </fieldset>

          <div style={{ marginTop: '12px' }}>
            <strong style={{ fontSize: '13px' }}>Порядок треков</strong>
            {form.source === 'allStorage' ? (
              <p style={{ color: COLORS.subtle, fontSize: '12px' }}>
                Динамический плейлист: треки берутся из хранилища автоматически, ручной порядок недоступен.
              </p>
            ) : items.length === 0 ? (
              <p style={{ color: '#89a', fontSize: '12px' }}>Список пуст. Добавляйте треки из Библиотеки (⋯ → Плейлисты).</p>
            ) : (
              <>
                <p style={{ fontSize: '11px', color: '#89a', margin: '4px 0 2px' }}>Перетащите строки, чтобы изменить порядок.</p>
                <ol className="tree-view has-container" style={{ marginTop: '2px' }}>
                  {items.map((item, index) => (
                    <li
                      key={item.id}
                      draggable
                      onDragStart={() => {
                        dragIndex.current = index;
                      }}
                      onDragOver={(event) => event.preventDefault()}
                      onDrop={() => onDrop(index)}
                      style={{ display: 'flex', alignItems: 'center', gap: '8px', padding: '5px 6px', cursor: 'grab' }}
                    >
                      <span style={{ color: '#9ab', fontSize: '12px' }}>⋮⋮</span>
                      <span style={{ flex: 1, ...ellipsis }}>
                        <strong>{item.title}</strong> <span style={{ color: '#789', fontSize: '12px' }}>— {item.artist}</span>
                      </span>
                      <button
                        type="button"
                        title="Убрать"
                        style={iconButton}
                        onClick={() => setItems((current) => current.filter((_, i) => i !== index))}
                      >
                        ✕
                      </button>
                    </li>
                  ))}
                </ol>
              </>
            )}
          </div>

          <div style={{ marginTop: '14px' }}>
            <button type="button" className="default" onClick={() => void save()} disabled={saving}>
              {saving ? 'Сохранение…' : 'Сохранить плейлист'}
            </button>
          </div>
        </div>
      ) : null}
    </div>
  );
}
