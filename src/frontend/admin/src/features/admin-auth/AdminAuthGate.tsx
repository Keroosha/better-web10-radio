import {
  ApiError,
  clearAdminSession,
  getAdminSession,
  loginAdmin,
  setAdminSession,
  logoutAdmin,
  subscribeToAdminSessionInvalidation,
  type AdminSession,
} from '@web10/shared';
import {
  createContext,
  useContext,
  useEffect,
  useState,
  type FormEvent,
  type ReactElement,
  type ReactNode,
} from 'react';

interface AdminAuthGateProps {
  readonly children: ReactNode;
}

interface AdminAuthContextValue {
  readonly session: AdminSession;
  readonly logout: () => Promise<void>;
}

const AdminSessionContext = createContext<AdminSession | null>(null);
const AdminAuthContext = createContext<AdminAuthContextValue | null>(null);

/** The authenticated session for admin-page features, or `null` outside the cabinet shell. */
export function useAdminSession(): AdminSession | null {
  return useContext(AdminSessionContext);
}

/** The authenticated session and its server-backed logout action for cabinet-shell controls. */
export function useAdminAuth(): AdminAuthContextValue | null {
  return useContext(AdminAuthContext);
}

function formatServerError(error: Error): string {
  if (error instanceof ApiError && error.code !== null) {
    return error.code;
  }

  return error.message;
}

/**
 * Establishes the opaque cookie-backed admin session before mounting the cabinet.
 * A global authenticated-request `401` invalidates this gate without recursively
 * probing the session endpoint; login/session probes opt out in the shared client.
 */
export function AdminAuthGate({ children }: AdminAuthGateProps): ReactElement {
  const [session, setSession] = useState<AdminSession | null>(null);
  const [isProbing, setIsProbing] = useState(true);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);

  useEffect(() => {
    let isCurrent = true;

    const restoreSession = async (): Promise<void> => {
      try {
        const restored = await getAdminSession();
        if (!isCurrent) {
          return;
        }
        setAdminSession(restored);
        setSession(restored);
      } catch {
        if (!isCurrent) {
          return;
        }
        clearAdminSession();
        setSession(null);
      } finally {
        if (isCurrent) {
          setIsProbing(false);
        }
      }
    };

    void restoreSession();
    const unsubscribe = subscribeToAdminSessionInvalidation(() => {
      clearAdminSession();
      setSession(null);
      setServerError(null);
    });

    return () => {
      isCurrent = false;
      unsubscribe();
    };
  }, []);

  const onSubmit = async (event: FormEvent<HTMLFormElement>): Promise<void> => {
    event.preventDefault();
    setServerError(null);
    setIsSubmitting(true);

    try {
      const authenticated = await loginAdmin({ username, password });
      setAdminSession(authenticated);
      setSession(authenticated);
      setPassword('');
    } catch (error) {
      if (error instanceof Error) {
        setServerError(formatServerError(error));
      } else {
        setServerError('Unable to sign in');
      }
    } finally {
      setIsSubmitting(false);
    }
  };

  const logout = async (): Promise<void> => {
    await logoutAdmin();
    clearAdminSession();
    setSession(null);
    setServerError(null);
  };

  if (isProbing) {
    return (
      <div className="admin-login-desktop">
        <div className="window glass active" style={{ width: 'min(360px, 100%)' }}>
          <div className="title-bar">
            <div className="title-bar-text">Web10.Radio — Admin</div>
          </div>
          <div className="window-body has-space">
            <p role="status">Checking admin session…</p>
          </div>
        </div>
      </div>
    );
  }

  if (session !== null) {
    return (
      <AdminAuthContext.Provider value={{ session, logout }}>
        <AdminSessionContext.Provider value={session}>{children}</AdminSessionContext.Provider>
      </AdminAuthContext.Provider>
    );
  }
  return (
    <div className="admin-login-desktop">
      <div className="window glass active" style={{ width: 'min(420px, 100%)' }}>
        <div className="title-bar">
          <div className="title-bar-text">Web10.Radio — Admin</div>
          <div className="title-bar-controls">
            <button type="button" aria-label="Close" tabIndex={-1} />
          </div>
        </div>
        <div className="window-body has-space">
          <p className="admin-muted">Sign in with your administrator credentials.</p>
          <form
            onSubmit={(event) => void onSubmit(event)}
            style={{ display: 'flex', flexDirection: 'column', gap: '10px' }}
          >
            <div className="group">
              <label htmlFor="admin-username">Username</label>
              <input
                id="admin-username"
                type="text"
                value={username}
                onChange={(event) => setUsername(event.target.value)}
                autoComplete="username"
                disabled={isSubmitting}
                required
              />
            </div>
            <div className="group">
              <label htmlFor="admin-password">Password</label>
              <input
                id="admin-password"
                type="password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                autoComplete="current-password"
                disabled={isSubmitting}
                required
              />
            </div>
            {serverError !== null && (
              <div role="alert" style={{ alignSelf: 'stretch' }}>
                <div role="tooltip" className="admin-error">
                  {serverError}
                </div>
              </div>
            )}
            <button type="submit" className="default" disabled={isSubmitting} style={{ alignSelf: 'flex-start' }}>
              {isSubmitting ? 'Signing in…' : 'Sign in'}
            </button>
          </form>
        </div>
      </div>
    </div>
  );
}
