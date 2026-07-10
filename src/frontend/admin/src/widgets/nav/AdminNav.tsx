import type { CSSProperties, ReactElement } from 'react';

import type { AdminNavItem, AdminPageId } from '../../shared/lib/pages';

interface AdminNavProps {
  readonly items: readonly AdminNavItem[];
  readonly current: AdminPageId;
  readonly onNavigate: (id: AdminPageId) => void;
  readonly onLogout: () => void;
  readonly isLoggingOut: boolean;
}

function itemStyle(active: boolean): CSSProperties {
  return {
    display: 'block',
    width: '100%',
    textAlign: 'left',
    padding: '7px 10px',
    marginBottom: '2px',
    borderRadius: '5px',
    border: 'none',
    cursor: 'pointer',
    fontSize: '13px',
    background: active ? '#1084d0' : 'transparent',
    color: active ? '#fff' : '#123',
  };
}

/** Left-rail navigation for the admin cabinet. */
export function AdminNav({ items, current, onNavigate, onLogout, isLoggingOut }: AdminNavProps): ReactElement {
  return (
    <nav aria-label="Admin sections" style={{ width: '190px', flex: 'none', padding: '12px' }}>
      <div style={{ fontWeight: 700, fontSize: '13px', marginBottom: '10px' }}>Web10.Radio</div>
      {items.map((item) => (
        <button
          key={item.id}
          type="button"
          onClick={() => onNavigate(item.id)}
          style={itemStyle(item.id === current)}
          aria-current={item.id === current ? 'page' : undefined}
        >
          {item.label}
        </button>
      ))}
      <button
        type="button"
        onClick={onLogout}
        disabled={isLoggingOut}
        style={{ ...itemStyle(false), marginTop: '16px', border: '1px solid #aac' }}
      >
        {isLoggingOut ? 'Logging out…' : 'Log out'}
      </button>
    </nav>
  );
}
