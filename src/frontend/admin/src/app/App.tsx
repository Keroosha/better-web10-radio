import { useState, type ReactElement } from 'react';

import { AdminAuthGate } from '../features/admin-auth/AdminAuthGate';
import { DashboardPage } from '../pages/dashboard/DashboardPage';
import { DonationGoalPage } from '../pages/donation-goal/DonationGoalPage';
import { SocialLinksPage } from '../pages/social-links/SocialLinksPage';
import { UnpinnedPage } from '../pages/unpinned/UnpinnedPage';
import { ADMIN_NAV_ITEMS, type AdminPageId } from '../shared/lib/pages';
import { AdminNav } from '../widgets/nav/AdminNav';

function renderPage(page: AdminPageId): ReactElement {
  switch (page) {
    case 'dashboard':
      return <DashboardPage />;
    case 'social-links':
      return <SocialLinksPage />;
    case 'donation-goal':
      return <DonationGoalPage />;
    case 'playlists':
      return (
        <UnpinnedPage
          title="Playlists"
          routes={[
            'GET/POST /api/v0/admin/playlists',
            'GET/POST/PUT /api/v0/admin/playlists/{playlistId}/items',
          ]}
        />
      );
    case 'storage':
      return <UnpinnedPage title="Storage" routes={['GET/PUT /api/v0/admin/storage']} />;
    case 'say-moderation':
      return (
        <UnpinnedPage
          title="Say moderation"
          routes={[
            'GET /api/v0/admin/say-messages?status=pending|approved|rejected',
            'POST /api/v0/admin/say-messages/{messageId}/approve',
            'POST /api/v0/admin/say-messages/{messageId}/reject',
          ]}
        />
      );
    case 'stream-node':
      return (
        <UnpinnedPage
          title="Stream-node"
          routes={[
            'GET /api/v0/admin/stream-node/status',
            'POST /api/v0/admin/stream-node/restart',
          ]}
        />
      );
    case 'library-scan':
      return <UnpinnedPage title="Library scan" routes={['POST /api/v0/admin/library/scan']} />;
  }
}

function AdminShell(): ReactElement {
  const [page, setPage] = useState<AdminPageId>('dashboard');
  return (
    <div style={{ display: 'flex', minHeight: '100vh', fontFamily: 'system-ui, sans-serif', color: '#123' }}>
      <AdminNav items={ADMIN_NAV_ITEMS} current={page} onNavigate={setPage} />
      <main style={{ flex: 1, padding: '18px 22px', borderLeft: '1px solid #e5e5e5' }}>
        {renderPage(page)}
      </main>
    </div>
  );
}

/** Admin cabinet: an auth gate wrapping the FSD shell (nav + active page). */
export function App(): ReactElement {
  return (
    <AdminAuthGate>
      <AdminShell />
    </AdminAuthGate>
  );
}
