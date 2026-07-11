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
    <tr>
      <td className="admin-muted">{label}</td>
      <td style={{ fontWeight: 600 }}>{value}</td>
    </tr>
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
      <h2>Dashboard</h2>
      <ResourceView resource={streamStatus}>
        {(status) => (
          <table style={{ maxWidth: '520px', width: '100%' }}>
            <tbody>
              <Row label="Stream status" value={status.status} />
              <Row label="Desired state" value={status.desiredState} />
              <Row label="Bitrate" value={`${status.bitrateKbps} kbps`} />
              <Row label="Last heartbeat" value={status.lastHeartbeatUtc ?? '—'} />
              <Row label="Failure reason" value={status.failureReason ?? '—'} />
              <Row label="Restart generation" value={String(status.restartGeneration)} />
            </tbody>
          </table>
        )}
      </ResourceView>

      {session?.developmentFixturesEnabled === true ? (
        <form onSubmit={createFixture} style={{ marginTop: '20px', maxWidth: '520px' }}>
          <h3>Development fixtures</h3>
          <div className="group" style={{ maxWidth: '260px' }}>
            <label htmlFor="fixture-key">Fixture key</label>
            <input
              id="fixture-key"
              value={fixtureKey}
              onChange={(event) => setFixtureKey(event.currentTarget.value)}
              disabled={isCreatingFixture}
              maxLength={64}
            />
          </div>
          <button type="submit" className="default" disabled={isCreatingFixture} style={{ marginTop: '10px' }}>
            {isCreatingFixture ? 'Creating…' : 'Create demo data'}
          </button>
          {fixtureError !== null ? (
            <p role="alert" className="admin-error">
              {fixtureError instanceof ApiError && fixtureError.code !== null
                ? fixtureError.code
                : fixtureError.message}
            </p>
          ) : null}
          {fixture !== null ? (
            <>
              <p>Demo data created</p>
              <p className="admin-muted">
                Donation {fixture.donationPaymentId}, message {fixture.sayMessageId}.
              </p>
            </>
          ) : null}
        </form>
      ) : null}
    </section>
  );
}
