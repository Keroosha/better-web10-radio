import { useId, type ReactElement, type ReactNode } from 'react';

import type { AdminTrack } from '@web10/shared';

import { useTrackSearch, type UseTrackSearchOptions } from '../../shared/lib/useTrackSearch';

interface TrackSearchProps {
  /** Per-track action buttons supplied by the caller (e.g. "Queue now", "Add to playlist"). */
  readonly renderActions: (track: AdminTrack) => ReactNode;
  readonly label?: string;
  /** Search-hook overrides; injected in tests. */
  readonly options?: UseTrackSearchOptions;
}

/**
 * List-first cursor-paginated track picker. Rendering of per-track actions is
 * delegated to the caller so the same widget serves queue, playlist, and metadata
 * editing flows.
 */
export function TrackSearch({ renderActions, label = 'Track search', options }: TrackSearchProps): ReactElement {
  const { query, setQuery, state, nextCursor, isLoadingMore, loadMoreError, loadMore } = useTrackSearch(options);
  const inputId = useId();

  return (
    <div>
      <div className="group">
        <label htmlFor={inputId}>{label}</label>
        <input
          id={inputId}
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          maxLength={200}
        />
      </div>

      {state.status === 'loading' ? <p className="admin-muted">Loading tracks…</p> : null}
      {state.status === 'error' ? (
        <p role="alert" className="admin-error">
          Failed to load tracks: {state.error.message}
        </p>
      ) : null}
      {state.status === 'ready' && state.data.length === 0 ? <p>No tracks matched this search.</p> : null}
      {state.status === 'ready' && state.data.length > 0 ? (
        <>
          <ul aria-label="Scanned tracks">
            {state.data.map((track) => (
              <li key={track.id} style={{ marginBottom: '8px' }}>
                <strong>{track.title || 'Untitled'}</strong> — {track.artist || 'Unknown artist'}
                <span style={{ display: 'inline-flex', gap: '6px', marginLeft: '8px' }}>
                  {renderActions(track)}
                </span>
              </li>
            ))}
          </ul>
          {loadMoreError !== null ? (
            <p role="alert" className="admin-error">
              Failed to load more tracks: {loadMoreError.message}
            </p>
          ) : null}
          {nextCursor !== null ? (
            <button type="button" onClick={loadMore} disabled={isLoadingMore}>
              {isLoadingMore ? 'Loading more tracks…' : 'Load more tracks'}
            </button>
          ) : null}
        </>
      ) : null}
    </div>
  );
}
