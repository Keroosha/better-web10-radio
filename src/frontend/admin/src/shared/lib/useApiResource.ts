import { useEffect, useState } from 'react';

/** Async resource state for admin pages. */
export type ApiResource<T> =
  | { readonly status: 'loading' }
  | { readonly status: 'error'; readonly error: Error }
  | { readonly status: 'ready'; readonly data: T };

function toError<TCause>(cause: TCause): Error {
  return cause instanceof Error ? cause : new Error(String(cause));
}

/**
 * Runs `load` on mount and tracks loading/error/ready. `load` MUST be a stable
 * reference (module-level or `useCallback`) — an inline arrow changes every render and
 * would re-fire the effect in a loop.
 *
 * Pass `reloadKey` and bump it (e.g. after a mutation) to re-run `load`; omit it and
 * the resource loads exactly once. Existing callers that pass no key are unaffected.
 */
export function useApiResource<T>(load: () => Promise<T>, reloadKey?: number): ApiResource<T> {
  const [resource, setResource] = useState<ApiResource<T>>({ status: 'loading' });

  useEffect(() => {
    let active = true;
    setResource({ status: 'loading' });
    load()
      .then((data) => {
        if (active) {
          setResource({ status: 'ready', data });
        }
      })
      .catch((cause) => {
        if (active) {
          setResource({ status: 'error', error: toError(cause) });
        }
      });
    return () => {
      active = false;
    };
  }, [load, reloadKey]);

  return resource;
}
