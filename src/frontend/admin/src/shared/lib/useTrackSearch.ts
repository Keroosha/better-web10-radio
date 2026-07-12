import { useCallback, useEffect, useRef, useState } from 'react';

import { getTracksPage, type AdminTrack } from '@web10/shared';

import type { ApiResource } from './useApiResource';

export interface UseTrackSearch {
  readonly query: string;
  setQuery(next: string): void;
  readonly state: ApiResource<AdminTrack[]>;
  readonly nextCursor: string | null;
  readonly isLoadingMore: boolean;
  readonly loadMoreError: Error | null;
  loadMore(): void;
}

export interface UseTrackSearchOptions {
  readonly debounceMs?: number;
  readonly limit?: number;
}

const MAX_QUERY_LENGTH = 200;

function toError<TCause>(cause: TCause): Error {
  return cause instanceof Error ? cause : new Error(String(cause));
}

function appendUnique(current: readonly AdminTrack[], incoming: readonly AdminTrack[]): AdminTrack[] {
  const seen = new Set<string>(current.map((track) => track.id));
  const result = [...current];
  for (const track of incoming) {
    if (!seen.has(track.id)) {
      seen.add(track.id);
      result.push(track);
    }
  }
  return result;
}

/**
 * Cursor-paginated list-first track search. The first page is loaded immediately
 * for the empty query; typed queries are debounced and abort the previous request.
 * Additional pages append by ID and never replace the current list.
 */
export function useTrackSearch(options: UseTrackSearchOptions = {}): UseTrackSearch {
  const debounceMs = options.debounceMs ?? 250;
  const limit = options.limit ?? 100;
  const [query, setQuery] = useState('');
  const [state, setState] = useState<ApiResource<AdminTrack[]>>({ status: 'loading' });
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [isLoadingMore, setIsLoadingMore] = useState(false);
  const [loadMoreError, setLoadMoreError] = useState<Error | null>(null);
  const generation = useRef(0);
  const queryRef = useRef('');
  const loadMoreController = useRef<AbortController | null>(null);

  useEffect(() => {
    const trimmed = query.trim();
    queryRef.current = trimmed;
    generation.current += 1;
    const requestGeneration = generation.current;
    loadMoreController.current?.abort();
    loadMoreController.current = null;
    setNextCursor(null);
    setIsLoadingMore(false);
    setLoadMoreError(null);

    if (trimmed.length > MAX_QUERY_LENGTH) {
      setState({
        status: 'error',
        error: new Error(`Track search must be at most ${MAX_QUERY_LENGTH} characters.`),
      });
      return;
    }

    let active = true;
    const controller = new AbortController();
    // The initial full-library load fires immediately; typed queries debounce.
    const delay = query === '' ? 0 : debounceMs;
    const timer = window.setTimeout(() => {
      setState({ status: 'loading' });
      getTracksPage({ query: trimmed, limit }, { signal: controller.signal })
        .then((page) => {
          if (active && generation.current === requestGeneration) {
            setState({ status: 'ready', data: page.items });
            setNextCursor(page.nextCursor);
          }
        })
        .catch((cause) => {
          if (
            active &&
            generation.current === requestGeneration &&
            !(cause instanceof DOMException && cause.name === 'AbortError')
          ) {
            setState({ status: 'error', error: toError(cause) });
          }
        });
    }, delay);

    return () => {
      active = false;
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [query, debounceMs, limit]);

  const loadMore = useCallback((): void => {
    const cursor = nextCursor;
    if (cursor === null || isLoadingMore || state.status !== 'ready') {
      return;
    }

    const requestGeneration = generation.current;
    const controller = new AbortController();
    loadMoreController.current?.abort();
    loadMoreController.current = controller;
    setIsLoadingMore(true);
    setLoadMoreError(null);

    getTracksPage({ query: queryRef.current, limit, cursor }, { signal: controller.signal })
      .then((page) => {
        if (generation.current !== requestGeneration || controller.signal.aborted) {
          return;
        }
        setState((current) => {
          if (current.status !== 'ready') {
            return current;
          }
          return { status: 'ready', data: appendUnique(current.data, page.items) };
        });
        setNextCursor(page.nextCursor);
      })
      .catch((cause) => {
        if (generation.current !== requestGeneration || controller.signal.aborted) {
          return;
        }
        setLoadMoreError(toError(cause));
      })
      .finally(() => {
        if (generation.current === requestGeneration) {
          setIsLoadingMore(false);
        }
      });
  }, [isLoadingMore, limit, nextCursor, state.status]);

  return { query, setQuery, state, nextCursor, isLoadingMore, loadMoreError, loadMore };
}
