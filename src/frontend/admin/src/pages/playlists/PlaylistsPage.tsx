import { useEffect, useState, type ReactElement } from 'react';

import {
  ApiError,
  createPlaylist,
  getPlaylistItems,
  getPlaylists,
  PlaylistMutationRequestSchema,
  queueTrack,
  replacePlaylist,
  replacePlaylistItems,
  type AdminTrack,
  type Playlist,
  type PlaylistItem,
  type PlaylistMutationRequest,
  type PlaylistOrder,
  type PlaylistSchedule,
  type PlaylistSource,
  type PlaylistType,
} from '@web10/shared';

import { TrackSearch } from '../../widgets/track-search/TrackSearch';

type LoadState<T> =
  | { readonly status: 'loading' }
  | { readonly status: 'error'; readonly message: string }
  | { readonly status: 'ready'; readonly data: T };

interface EditablePlaylistItem {
  readonly id: string | null;
  readonly trackId: string;
  readonly title: string;
  readonly artist: string;
  readonly position: number;
}

interface PlaylistForm {
  readonly name: string;
  readonly description: string;
  readonly isActive: boolean;
  readonly type: PlaylistType;
  readonly source: PlaylistSource;
  readonly order: PlaylistOrder;
  readonly weight: number;
  readonly isJingle: boolean;
  readonly interrupt: boolean;
  readonly avoidDuplicates: boolean;
  readonly playEverySongs: number | null;
  readonly playEveryMinutes: number | null;
  readonly playAtMinute: number | null;
  readonly schedules: readonly PlaylistSchedule[];
}

const DEFAULT_FORM: PlaylistForm = {
  name: '',
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
};

function formFromPlaylist(playlist: Playlist): PlaylistForm {
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

function actionError<TCause>(cause: TCause, fallback: string): string {
  if (cause instanceof ApiError) {
    // Keep the exact server problem code visible to operators and tests. These
    // are intentionally not collapsed into a generic HTTP status message.
    switch (cause.code) {
      case 'playlist.request_invalid':
        return `playlist.request_invalid: ${cause.message}`;
      case 'playlist.not_found':
        return `playlist.not_found: ${cause.message}`;
      case 'playlist.conflict':
        return `playlist.conflict: ${cause.message}`;
      case 'playlist.source_conflict':
        return `playlist.source_conflict: ${cause.message}`;
      default:
        break;
    }
    if (cause.status === 404) {
      return `playlist.not_found: ${cause.message}`;
    }
    if (cause.status === 409) {
      return `playlist.conflict: ${cause.message}`;
    }
  }
  return cause instanceof Error && cause.message.length > 0 ? cause.message : fallback;
}

function reindex(items: readonly EditablePlaylistItem[]): EditablePlaylistItem[] {
  return items.map((item, position) => ({ ...item, position }));
}

function editableItems(items: readonly PlaylistItem[]): EditablePlaylistItem[] {
  return items.map((item) => ({
    id: item.id,
    trackId: item.trackId,
    title: item.title,
    artist: item.artist,
    position: item.position,
  }));
}

function nullableNumber(value: string): number | null {
  if (value.trim() === '') {
    return null;
  }
  const parsed = Number(value);
  return Number.isInteger(parsed) ? parsed : null;
}

function daysFromInput(value: string): number[] {
  return value
    .split(',')
    .map((part) => part.trim())
    .filter((part) => part.length > 0)
    .map((part) => Number(part));
}

function emptySchedule(): PlaylistSchedule {
  return {
    id: null,
    daysOfWeek: [],
    startTime: '00:00',
    endTime: '23:59',
    startDate: null,
    endDate: null,
    timeZoneId: 'UTC',
  };
}

/** Policy-driven playlist editor with multiple active playlists and manual-item ordering. */
export function PlaylistsPage(): ReactElement {
  const [playlistsState, setPlaylistsState] = useState<LoadState<Playlist[]>>({ status: 'loading' });
  const [selectedPlaylistId, setSelectedPlaylistId] = useState<string | null>(null);
  const [itemsState, setItemsState] = useState<LoadState<EditablePlaylistItem[]>>({
    status: 'ready',
    data: [],
  });
  const [form, setForm] = useState<PlaylistForm>(DEFAULT_FORM);
  const [isSavingPlaylist, setIsSavingPlaylist] = useState(false);
  const [isSavingItems, setIsSavingItems] = useState(false);
  const [isQueueingTrackId, setIsQueueingTrackId] = useState<string | null>(null);
  const [itemsDirty, setItemsDirty] = useState(false);
  const [validationError, setValidationError] = useState<string | null>(null);
  const [actionMessage, setActionMessage] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    void getPlaylists({ signal: controller.signal })
      .then((playlists) => {
        if (!active) {
          return;
        }
        setPlaylistsState({ status: 'ready', data: playlists });
        setSelectedPlaylistId((current) => {
          if (current !== null && playlists.some((playlist) => playlist.id === current)) {
            return current;
          }
          return playlists[0]?.id ?? null;
        });
      })
      .catch((cause) => {
        if (active) {
          setPlaylistsState({
            status: 'error',
            message: actionError(cause, 'Playlists could not be loaded.'),
          });
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, []);

  const selectedPlaylist =
    playlistsState.status === 'ready'
      ? playlistsState.data.find((playlist) => playlist.id === selectedPlaylistId) ?? null
      : null;

  // Keep the policy form synchronized with the selected playlist. A new playlist
  // starts with DEFAULT_FORM, while edits remain local until explicitly saved.
  useEffect(() => {
    if (selectedPlaylist !== null) {
      setForm(formFromPlaylist(selectedPlaylist));
    }
  }, [selectedPlaylist]);

  useEffect(() => {
    if (selectedPlaylist === null || selectedPlaylist.source === 'allStorage') {
      setItemsDirty(false);
      setItemsState({ status: 'ready', data: [] });
      return;
    }

    setItemsDirty(false);
    setItemsState({ status: 'loading' });
    const controller = new AbortController();
    let active = true;
    void getPlaylistItems(selectedPlaylist.id, { signal: controller.signal })
      .then((items) => {
        if (active) {
          setItemsState({ status: 'ready', data: reindex(editableItems(items)) });
        }
      })
      .catch((cause) => {
        if (active) {
          setItemsState({
            status: 'error',
            message: actionError(cause, 'Playlist items could not be loaded.'),
          });
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, [selectedPlaylist]);

  const setFormField = <K extends keyof PlaylistForm>(key: K, value: PlaylistForm[K]): void => {
    setForm((current) => ({ ...current, [key]: value }));
  };

  const selectType = (type: PlaylistType): void => {
    if (type === 'general') {
      setForm((current) => ({
        ...current,
        type,
        playEverySongs: null,
        playEveryMinutes: null,
        playAtMinute: null,
      }));
    } else if (type === 'oncePerSongs') {
      setForm((current) => ({
        ...current,
        type,
        playEverySongs: current.playEverySongs ?? 1,
        playEveryMinutes: null,
        playAtMinute: null,
      }));
    } else if (type === 'oncePerMinutes') {
      setForm((current) => ({
        ...current,
        type,
        playEverySongs: null,
        playEveryMinutes: current.playEveryMinutes ?? 1,
        playAtMinute: null,
      }));
    } else {
      setForm((current) => ({
        ...current,
        type,
        playEverySongs: null,
        playEveryMinutes: null,
        playAtMinute: current.playAtMinute ?? 0,
      }));
    }
  };

  const savePlaylist = async (): Promise<void> => {
    const name = form.name.trim();
    const description = form.description.trim();
    if (name.length === 0 || name.length > 120) {
      setValidationError('playlist.request_invalid: Playlist name must contain 1 to 120 characters.');
      return;
    }
    if (description.length > 1000) {
      setValidationError('playlist.request_invalid: Playlist description must contain at most 1000 characters.');
      return;
    }

    const body: PlaylistMutationRequest = {
      name,
      description: description === '' ? null : description,
      isActive: form.isActive,
      type: form.type,
      source: form.source,
      order: form.order,
      weight: form.weight,
      isJingle: form.isJingle,
      interrupt: form.interrupt,
      avoidDuplicates: form.avoidDuplicates,
      playEverySongs: form.playEverySongs,
      playEveryMinutes: form.playEveryMinutes,
      playAtMinute: form.playAtMinute,
      schedules: [...form.schedules],
    };
    const parsed = PlaylistMutationRequestSchema.safeParse(body);
    if (!parsed.success) {
      const issue = parsed.error.issues[0];
      setValidationError(`playlist.request_invalid: ${issue?.message ?? 'Invalid playlist policy.'}`);
      return;
    }

    setValidationError(null);
    setActionMessage(null);
    setIsSavingPlaylist(true);
    try {
      if (selectedPlaylist === null) {
        const created = await createPlaylist(body);
        setPlaylistsState((current) =>
          current.status === 'ready' ? { status: 'ready', data: [...current.data, created] } : current,
        );
        setSelectedPlaylistId(created.id);
        setForm(formFromPlaylist(created));
      } else {
        const updated = await replacePlaylist(selectedPlaylist.id, body);
        setPlaylistsState((current) =>
          current.status === 'ready'
            ? {
                status: 'ready',
                data: current.data.map((playlist) => (playlist.id === updated.id ? updated : playlist)),
              }
            : current,
        );
        setForm(formFromPlaylist(updated));
      }
      setActionMessage('Saved');
    } catch (cause) {
      setValidationError(actionError(cause, 'The playlist could not be saved.'));
    } finally {
      setIsSavingPlaylist(false);
    }
  };

  const queueNow = async (track: AdminTrack): Promise<void> => {
    setValidationError(null);
    setActionMessage(null);
    setIsQueueingTrackId(track.id);
    try {
      await queueTrack({ trackId: track.id });
      setActionMessage('Queued');
    } catch (cause) {
      setValidationError(actionError(cause, 'The track could not be queued.'));
    } finally {
      setIsQueueingTrackId(null);
    }
  };

  const addTrackToPlaylist = (track: AdminTrack): void => {
    if (selectedPlaylist?.source === 'allStorage') {
      setValidationError('playlist.source_conflict: All tracks is dynamic and cannot contain manual items.');
      return;
    }
    if (itemsState.status !== 'ready') {
      return;
    }
    if (itemsState.data.some((item) => item.trackId === track.id)) {
      setValidationError('playlist.conflict: This track is already in the playlist.');
      return;
    }

    setValidationError(null);
    setItemsState({
      status: 'ready',
      data: reindex([
        ...itemsState.data,
        { id: null, trackId: track.id, title: track.title, artist: track.artist, position: itemsState.data.length },
      ]),
    });
    setItemsDirty(true);
    setActionMessage(null);
  };

  const moveItem = (index: number, direction: -1 | 1): void => {
    if (itemsState.status !== 'ready') {
      return;
    }
    const destination = index + direction;
    if (destination < 0 || destination >= itemsState.data.length) {
      return;
    }

    const reordered = [...itemsState.data];
    const current = reordered[index];
    const target = reordered[destination];
    if (current === undefined || target === undefined) {
      return;
    }
    reordered[index] = target;
    reordered[destination] = current;
    setItemsState({ status: 'ready', data: reindex(reordered) });
    setItemsDirty(true);
    setActionMessage(null);
  };

  const removeItem = (index: number): void => {
    if (itemsState.status !== 'ready') {
      return;
    }
    setItemsState({ status: 'ready', data: reindex(itemsState.data.filter((_, itemIndex) => itemIndex !== index)) });
    setItemsDirty(true);
    setActionMessage(null);
  };

  const saveItems = async (): Promise<void> => {
    if (
      selectedPlaylist === null ||
      selectedPlaylist.source === 'allStorage' ||
      itemsState.status !== 'ready' ||
      !itemsDirty
    ) {
      if (selectedPlaylist?.source === 'allStorage' && itemsDirty) {
        setValidationError('playlist.source_conflict: All tracks is dynamic and cannot contain manual items.');
      }
      return;
    }

    setValidationError(null);
    setActionMessage(null);
    setIsSavingItems(true);
    try {
      const saved = await replacePlaylistItems(selectedPlaylist.id, {
        items: itemsState.data.map((item) => ({ id: item.id, trackId: item.trackId })),
      });
      setItemsState({ status: 'ready', data: reindex(editableItems(saved)) });
      setItemsDirty(false);
      setPlaylistsState((current) =>
        current.status === 'ready'
          ? {
              status: 'ready',
              data: current.data.map((playlist) =>
                playlist.id === selectedPlaylist.id ? { ...playlist, itemCount: saved.length } : playlist,
              ),
            }
          : current,
      );
      setActionMessage('Saved playlist items');
    } catch (cause) {
      setValidationError(actionError(cause, 'Playlist items could not be saved.'));
    } finally {
      setIsSavingItems(false);
    }
  };

  const updateSchedule = (index: number, patch: Partial<PlaylistSchedule>): void => {
    setForm((current) => ({
      ...current,
      schedules: current.schedules.map((schedule, scheduleIndex) =>
        scheduleIndex === index ? { ...schedule, ...patch } : schedule,
      ),
    }));
  };

  return (
    <section>
      <h2>Playlists</h2>
      <p className="admin-muted">
        Configure policy-driven rotations, schedules, and manual tracks. Multiple playlists may be active at once.
      </p>
      {playlistsState.status === 'loading' ? <p className="admin-muted">Loading playlists…</p> : null}
      {playlistsState.status === 'error' ? <p role="alert">Failed to load playlists: {playlistsState.message}</p> : null}
      {validationError !== null ? <p role="alert">{validationError}</p> : null}
      {actionMessage !== null ? <p aria-live="polite">{actionMessage}</p> : null}

      <TrackSearch
        renderActions={(track) => (
          <>
            <button
              type="button"
              onClick={() => void queueNow(track)}
              disabled={isQueueingTrackId === track.id}
            >
              {isQueueingTrackId === track.id ? 'Queueing…' : `Queue ${track.title} now`}
            </button>
            {selectedPlaylist?.source === 'manual' ? (
              <button type="button" onClick={() => addTrackToPlaylist(track)}>
                Add {track.title} to playlist
              </button>
            ) : null}
          </>
        )}
      />

      <hr />
      {playlistsState.status === 'ready' && playlistsState.data.length === 0 ? (
        <p>No active playlist exists yet.</p>
      ) : null}
      {playlistsState.status === 'ready' && playlistsState.data.length > 0 ? (
        <ul aria-label="Loaded playlists">
          {playlistsState.data.map((playlist) => (
            <li key={playlist.id}>
              <button
                type="button"
                aria-pressed={playlist.id === selectedPlaylistId}
                onClick={() => setSelectedPlaylistId(playlist.id)}
              >
                {playlist.name}
              </button>{' '}
              {playlist.isSystem ? <strong>System</strong> : null}{' '}
              {playlist.isActive ? '(active)' : '(inactive)'} — {playlist.source === 'allStorage' ? `${playlist.itemCount} playable tracks` : `${playlist.itemCount} items`}
            </li>
          ))}
        </ul>
      ) : null}

      {playlistsState.status === 'ready' ? (
        <div style={{ display: 'grid', gap: '10px', maxWidth: '680px' }}>
          {selectedPlaylist !== null ? <h3>{selectedPlaylist.name}</h3> : <h3>New playlist</h3>}
          <div className="group">
            <label htmlFor="playlist-name">Playlist name</label>
            <input
              id="playlist-name"
              value={form.name}
              onChange={(event) => setFormField('name', event.target.value)}
              maxLength={120}
              disabled={isSavingPlaylist}
            />
          </div>
          <div className="group">
            <label htmlFor="playlist-description">Playlist description</label>
            <textarea
              id="playlist-description"
              value={form.description}
              onChange={(event) => setFormField('description', event.target.value)}
              maxLength={1000}
              disabled={isSavingPlaylist}
            />
          </div>
          <div>
            <input
              id="playlist-active"
              type="checkbox"
              checked={form.isActive}
              onChange={(event) => setFormField('isActive', event.target.checked)}
              disabled={isSavingPlaylist}
            />
            <label htmlFor="playlist-active">Active playlist</label>
          </div>
          <div className="group">
            <label htmlFor="playlist-type">Playlist type</label>
            <select id="playlist-type" value={form.type} onChange={(event) => selectType(event.target.value as PlaylistType)} disabled={isSavingPlaylist || selectedPlaylist?.isSystem === true}>
              <option value="general">General</option>
              <option value="oncePerSongs">Once per songs</option>
              <option value="oncePerMinutes">Once per minutes</option>
              <option value="oncePerHour">Once per hour</option>
            </select>
          </div>
          <div className="group">
            <label htmlFor="playlist-source">Playlist source</label>
            <select
              id="playlist-source"
              value={form.source}
              onChange={(event) => setFormField('source', event.target.value as PlaylistSource)}
              disabled={isSavingPlaylist || selectedPlaylist?.isSystem === true}
            >
              <option value="manual">Manual</option>
              <option value="allStorage">All tracks (storage)</option>
            </select>
          </div>
          <div className="group">
            <label htmlFor="playlist-order">Playlist order</label>
            <select id="playlist-order" value={form.order} onChange={(event) => setFormField('order', event.target.value as PlaylistOrder)} disabled={isSavingPlaylist}>
              <option value="sequential">Sequential</option>
              <option value="shuffle">Shuffle</option>
              <option value="random">Random</option>
            </select>
          </div>
          <div className="group">
            <label htmlFor="playlist-weight">Playlist weight (1–25)</label>
            <input
              id="playlist-weight"
              type="number"
              min={1}
              max={25}
              step={1}
              value={form.weight}
              onChange={(event) => setFormField('weight', Number(event.target.value))}
              disabled={isSavingPlaylist}
            />
          </div>
          <fieldset>
            <legend>Playback policy</legend>
            <label>
              <input type="checkbox" checked={form.isJingle} onChange={(event) => setFormField('isJingle', event.target.checked)} disabled={isSavingPlaylist} />
              Jingle
            </label>{' '}
            <label>
              <input type="checkbox" checked={form.interrupt} onChange={(event) => setFormField('interrupt', event.target.checked)} disabled={isSavingPlaylist} />
              Interrupt current track
            </label>{' '}
            <label>
              <input type="checkbox" checked={form.avoidDuplicates} onChange={(event) => setFormField('avoidDuplicates', event.target.checked)} disabled={isSavingPlaylist} />
              Avoid duplicates
            </label>
          </fieldset>
          {form.type === 'oncePerSongs' ? (
            <div className="group">
              <label htmlFor="playlist-play-every-songs">Play every songs</label>
              <input id="playlist-play-every-songs" type="number" min={1} max={1000} value={form.playEverySongs ?? ''} onChange={(event) => setFormField('playEverySongs', nullableNumber(event.target.value))} disabled={isSavingPlaylist} />
            </div>
          ) : null}
          {form.type === 'oncePerMinutes' ? (
            <div className="group">
              <label htmlFor="playlist-play-every-minutes">Play every minutes</label>
              <input id="playlist-play-every-minutes" type="number" min={1} max={10080} value={form.playEveryMinutes ?? ''} onChange={(event) => setFormField('playEveryMinutes', nullableNumber(event.target.value))} disabled={isSavingPlaylist} />
            </div>
          ) : null}
          {form.type === 'oncePerHour' ? (
            <div className="group">
              <label htmlFor="playlist-play-at-minute">Play at minute</label>
              <input id="playlist-play-at-minute" type="number" min={0} max={59} value={form.playAtMinute ?? ''} onChange={(event) => setFormField('playAtMinute', nullableNumber(event.target.value))} disabled={isSavingPlaylist} />
            </div>
          ) : null}

          <fieldset aria-label="Playlist schedules">
            <legend>Schedules (optional)</legend>
            {form.schedules.map((schedule, index) => (
              <div key={schedule.id ?? `new-schedule-${index}`} style={{ display: 'grid', gap: '4px', border: '1px solid currentColor', padding: '8px' }}>
                <label>
                  Days of week (1–7, blank for every day)
                  <input
                    value={schedule.daysOfWeek.join(',')}
                    onChange={(event) => updateSchedule(index, { daysOfWeek: daysFromInput(event.target.value) })}
                  />
                </label>
                <label>
                  Start time
                  <input type="time" value={schedule.startTime} onChange={(event) => updateSchedule(index, { startTime: event.target.value })} />
                </label>
                <label>
                  End time
                  <input type="time" value={schedule.endTime} onChange={(event) => updateSchedule(index, { endTime: event.target.value })} />
                </label>
                <label>
                  Start date
                  <input type="date" value={schedule.startDate ?? ''} onChange={(event) => updateSchedule(index, { startDate: event.target.value === '' ? null : event.target.value })} />
                </label>
                <label>
                  End date
                  <input type="date" value={schedule.endDate ?? ''} onChange={(event) => updateSchedule(index, { endDate: event.target.value === '' ? null : event.target.value })} />
                </label>
                <label>
                  Time zone
                  <input value={schedule.timeZoneId} onChange={(event) => updateSchedule(index, { timeZoneId: event.target.value })} />
                </label>
                <button type="button" onClick={() => setForm((current) => ({ ...current, schedules: current.schedules.filter((_, scheduleIndex) => scheduleIndex !== index) }))}>
                  Remove schedule {index + 1}
                </button>
              </div>
            ))}
            <button type="button" onClick={() => setForm((current) => ({ ...current, schedules: [...current.schedules, emptySchedule()] }))} disabled={form.schedules.length >= 32 || isSavingPlaylist}>
              Add schedule
            </button>
          </fieldset>

          <div>
            <button type="button" className="default" onClick={() => void savePlaylist()} disabled={isSavingPlaylist}>
              {isSavingPlaylist ? 'Saving…' : selectedPlaylist === null ? 'Create playlist' : 'Save playlist'}
            </button>
          </div>
        </div>
      ) : null}

      {selectedPlaylist?.source === 'allStorage' ? (
        <div style={{ marginTop: '18px' }}>
          <h3>All tracks</h3>
          <p>This system playlist is populated dynamically from playable cached storage tracks.</p>
          <p>Playable tracks: {selectedPlaylist.itemCount}</p>
          <p className="admin-muted">Manual item controls are unavailable for allStorage playlists.</p>
        </div>
      ) : null}

      {selectedPlaylist?.source === 'manual' ? (
        <div style={{ marginTop: '18px' }}>
          <h3>Playlist order</h3>
          {itemsState.status === 'loading' ? <p className="admin-muted">Loading playlist items…</p> : null}
          {itemsState.status === 'error' ? <p role="alert">Failed to load playlist items: {itemsState.message}</p> : null}
          {itemsState.status === 'ready' && itemsState.data.length === 0 ? <p>No tracks in this playlist yet.</p> : null}
          {itemsState.status === 'ready' && itemsState.data.length > 0 ? (
            <ol>
              {itemsState.data.map((item, index) => (
                <li key={item.id ?? `${item.trackId}-${item.position}`} style={{ marginBottom: '8px' }}>
                  <strong>{item.title}</strong> — {item.artist || 'Unknown artist'}
                  <span style={{ display: 'inline-flex', gap: '6px', marginLeft: '8px' }}>
                    <button type="button" onClick={() => moveItem(index, -1)} disabled={index === 0 || isSavingItems}>
                      Move {item.title} up
                    </button>
                    <button type="button" onClick={() => moveItem(index, 1)} disabled={index === itemsState.data.length - 1 || isSavingItems}>
                      Move {item.title} down
                    </button>
                    <button type="button" onClick={() => removeItem(index)} disabled={isSavingItems}>
                      Remove {item.title}
                    </button>
                  </span>
                </li>
              ))}
            </ol>
          ) : null}
          <button type="button" className="default" onClick={() => void saveItems()} disabled={itemsState.status !== 'ready' || !itemsDirty || isSavingItems}>
            {isSavingItems ? 'Saving playlist items…' : 'Save playlist items'}
          </button>
        </div>
      ) : null}
    </section>
  );
}
