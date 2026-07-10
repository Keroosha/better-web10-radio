import { useEffect, useState, type FormEvent, type ReactElement } from 'react';

import {
  ApiError,
  getPlaylistItems,
  getPlaylists,
  getTracks,
  queueTrack,
  createPlaylist,
  replacePlaylist,
  replacePlaylistItems,
  type AdminTrack,
  type Playlist,
  type PlaylistItem,
} from '@web10/shared';

type LoadState<T> =
  | { readonly status: 'loading' }
  | { readonly status: 'error'; readonly message: string }
  | { readonly status: 'ready'; readonly data: T };

type TrackState = { readonly status: 'idle' } | LoadState<AdminTrack[]>;

interface EditablePlaylistItem {
  readonly id: string | null;
  readonly trackId: string;
  readonly title: string;
  readonly artist: string;
  readonly position: number;
}

function actionError(cause: Error): string {
  if (cause instanceof ApiError) {
    if (cause.status === 404) {
      return 'The requested track or playlist no longer exists. Reload the page and try again.';
    }
    if (cause.status === 409) {
      return 'This change conflicts with the current playlist. Reload the page and try again.';
    }
  }
  return cause.message || 'The request could not be completed.';
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

/** Manages the single active playlist, its order, and immediate non-payment track queueing. */
export function PlaylistsPage(): ReactElement {
  const [playlistsState, setPlaylistsState] = useState<LoadState<Playlist[]>>({ status: 'loading' });
  const [activePlaylistId, setActivePlaylistId] = useState<string | null>(null);
  const [itemsState, setItemsState] = useState<LoadState<EditablePlaylistItem[]>>({
    status: 'ready',
    data: [],
  });
  const [tracksState, setTracksState] = useState<TrackState>({ status: 'idle' });
  const [searchQuery, setSearchQuery] = useState('');
  const [playlistName, setPlaylistName] = useState('');
  const [playlistDescription, setPlaylistDescription] = useState('');
  const [playlistIsActive, setPlaylistIsActive] = useState(false);
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
        const currentActive = playlists.find((playlist) => playlist.isActive);
        setActivePlaylistId(currentActive?.id ?? null);
        if (currentActive !== undefined) {
          setPlaylistName(currentActive.name);
          setPlaylistDescription(currentActive.description ?? '');
          setPlaylistIsActive(currentActive.isActive);
        }
      })
      .catch((cause) => {
        if (active) {
          setPlaylistsState({
            status: 'error',
            message: cause instanceof Error ? actionError(cause) : 'Playlists could not be loaded.',
          });
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, []);

  const activePlaylist =
    playlistsState.status === 'ready'
      ? playlistsState.data.find((playlist) => playlist.id === activePlaylistId) ?? null
      : null;

  useEffect(() => {
    if (activePlaylist === null) {
      setItemsState({ status: 'ready', data: [] });
      return;
    }

    setItemsDirty(false);
    setItemsState({ status: 'loading' });

    const controller = new AbortController();
    let active = true;
    void getPlaylistItems(activePlaylist.id, { signal: controller.signal })
      .then((items) => {
        if (active) {
          setItemsState({ status: 'ready', data: reindex(editableItems(items)) });
        }
      })
      .catch((cause) => {
        if (active) {
          setItemsState({
            status: 'error',
            message: cause instanceof Error ? actionError(cause) : 'Playlist items could not be loaded.',
          });
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, [activePlaylistId]);

  const searchTracks = async (event: FormEvent<HTMLFormElement>): Promise<void> => {
    event.preventDefault();
    const query = searchQuery.trim();
    if (query.length > 200) {
      setValidationError('Track search must be at most 200 characters.');
      return;
    }

    setValidationError(null);
    setActionMessage(null);
    setTracksState({ status: 'loading' });
    try {
      const tracks = await getTracks({ query, limit: 100 });
      setTracksState({ status: 'ready', data: tracks });
    } catch (cause) {
      setTracksState({
        status: 'error',
        message: cause instanceof Error ? actionError(cause) : 'Tracks could not be loaded.',
      });
    }
  };

  const savePlaylist = async (): Promise<void> => {
    const name = playlistName.trim();
    const description = playlistDescription.trim();
    if (name.length === 0 || name.length > 120) {
      setValidationError('Playlist name must contain 1 to 120 characters.');
      return;
    }
    if (description.length > 1000) {
      setValidationError('Playlist description must contain at most 1000 characters.');
      return;
    }

    setValidationError(null);
    setActionMessage(null);
    setIsSavingPlaylist(true);
    const body = { name, description: description === '' ? null : description, isActive: playlistIsActive };

    try {
      if (activePlaylist === null) {
        const created = await createPlaylist(body);
        setPlaylistsState({ status: 'ready', data: [created] });
        setActivePlaylistId(created.id);
        setPlaylistName(created.name);
        setPlaylistDescription(created.description ?? '');
        setPlaylistIsActive(created.isActive);
      } else {
        const updated = await replacePlaylist(activePlaylist.id, body);
        if (playlistsState.status === 'ready') {
          setPlaylistsState({
            status: 'ready',
            data: playlistsState.data.map((playlist) => (playlist.id === updated.id ? updated : playlist)),
          });
        }
      }
      setActionMessage('Saved');
    } catch (cause) {
      setValidationError(cause instanceof Error ? actionError(cause) : 'The playlist could not be saved.');
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
      setValidationError(cause instanceof Error ? actionError(cause) : 'The track could not be queued.');
    } finally {
      setIsQueueingTrackId(null);
    }
  };

  const addTrackToPlaylist = (track: AdminTrack): void => {
    if (itemsState.status !== 'ready') {
      return;
    }
    if (itemsState.data.some((item) => item.trackId === track.id)) {
      setValidationError('This track is already in the playlist.');
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
    if (activePlaylist === null || itemsState.status !== 'ready' || !itemsDirty) {
      return;
    }

    setValidationError(null);
    setActionMessage(null);
    setIsSavingItems(true);
    try {
      const saved = await replacePlaylistItems(activePlaylist.id, {
        items: itemsState.data.map((item) => ({ id: item.id, trackId: item.trackId })),
      });
      setItemsState({ status: 'ready', data: reindex(editableItems(saved)) });
      setItemsDirty(false);
      if (playlistsState.status === 'ready') {
        setPlaylistsState({
          status: 'ready',
          data: playlistsState.data.map((playlist) =>
            playlist.id === activePlaylist.id ? { ...playlist, itemCount: saved.length } : playlist,
          ),
        });
      }
      setActionMessage('Saved playlist items');
    } catch (cause) {
      setValidationError(cause instanceof Error ? actionError(cause) : 'Playlist items could not be saved.');
    } finally {
      setIsSavingItems(false);
    }
  };

  return (
    <section>
      <h2 style={{ fontSize: '16px' }}>Playlists</h2>
      <p style={{ fontSize: '12px', opacity: 0.7 }}>
        Build the active rotation from scanned tracks, or queue a track immediately.
      </p>
      {playlistsState.status === 'ready' && activePlaylist !== null && itemsState.status === 'ready' ? (
        <ul aria-label="Loaded playlists">
          {playlistsState.data.map((playlist) => (
            <li key={playlist.id}>
              {playlist.name}{playlist.isActive ? ' (active)' : ''} — {playlist.itemCount} items
            </li>
          ))}
        </ul>
      ) : null}


      {validationError !== null ? <p role="alert">{validationError}</p> : null}
      {actionMessage !== null ? <p aria-live="polite">{actionMessage}</p> : null}

      <form onSubmit={(event) => void searchTracks(event)} style={{ display: 'flex', gap: '8px', alignItems: 'end' }}>
        <label htmlFor="track-search" style={{ display: 'grid', gap: '4px' }}>
          Track search
          <input
            id="track-search"
            value={searchQuery}
            onChange={(event) => setSearchQuery(event.target.value)}
            maxLength={201}
          />
        </label>
        <button type="submit" disabled={tracksState.status === 'loading'}>
          {tracksState.status === 'loading' ? 'Searching…' : 'Search tracks'}
        </button>
      </form>

      {tracksState.status === 'idle' ? <p style={{ opacity: 0.7 }}>Search the scanned library to add or queue a track.</p> : null}
      {tracksState.status === 'error' ? <p role="alert">Failed to load tracks: {tracksState.message}</p> : null}
      {tracksState.status === 'ready' && tracksState.data.length === 0 ? <p>No tracks matched this search.</p> : null}
      {tracksState.status === 'ready' && tracksState.data.length > 0 ? (
        <ul aria-label="Scanned tracks">
          {tracksState.data.map((track) => (
            <li key={track.id} style={{ marginBottom: '8px' }}>
              <strong>{track.title}</strong> — {track.artist || 'Unknown artist'}
              <div style={{ display: 'inline-flex', gap: '6px', marginLeft: '8px' }}>
                <button
                  type="button"
                  onClick={() => void queueNow(track)}
                  disabled={isQueueingTrackId === track.id}
                >
                  {isQueueingTrackId === track.id ? 'Queueing…' : `Queue ${track.title} now`}
                </button>
                {activePlaylist !== null && itemsState.status === 'ready' ? (
                  <button type="button" onClick={() => addTrackToPlaylist(track)}>
                    Add {track.title} to playlist
                  </button>
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      ) : null}

      <hr />
      {playlistsState.status === 'loading' ? <p style={{ opacity: 0.7 }}>Loading playlists…</p> : null}
      {playlistsState.status === 'error' ? <p role="alert">Failed to load playlists: {playlistsState.message}</p> : null}
      {playlistsState.status === 'ready' && playlistsState.data.length === 0 ? <p>No active playlist exists yet.</p> : null}
      {playlistsState.status === 'ready' && playlistsState.data.length > 1 ? (
        <p style={{ opacity: 0.7 }}>{playlistsState.data.length} playlists loaded; edit the active playlist below.</p>
      ) : null}

      {playlistsState.status === 'ready' ? (
        <div style={{ display: 'grid', gap: '10px', maxWidth: '560px' }}>
          {activePlaylist !== null && itemsState.status === 'ready' ? <h3>{activePlaylist.name}</h3> : null}
          <label htmlFor="playlist-name">
            Playlist name
            <input
              id="playlist-name"
              value={playlistName}
              onChange={(event) => setPlaylistName(event.target.value)}
              maxLength={121}
              disabled={isSavingPlaylist}
            />
          </label>
          <label htmlFor="playlist-description">
            Playlist description
            <textarea
              id="playlist-description"
              value={playlistDescription}
              onChange={(event) => setPlaylistDescription(event.target.value)}
              maxLength={1001}
              disabled={isSavingPlaylist}
            />
          </label>
          <label>
            <input
              type="checkbox"
              checked={playlistIsActive}
              onChange={(event) => setPlaylistIsActive(event.target.checked)}
              disabled={isSavingPlaylist}
            />{' '}
            Active playlist
          </label>
          <div>
            <button type="button" onClick={() => void savePlaylist()} disabled={isSavingPlaylist}>
              {isSavingPlaylist ? 'Saving…' : activePlaylist === null ? 'Create playlist' : 'Save playlist'}
            </button>
          </div>
        </div>
      ) : null}

      {activePlaylist !== null ? (
        <div style={{ marginTop: '18px' }}>
          <h3>Playlist order</h3>
          {itemsState.status === 'loading' ? <p style={{ opacity: 0.7 }}>Loading playlist items…</p> : null}
          {itemsState.status === 'error' ? <p role="alert">Failed to load playlist items: {itemsState.message}</p> : null}
          {itemsState.status === 'ready' && itemsState.data.length === 0 ? <p>No tracks in this playlist yet.</p> : null}
          {itemsState.status === 'ready' && itemsState.data.length > 0 ? (
            <ol>
              {itemsState.data.map((item, index) => (
                <li key={item.id ?? `${item.trackId}-${item.position}`} style={{ marginBottom: '8px' }}>
                  <strong>{item.title}</strong> — {item.artist || 'Unknown artist'}
                  <span style={{ display: 'inline-flex', gap: '6px', marginLeft: '8px' }}>
                    <button
                      type="button"
                      onClick={() => moveItem(index, -1)}
                      disabled={index === 0 || isSavingItems}
                    >
                      Move {item.title} up
                    </button>
                    <button
                      type="button"
                      onClick={() => moveItem(index, 1)}
                      disabled={index === itemsState.data.length - 1 || isSavingItems}
                    >
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
          <button
            type="button"
            onClick={() => void saveItems()}
            disabled={itemsState.status !== 'ready' || !itemsDirty || isSavingItems}
          >
            {isSavingItems ? 'Saving playlist items…' : 'Save playlist items'}
          </button>
        </div>
      ) : null}
    </section>
  );
}
