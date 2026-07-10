import type { ReactElement } from 'react';

interface UnpinnedPageProps {
  readonly title: string;
  /** The SPEC §5 admin routes this page will drive once their contract is pinned. */
  readonly routes: readonly string[];
}

/**
 * Placeholder for admin pages whose backend routes are still
 * `501 admin.contract_unpinned` (playlists, storage, `/say` moderation, stream-node
 * control, library scan). The FSD slot exists so wiring is a drop-in once the backend
 * pins the request/response bodies (B4+); we deliberately invent no DTO shapes here.
 */
export function UnpinnedPage({ title, routes }: UnpinnedPageProps): ReactElement {
  return (
    <section>
      <h2 style={{ fontSize: '16px' }}>{title}</h2>
      <div
        style={{
          border: '1px dashed #c8a200',
          background: '#fffbe6',
          padding: '12px 14px',
          borderRadius: '6px',
          maxWidth: '560px',
        }}
      >
        <p style={{ margin: 0, fontWeight: 600 }}>Contract not pinned yet</p>
        <p style={{ margin: '6px 0 0', fontSize: '13px' }}>
          The backend serves these routes as <code>501 admin.contract_unpinned</code>. This page
          lands once the admin request/response bodies are pinned (backend B4+).
        </p>
        <ul style={{ margin: '8px 0 0', fontSize: '12px', fontFamily: 'monospace' }}>
          {routes.map((route) => (
            <li key={route}>{route}</li>
          ))}
        </ul>
      </div>
    </section>
  );
}
