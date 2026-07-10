import { useState, type ReactElement } from 'react';

import { AdminAuthGate, useAdminAuth } from '../features/admin-auth/AdminAuthGate';
import { DashboardPage } from '../pages/dashboard/DashboardPage';
import { DonationGoalPage } from '../pages/donation-goal/DonationGoalPage';
import { LibraryScanPage } from '../pages/library-scan/LibraryScanPage';
import { PlaylistsPage } from '../pages/playlists/PlaylistsPage';
import { SayModerationPage } from '../pages/say-moderation/SayModerationPage';
import { SocialLinksPage } from '../pages/social-links/SocialLinksPage';
import { StoragePage } from '../pages/storage/StoragePage';
import { StreamNodePage } from '../pages/stream-node/StreamNodePage';
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
      return <PlaylistsPage />;
    case 'storage':
      return <StoragePage />;
    case 'say-moderation':
      return <SayModerationPage />;
    case 'stream-node':
      return <StreamNodePage />;
    case 'library-scan':
      return <LibraryScanPage />;
  }
}

function AdminShell(): ReactElement {
  const [page, setPage] = useState<AdminPageId>('dashboard');
  const [isLoggingOut, setIsLoggingOut] = useState(false);
  const [logoutError, setLogoutError] = useState<string | null>(null);
  const auth = useAdminAuth();

  const onLogout = async (): Promise<void> => {
    if (auth === null) {
      return;
    }

    setLogoutError(null);
    setIsLoggingOut(true);
    try {
      await auth.logout();
    } catch (error) {
      setLogoutError(error instanceof Error ? error.message : 'Unable to log out');
      setIsLoggingOut(false);
    }
  };

  return (
    <div style={{ display: 'flex', minHeight: '100vh', fontFamily: 'system-ui, sans-serif', color: '#123' }}>
      <AdminNav
        items={ADMIN_NAV_ITEMS}
        current={page}
        onNavigate={setPage}
        onLogout={() => void onLogout()}
        isLoggingOut={isLoggingOut}
      />
      <main style={{ flex: 1, padding: '18px 22px', borderLeft: '1px solid #e5e5e5' }}>
        {logoutError !== null && <p role="alert">{logoutError}</p>}
        {renderPage(page)}
      </main>
    </div>
  );
}

/** Admin cabinet: a cookie-backed session gate wrapping the FSD shell. */
export function App(): ReactElement {
  return (
    <AdminAuthGate>
      <AdminShell />
    </AdminAuthGate>
  );
}
