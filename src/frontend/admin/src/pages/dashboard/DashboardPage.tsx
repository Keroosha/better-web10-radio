import type { ReactElement } from 'react';

import { formatTrackLabel, getPlayerState, type PlayerState } from '@web10/shared';

import { useApiResource } from '../../shared/lib/useApiResource';
import { ResourceView } from '../../shared/ui/ResourceView';

// Stable module-level loader (see useApiResource). Dashboard reuses the PUBLIC player
// snapshot for stream/track/queue — it needs no admin auth.
const loadPlayerState = (): Promise<PlayerState> => getPlayerState();

function Row({ label, value }: { readonly label: string; readonly value: string }): ReactElement {
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', padding: '6px 0', borderBottom: '1px solid #eee' }}>
      <span style={{ opacity: 0.7 }}>{label}</span>
      <span style={{ fontWeight: 600 }}>{value}</span>
    </div>
  );
}

/**
 * Admin dashboard: stream status, current track and queue summary from the player
 * snapshot. The stream-node heartbeat (SPEC `GET /admin/stream-node/status`) is still a
 * `501 admin.contract_unpinned` placeholder, so it is surfaced as unavailable rather than
 * faked.
 */
export function DashboardPage(): ReactElement {
  const resource = useApiResource(loadPlayerState);

  return (
    <section>
      <h2 style={{ fontSize: '16px' }}>Dashboard</h2>
      <ResourceView resource={resource}>
        {(state) => {
          const track = formatTrackLabel(state.nowPlaying.artist, state.nowPlaying.title);
          return (
            <div style={{ maxWidth: '520px' }}>
              <Row label="Stream status" value={state.stream.status} />
              <Row label="Current track" value={track === '' ? '—' : track} />
              <Row label="Queue length" value={String(state.queue.items.length)} />
              <Row label="Approved super-chats" value={String(state.superChat.messages.length)} />
              <Row label="Stream-node heartbeat" value="unavailable — contract unpinned" />
            </div>
          );
        }}
      </ResourceView>
    </section>
  );
}
