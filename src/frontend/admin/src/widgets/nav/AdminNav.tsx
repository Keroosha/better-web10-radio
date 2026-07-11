import type { ReactElement } from 'react';

import type { AdminNavItem, AdminPageId } from '../../shared/lib/pages';

interface AdminNavProps {
  readonly items: readonly AdminNavItem[];
  readonly current: AdminPageId;
  readonly onNavigate: (id: AdminPageId) => void;
}

/** Top tab-bar navigation for the admin cabinet (7.css tabs). */
export function AdminNav({ items, current, onNavigate }: AdminNavProps): ReactElement {
  return (
    <menu role="tablist" aria-label="Admin sections" className="admin-tablist">
      {items.map((item) => (
        <button
          key={item.id}
          type="button"
          role="tab"
          aria-selected={item.id === current}
          aria-controls="admin-panel"
          onClick={() => onNavigate(item.id)}
        >
          {item.label}
        </button>
      ))}
    </menu>
  );
}
