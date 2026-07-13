// The cabinet's in-memory navigation model (no router — a single window with a tree,
// mirroring the mock's `state.view` + `state.groupBy`).

export type AdminView =
  | 'player'
  | 'library'
  | 'playlists'
  | 'banners'
  | 'storage'
  | 'node';

export type LibraryGroupBy = 'album' | 'artist';

export interface AdminNav {
  readonly view: AdminView;
  readonly groupBy: LibraryGroupBy;
}

export const INITIAL_NAV: AdminNav = { view: 'player', groupBy: 'album' };

/** `Web10.Radio › <Section> › <Sub>` breadcrumb tail for the current view. */
export function breadcrumb(nav: AdminNav): string {
  switch (nav.view) {
    case 'player':
      return 'Плеер';
    case 'library':
      return `Библиотека › ${nav.groupBy === 'album' ? 'По альбомам' : 'По исполнителям'}`;
    case 'playlists':
      return 'Плейлисты';
    case 'banners':
      return 'Настройки › Баннеры';
    case 'storage':
      return 'Настройки › Хранилища';
    case 'node':
      return 'Настройки › Нода трансляции';
  }
}
