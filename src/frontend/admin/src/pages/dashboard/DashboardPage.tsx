import { useState, type FormEvent, type ReactElement } from 'react';

import {
  ApiError,
  createPaidVerticalSliceFixture,
  getStreamNodeStatus,
  type PaidVerticalSliceFixture,
  type StreamNodeStatus,
} from '@web10/shared';

import { useAdminSession } from '../../features/admin-auth/AdminAuthGate';
import { useApiResource } from '../../shared/lib/useApiResource';
import { ResourceView } from '../../shared/ui/ResourceView';

const loadStreamStatus = (): Promise<StreamNodeStatus> => getStreamNodeStatus();

function Row({ label, value }: { readonly label: string; readonly value: string }): ReactElement {
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', padding: '6px 0', borderBottom: '1px solid #eee' }}>
      <span style={{ opacity: 0.7 }}>{label}</span>
      <span style={{ fontWeight: 600 }}>{value}</span>
    </div>
  );
}
/** Admin dashboard backed by the admin stream-node and session contracts. */
export function DashboardPage(): ReactElement {
  const streamStatus = useApiResource(loadStreamStatus);
  const session = useAdminSession();
  const [fixtureKey, setFixtureKey] = useState('admin-demo');
  const [fixture, setFixture] = useState<PaidVerticalSliceFixture | null>(null);
  const [fixtureError, setFixtureError] = useState<Error | null>(null);
  const [isCreatingFixture, setIsCreatingFixture] = useState(false);

  const createFixture = async (event: FormEvent<HTMLFormElement>): Promise<void> => {
    event.preventDefault();
    if (isCreatingFixture) {
      return;
    }

    const normalizedFixtureKey = fixtureKey.trim();
    if (normalizedFixtureKey.length === 0 || normalizedFixtureKey.length > 64) {
      setFixture(null);
      setFixtureError(new Error('Fixture key must contain 1–64 characters.'));
      return;
    }

    setIsCreatingFixture(true);
    setFixtureError(null);
    setFixture(null);
    try {
      setFixture(await createPaidVerticalSliceFixture({ fixtureKey: normalizedFixtureKey }));
    } catch (cause) {
      setFixtureError(cause instanceof Error ? cause : new Error('Unable to create demo data.'));
    } finally {
      setIsCreatingFixture(false);
    }
  };

  return (
    <section>
      <h2 style={{ fontSize: '16px' }}>Dashboard</h2>
      <ResourceView resource={streamStatus}>
        {(status) => (
          <div style={{ maxWidth: '520px' }}>
            <Row label="Stream status" value={status.status} />
            <Row label="Desired state" value={status.desiredState} />
            <Row label="Bitrate" value={`${status.bitrateKbps} kbps`} />
            <Row label="Last heartbeat" value={status.lastHeartbeatUtc ?? '—'} />
            <Row label="Failure reason" value={status.failureReason ?? '—'} />
            <Row label="Restart generation" value={String(status.restartGeneration)} />
          </div>
        )}
      </ResourceView>

      {session?.developmentFixturesEnabled === true ? (
        <form onSubmit={createFixture} style={{ marginTop: '20px', maxWidth: '520px' }}>
          <h3 style={{ fontSize: '14px' }}>Development fixtures</h3>
          <label htmlFor="fixture-key" style={{ display: 'block', marginBottom: '6px' }}>
            Fixture key
          </label>
          <input
            id="fixture-key"
            value={fixtureKey}
            onChange={(event) => setFixtureKey(event.currentTarget.value)}
            disabled={isCreatingFixture}
            maxLength={64}
          />
          <button type="submit" disabled={isCreatingFixture} style={{ marginLeft: '8px' }}>
            {isCreatingFixture ? 'Creating…' : 'Create demo data'}
          </button>
          {fixtureError !== null ? (
            <p role="alert" style={{ color: '#b00020' }}>
              {fixtureError instanceof ApiError && fixtureError.code !== null
                ? fixtureError.code
                : fixtureError.message}
            </p>
          ) : null}
          {fixture !== null ? (
            <>
              <p>Demo data created</p>
              <p style={{ opacity: 0.7 }}>
                Donation {fixture.donationPaymentId}, message {fixture.sayMessageId}.
              </p>
            </>
          ) : null}
        </form>
      ) : null}
    </section>
  );
}
