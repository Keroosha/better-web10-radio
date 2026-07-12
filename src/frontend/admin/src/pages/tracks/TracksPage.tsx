import { useState, type ReactElement } from 'react';

import { removeTrackCover, replaceTrackCover, updateTrackMetadata, type AdminTrack } from '@web10/shared';

import { useApiMutation } from '../../shared/lib/useApiMutation';
import { TrackSearch } from '../../widgets/track-search/TrackSearch';

interface MetadataDraft {
  readonly title: string;
  readonly artist: string;
  readonly album: string;
}

const MAX_COVER_BYTES = 10 * 1024 * 1024;
const COVER_TYPES = new Set(['image/jpeg', 'image/png', 'image/webp']);

function toDraft(track: AdminTrack): MetadataDraft {
  return { title: track.title, artist: track.artist, album: track.album };
}

function errorMessage(cause: Error): string {
  return cause.message || 'The request could not be completed.';
}

/** Search and edit canonical track metadata and the managed cover asset. */
export function TracksPage(): ReactElement {
  const [selectedTrack, setSelectedTrack] = useState<AdminTrack | null>(null);
  const [draft, setDraft] = useState<MetadataDraft | null>(null);
  const [coverFile, setCoverFile] = useState<File | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const mutation = useApiMutation();

  const selectTrack = (track: AdminTrack): void => {
    setSelectedTrack(track);
    setDraft(toDraft(track));
    setCoverFile(null);
    setMessage(null);
    mutation.reset();
  };

  const saveMetadata = (): void => {
    if (selectedTrack === null || draft === null) {
      return;
    }
    const title = draft.title.trim();
    const artist = draft.artist.trim();
    const album = draft.album.trim();
    if (title.length === 0 || title.length > 200 || artist.length === 0 || artist.length > 200) {
      setMessage('Title and artist must each contain 1 to 200 non-whitespace characters.');
      return;
    }
    if (album.length > 200) {
      setMessage('Album must contain at most 200 characters.');
      return;
    }

    setMessage(null);
    mutation.run(
      () =>
        updateTrackMetadata(selectedTrack.id, {
          title,
          artist,
          album: album.length === 0 ? null : album,
        }).then((updated) => {
          setSelectedTrack(updated);
          setDraft(toDraft(updated));
          setMessage('Metadata saved.');
        }),
    );
  };

  const uploadCover = (): void => {
    if (selectedTrack === null || coverFile === null) {
      return;
    }
    if (!COVER_TYPES.has(coverFile.type) || coverFile.size === 0 || coverFile.size > MAX_COVER_BYTES) {
      setMessage('Cover must be a non-empty JPEG, PNG, or WebP image no larger than 10 MiB.');
      return;
    }

    setMessage(null);
    mutation.run(
      () =>
        replaceTrackCover(selectedTrack.id, coverFile).then((updated) => {
          setSelectedTrack(updated);
          setDraft(toDraft(updated));
          setCoverFile(null);
          setMessage('Cover uploaded.');
        }),
    );
  };

  const clearCover = (): void => {
    if (selectedTrack === null) {
      return;
    }
    setMessage(null);
    mutation.run(
      () =>
        removeTrackCover(selectedTrack.id).then((updated) => {
          setSelectedTrack(updated);
          setDraft(toDraft(updated));
          setCoverFile(null);
          setMessage('Cover cleared.');
        }),
    );
  };

  return (
    <section>
      <h2>Tracks</h2>
      <p className="admin-muted">Search the scanned library, edit canonical metadata, and manage cover artwork.</p>

      {message !== null ? <p aria-live="polite">{message}</p> : null}
      {mutation.error !== null ? (
        <p role="alert" className="admin-error">
          {errorMessage(mutation.error)}
        </p>
      ) : null}

      <TrackSearch
        renderActions={(track) => (
          <button type="button" onClick={() => selectTrack(track)} disabled={mutation.status === 'pending'}>
            {selectedTrack?.id === track.id ? 'Selected' : `Edit ${track.title || 'track'}`}
          </button>
        )}
      />

      {selectedTrack !== null && draft !== null ? (
        <form
          onSubmit={(event) => {
            event.preventDefault();
            saveMetadata();
          }}
          style={{ display: 'grid', gap: '8px', maxWidth: '560px', marginTop: '18px' }}
        >
          <h3>Editing {selectedTrack.title || 'track'}</h3>
          <p className="admin-muted">Metadata source: {selectedTrack.metadataSource}</p>
          <div className="group">
            <label htmlFor="track-title">Title</label>
            <input
              id="track-title"
              value={draft.title}
              maxLength={200}
              disabled={mutation.status === 'pending'}
              onChange={(event) => setDraft({ ...draft, title: event.target.value })}
            />
          </div>
          <div className="group">
            <label htmlFor="track-artist">Artist</label>
            <input
              id="track-artist"
              value={draft.artist}
              maxLength={200}
              disabled={mutation.status === 'pending'}
              onChange={(event) => setDraft({ ...draft, artist: event.target.value })}
            />
          </div>
          <div className="group">
            <label htmlFor="track-album">Album</label>
            <input
              id="track-album"
              value={draft.album}
              maxLength={200}
              disabled={mutation.status === 'pending'}
              onChange={(event) => setDraft({ ...draft, album: event.target.value })}
            />
          </div>
          <button type="submit" className="default" disabled={mutation.status === 'pending'}>
            {mutation.status === 'pending' ? 'Saving…' : 'Save metadata'}
          </button>

          <fieldset>
            <legend>Cover artwork</legend>
            {selectedTrack.coverImageUrl !== '' ? (
              <img
                src={selectedTrack.coverImageUrl}
                alt={`Cover for ${selectedTrack.title || 'track'}`}
                width={128}
                height={128}
                style={{ objectFit: 'cover', display: 'block', marginBottom: '8px' }}
              />
            ) : (
              <p className="admin-muted">No cover artwork is attached.</p>
            )}
            <input
              type="file"
              accept="image/jpeg,image/png,image/webp"
              aria-label="Cover image file"
              disabled={mutation.status === 'pending'}
              onChange={(event) => setCoverFile(event.target.files?.[0] ?? null)}
            />
            {coverFile !== null ? <p>{coverFile.name}</p> : null}
            <div style={{ display: 'flex', gap: '8px', marginTop: '8px' }}>
              <button
                type="button"
                onClick={uploadCover}
                disabled={mutation.status === 'pending' || coverFile === null}
              >
                {mutation.status === 'pending' ? 'Uploading…' : 'Upload cover'}
              </button>
              <button
                type="button"
                onClick={clearCover}
                disabled={mutation.status === 'pending' || selectedTrack.coverImageUrl === ''}
              >
                Clear cover
              </button>
            </div>
          </fieldset>
        </form>
      ) : (
        <p className="admin-muted">Select a track to edit its metadata or cover.</p>
      )}
    </section>
  );
}
