import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useRef,
  useState,
  type ReactElement,
  type ReactNode,
} from 'react';

interface ToastContextValue {
  /** Show a short confirmation at the bottom-right; auto-hides after ~2.2s. */
  readonly showToast: (message: string) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);

/** Bottom-right confirmation toast host (ПРАВИЛА-UI.md §8). */
export function ToastProvider({ children }: { readonly children: ReactNode }): ReactElement {
  const [toast, setToast] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const showToast = useCallback((message: string): void => {
    setToast(message);
    if (timer.current !== null) {
      clearTimeout(timer.current);
    }
    timer.current = setTimeout(() => setToast(null), 2200);
  }, []);

  const value = useMemo<ToastContextValue>(() => ({ showToast }), [showToast]);

  return (
    <ToastContext.Provider value={value}>
      {children}
      {toast !== null ? (
        <div
          role="status"
          aria-live="polite"
          style={{
            position: 'fixed',
            right: '24px',
            bottom: '84px',
            zIndex: 60,
            animation: 'balloonin .18s ease-out',
          }}
        >
          <div
            role="tooltip"
            className="is-top"
            onClick={() => setToast(null)}
            style={{ cursor: 'pointer', maxWidth: '340px' }}
          >
            {toast}
          </div>
        </div>
      ) : null}
    </ToastContext.Provider>
  );
}

/** Read the toast dispatcher. Throws if used outside {@link ToastProvider}. */
export function useToast(): ToastContextValue {
  const value = useContext(ToastContext);
  if (value === null) {
    throw new Error('useToast must be used within a ToastProvider.');
  }
  return value;
}
