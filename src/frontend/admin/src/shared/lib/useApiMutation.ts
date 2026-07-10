import { useCallback, useState } from 'react';

/** Lifecycle of a user-triggered admin mutation (approve/reject a `/say` message, etc.). */
export type MutationStatus = 'idle' | 'pending' | 'error' | 'done';

export interface ApiMutation {
  readonly status: MutationStatus;
  readonly error: Error | null;
  /**
   * Run a mutation thunk. `onDone` fires only on success (e.g. to bump a reload key).
   * The thunk is passed in per call so the hook stays free of generics over `unknown`
   * (SPEC §10 bans authored `unknown`); the caller closes over its own typed args.
   */
  run(action: () => Promise<void>, onDone?: () => void): void;
  reset(): void;
}

function toError<TCause>(cause: TCause): Error {
  return cause instanceof Error ? cause : new Error(String(cause));
}

/** Tracks idle/pending/error/done for a single admin mutation at a time. */
export function useApiMutation(): ApiMutation {
  const [status, setStatus] = useState<MutationStatus>('idle');
  const [error, setError] = useState<Error | null>(null);

  const run = useCallback((action: () => Promise<void>, onDone?: () => void): void => {
    setStatus('pending');
    setError(null);
    action()
      .then(() => {
        setStatus('done');
        onDone?.();
      })
      .catch((cause) => {
        setError(toError(cause));
        setStatus('error');
      });
  }, []);

  const reset = useCallback((): void => {
    setStatus('idle');
    setError(null);
  }, []);

  return { status, error, run, reset };
}
