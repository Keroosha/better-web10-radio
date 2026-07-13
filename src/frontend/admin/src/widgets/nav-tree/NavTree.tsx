import type { CSSProperties, ReactElement } from 'react';

import type { AdminNav, AdminView, LibraryGroupBy } from '../../shared/lib/view';
import { COLORS } from '../../shared/ui/tokens';

interface NavTreeProps {
  readonly nav: AdminNav;
  readonly onNavigate: (view: AdminView, groupBy?: LibraryGroupBy) => void;
}

const SELECTED_BG = COLORS.selection;

function itemStyle(active: boolean): CSSProperties {
  return {
    cursor: 'pointer',
    padding: '3px 6px',
    borderRadius: '3px',
    background: active ? SELECTED_BG : 'transparent',
    display: 'flex',
    alignItems: 'center',
    gap: '7px',
  };
}

const glyph: CSSProperties = { width: '20px', textAlign: 'center', fontSize: '15px' };

/** Left navigation tree — the single source of section transitions (ПРАВИЛА §2). */
export function NavTree({ nav, onNavigate }: NavTreeProps): ReactElement {
  const isLibrary = nav.view === 'library';
  return (
    <ul
      className="tree-view has-container"
      style={{ flex: 'none', width: '214px', overflow: 'auto', margin: 0, fontSize: '13px' }}
    >
      <li style={itemStyle(nav.view === 'player')} onClick={() => onNavigate('player')}>
        <span style={glyph}>🎧</span>Плеер
      </li>
      <li>
        <details open>
          <summary style={{ cursor: 'pointer', padding: '2px 0', display: 'flex', alignItems: 'center', gap: '7px' }}>
            <span style={glyph}>🎼</span>Библиотека
          </summary>
          <ul>
            <li
              style={itemStyle(isLibrary && nav.groupBy === 'album')}
              onClick={() => onNavigate('library', 'album')}
            >
              <span style={glyph}>💿</span>По альбомам
            </li>
            <li
              style={itemStyle(isLibrary && nav.groupBy === 'artist')}
              onClick={() => onNavigate('library', 'artist')}
            >
              <span style={glyph}>🎤</span>По исполнителям
            </li>
          </ul>
        </details>
      </li>
      <li style={itemStyle(nav.view === 'playlists')} onClick={() => onNavigate('playlists')}>
        <span style={glyph}>🎶</span>Плейлисты
      </li>
      <li>
        <details open>
          <summary style={{ cursor: 'pointer', padding: '2px 0', display: 'flex', alignItems: 'center', gap: '7px' }}>
            <span style={glyph}>⚙️</span>Настройки
          </summary>
          <ul>
            <li style={itemStyle(nav.view === 'banners')} onClick={() => onNavigate('banners')}>
              <span style={glyph}>🖼️</span>Баннеры
            </li>
            <li style={itemStyle(nav.view === 'storage')} onClick={() => onNavigate('storage')}>
              <span style={glyph}>🗄️</span>Хранилища
            </li>
            <li style={itemStyle(nav.view === 'node')} onClick={() => onNavigate('node')}>
              <span style={glyph}>📡</span>Нода трансляции
            </li>
          </ul>
        </details>
      </li>
    </ul>
  );
}
