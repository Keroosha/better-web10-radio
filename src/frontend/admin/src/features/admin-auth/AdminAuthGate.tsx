import { useState, type FormEvent, type ReactElement, type ReactNode } from 'react';

import { setAdminToken } from '@web10/shared';

interface AdminAuthGateProps {
  readonly children: ReactNode;
}

/**
 * Placeholder admin auth guard (SPEC §5: admin routes require
 * `Authorization: Bearer <WEB10_ADMIN__TOKEN>`). This milestone deliberately does NOT
 * invent OAuth/provider UX — it just captures the bearer token and pushes it into the
 * shared API client via `setAdminToken`, then renders the cabinet. A real login flow
 * lands once the backend pins the admin auth contract.
 */
export function AdminAuthGate({ children }: AdminAuthGateProps): ReactElement {
  const [token, setToken] = useState('');
  const [authenticated, setAuthenticated] = useState(false);

  const onSubmit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    if (token.trim() === '') {
      return;
    }
    setAdminToken(token.trim());
    setAuthenticated(true);
  };

  if (authenticated) {
    return <>{children}</>;
  }

  return (
    <main style={{ maxWidth: '420px', margin: '10vh auto', fontFamily: 'system-ui, sans-serif' }}>
      <h1 style={{ fontSize: '18px' }}>Web10.Radio — Admin</h1>
      <p style={{ fontSize: '13px', opacity: 0.75 }}>
        Enter the admin bearer token (<code>WEB10_ADMIN__TOKEN</code>) to continue. Placeholder
        guard — a real login lands once the admin contract is pinned.
      </p>
      <form onSubmit={onSubmit} style={{ display: 'flex', gap: '8px', marginTop: '12px' }}>
        <input
          type="password"
          value={token}
          onChange={(event) => setToken(event.target.value)}
          placeholder="admin bearer token"
          aria-label="admin bearer token"
          style={{ flex: 1, padding: '6px 8px' }}
        />
        <button type="submit" style={{ padding: '6px 12px' }}>
          Enter
        </button>
      </form>
    </main>
  );
}
