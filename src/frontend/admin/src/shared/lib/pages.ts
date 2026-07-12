/** All real admin cabinet pages. */
export type AdminPageId =
  | 'dashboard'
  | 'queue'
  | 'tracks'
  | 'social-links'
  | 'donation-goal'
  | 'playlists'
  | 'storage'
  | 'say-moderation'
  | 'stream-node'
  | 'library-scan';

export interface AdminNavItem {
  readonly id: AdminPageId;
  readonly label: string;
}

export const ADMIN_NAV_ITEMS: readonly AdminNavItem[] = [
  { id: 'dashboard', label: 'Dashboard' },
  { id: 'queue', label: 'Queue' },
  { id: 'tracks', label: 'Tracks' },
  { id: 'social-links', label: 'Social links' },
  { id: 'donation-goal', label: 'Donation goal' },
  { id: 'playlists', label: 'Playlists' },
  { id: 'storage', label: 'Storage' },
  { id: 'say-moderation', label: 'Say moderation' },
  { id: 'stream-node', label: 'Stream-node' },
  { id: 'library-scan', label: 'Library scan' },
];
