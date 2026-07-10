/** All admin cabinet pages. Unpinned ones render placeholders until the backend pins them. */
export type AdminPageId =
  | 'dashboard'
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
  /** True while the backend route contract is still `501 admin.contract_unpinned`. */
  readonly unpinned: boolean;
}

export const ADMIN_NAV_ITEMS: readonly AdminNavItem[] = [
  { id: 'dashboard', label: 'Dashboard', unpinned: false },
  { id: 'social-links', label: 'Social links', unpinned: false },
  { id: 'donation-goal', label: 'Donation goal', unpinned: false },
  { id: 'playlists', label: 'Playlists', unpinned: true },
  { id: 'storage', label: 'Storage', unpinned: true },
  { id: 'say-moderation', label: 'Say moderation', unpinned: true },
  { id: 'stream-node', label: 'Stream-node', unpinned: true },
  { id: 'library-scan', label: 'Library scan', unpinned: true },
];
