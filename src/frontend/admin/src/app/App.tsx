import { useCallback, useEffect, useState, type ReactElement } from 'react';

import {
  pauseStreamNode,
  skipCurrent,
  startStreamNode,
  stopStreamNode,
  type PlaybackState,
  type StreamNodeControl,
} from '@web10/shared';

import { AdminAuthGate, useAdminAuth } from '../features/admin-auth/AdminAuthGate';
import { useLiveQueue } from '../entities/live-queue';
import { errorMessage } from '../shared/lib/errorMessage';
import { BannersPage } from '../pages/banners/BannersPage';
import { LibraryPage } from '../pages/library/LibraryPage';
import { NodePage } from '../pages/node/NodePage';
import { PlayerPage } from '../pages/player/PlayerPage';
import { PlaylistsPage } from '../pages/playlists/PlaylistsPage';
import { StoragePage } from '../pages/storage/StoragePage';
import { breadcrumb, INITIAL_NAV, type AdminNav, type AdminView, type LibraryGroupBy } from '../shared/lib/view';
import { ToastProvider, useToast } from '../shared/ui/toast';
import { NavTree } from '../widgets/nav-tree/NavTree';
import { TransportBar } from '../widgets/transport-bar/TransportBar';

function AdminShell(): ReactElement {
  const [nav, setNav] = useState<AdminNav>(INITIAL_NAV);
  const [optimistic, setOptimistic] = useState<PlaybackState | null>(null);
  const [isLoggingOut, setIsLoggingOut] = useState(false);
  const auth = useAdminAuth();
  const { showToast } = useToast();
  const live = useLiveQueue();

  const playbackState = optimistic ?? live.playbackState;

  // Drop the optimistic override once the live snapshot agrees with it.
  useEffect(() => {
    if (optimistic !== null && live.playbackState === optimistic) {
      setOptimistic(null);
    }
  }, [optimistic, live.playbackState]);

  const navigate = useCallback((view: AdminView, groupBy?: LibraryGroupBy): void => {
    setNav((current) => ({ view, groupBy: groupBy ?? current.groupBy }));
  }, []);

  const control = useCallback(
    (next: PlaybackState, action: () => Promise<StreamNodeControl>, message: string): void => {
      setOptimistic(next);
      action()
        .then(() => showToast(message))
        .catch((cause) => {
          setOptimistic(null);
          showToast(errorMessage(cause, 'Ошибка управления'));
        });
    },
    [showToast],
  );

  const onTogglePlay = useCallback((): void => {
    if (playbackState === 'playing') {
      control('paused', pauseStreamNode, 'Пауза');
    } else {
      control('playing', startStreamNode, 'Воспроизведение');
    }
  }, [playbackState, control]);

  const onStop = useCallback((): void => control('stopped', stopStreamNode, 'Остановлено'), [control]);

  const onSkip = useCallback((): void => {
    skipCurrent()
      .then(() => showToast('Пропущено'))
      .catch((cause) => showToast(errorMessage(cause, 'Ошибка')));
  }, [showToast]);

  const onLogout = async (): Promise<void> => {
    if (auth === null) {
      return;
    }
    setIsLoggingOut(true);
    try {
      await auth.logout();
    } catch {
      setIsLoggingOut(false);
    }
  };

  return (
    <div style={{ height: '100vh', padding: '10px', display: 'flex' }}>
      <div
        className="window glass active"
        style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0, maxWidth: 'none', width: 'auto' }}
      >
        <div className="title-bar" style={{ flex: 'none' }}>
          <div className="title-bar-text">Web10.Radio — Администрирование</div>
          <div className="title-bar-controls">
            <button type="button" aria-label="Minimize" tabIndex={-1} />
            <button type="button" aria-label="Maximize" tabIndex={-1} />
            <button
              type="button"
              aria-label="Close"
              onClick={() => void onLogout()}
              disabled={isLoggingOut}
            />
          </div>
        </div>
        <div className="window-body" style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <div
            style={{ display: 'flex', alignItems: 'center', gap: '8px', padding: '4px 2px 8px', flex: 'none', lineHeight: 1 }}
          >
            <span style={{ fontSize: '12px', color: '#33556b' }}>Web10.Radio</span>
            <span style={{ fontSize: '12px', color: '#9bb' }}>›</span>
            <strong style={{ fontSize: '12px', color: '#12354a' }}>{breadcrumb(nav)}</strong>
          </div>
          <div style={{ flex: 1, display: 'flex', gap: '8px', minHeight: 0 }}>
            <NavTree nav={nav} onNavigate={navigate} />
            <div
              style={{
                flex: 1,
                minWidth: 0,
                overflow: 'auto',
                background: '#fff',
                border: '1px solid #a7c4dd',
                borderRadius: '3px',
                padding: '16px',
              }}
            >
              {nav.view === 'player' ? <PlayerPage queue={live.queue} nowPlaying={live.nowPlaying} /> : null}
              {nav.view === 'library' ? <LibraryPage groupBy={nav.groupBy} /> : null}
              {nav.view === 'playlists' ? <PlaylistsPage /> : null}
              {nav.view === 'banners' ? <BannersPage /> : null}
              {nav.view === 'storage' ? <StoragePage /> : null}
              {nav.view === 'node' ? <NodePage /> : null}
            </div>
          </div>
          <TransportBar
            nowPlaying={live.nowPlaying}
            playbackState={playbackState}
            onSkip={onSkip}
            onTogglePlay={onTogglePlay}
            onStop={onStop}
          />
        </div>
      </div>
    </div>
  );
}

/** Admin cabinet: cookie-session gate wrapping the single-window shell + toast host. */
export function App(): ReactElement {
  return (
    <AdminAuthGate>
      <ToastProvider>
        <AdminShell />
      </ToastProvider>
    </AdminAuthGate>
  );
}
