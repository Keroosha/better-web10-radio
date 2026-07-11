import { useCallback, useEffect, useState, type ReactElement } from 'react';

import {
  ApiError,
  getStreamNodeStatus,
  restartStreamNode,
  startStreamNode,
  stopStreamNode,
  type StreamNodeControl,
  type StreamNodeStatus,
} from '@web10/shared';

type ControlAction = 'start' | 'stop' | 'restart';

/** Polls the stream-node control plane and submits explicit operator controls. */
export function StreamNodePage(): ReactElement {
  const [status, setStatus] = useState<StreamNodeStatus | null>(null);
  const [control, setControl] = useState<StreamNodeControl | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [pendingAction, setPendingAction] = useState<ControlAction | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionSuccess, setActionSuccess] = useState<string | null>(null);

  const refreshStatus = useCallback(async (): Promise<void> => {
    const nextStatus = await getStreamNodeStatus();
    setStatus(nextStatus);
    setLoadError(null);
  }, []);

  useEffect(() => {
    let active = true;

    void getStreamNodeStatus()
      .then((nextStatus) => {
        if (active) {
          setStatus(nextStatus);
          setLoadError(null);
        }
      })
      .catch((cause) => {
        if (active) {
          setLoadError(
            cause instanceof ApiError && cause.code !== null
              ? `${cause.code}: ${cause.message}`
              : cause instanceof Error
                ? cause.message
                : String(cause),
          );
        }
      })
      .finally(() => {
        if (active) {
          setIsLoading(false);
        }
      });

    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    if (isLoading) {
      return;
    }

    let active = true;
    const interval = window.setInterval(() => {
      void getStreamNodeStatus()
        .then((nextStatus) => {
          if (active) {
            setStatus(nextStatus);
            setLoadError(null);
          }
        })
        .catch((cause) => {
          if (active) {
            setStatus(null);
            setLoadError(
              cause instanceof ApiError && cause.code !== null
                ? `${cause.code}: ${cause.message}`
                : cause instanceof Error
                  ? cause.message
                  : String(cause),
            );
          }
        });
    }, 1000);

    return () => {
      active = false;
      window.clearInterval(interval);
    };
  }, [isLoading]);

  const runControl = useCallback(
    async (action: ControlAction): Promise<void> => {
      setPendingAction(action);
      setActionError(null);
      setActionSuccess(null);

      try {
        let nextControl: StreamNodeControl;
        if (action === 'start') {
          nextControl = await startStreamNode();
        } else if (action === 'stop') {
          nextControl = await stopStreamNode();
        } else {
          nextControl = await restartStreamNode();
        }

        const actionLabel = action === 'start' ? 'Start' : action === 'stop' ? 'Stop' : 'Restart';
        setControl(nextControl);
        setActionSuccess(`${actionLabel} request accepted.`);
        void refreshStatus().catch((cause) => {
          setLoadError(
            cause instanceof ApiError && cause.code !== null
              ? `${cause.code}: ${cause.message}`
              : cause instanceof Error
                ? cause.message
                : String(cause),
          );
        });
      } catch (cause) {
        setActionError(
          cause instanceof ApiError && cause.code !== null
            ? `${cause.code}: ${cause.message}`
            : cause instanceof Error
              ? cause.message
              : String(cause),
        );
      } finally {
        setPendingAction(null);
      }
    },
    [refreshStatus],
  );

  if (isLoading) {
    return (
      <section>
        <h2>Stream-node</h2>
        <p className="admin-muted">Loading stream-node status…</p>
      </section>
    );
  }

  if (loadError !== null || status === null) {
    return (
      <section>
        <h2>Stream-node</h2>
        <p role="alert" className="admin-error">
          Failed to load stream-node status: {loadError ?? 'Status data was unavailable.'}
        </p>
      </section>
    );
  }

  const desiredState = control?.desiredState ?? status.desiredState;
  const restartGeneration = control?.restartGeneration ?? status.restartGeneration;

  return (
    <section>
      <h2 style={{ fontSize: '16px' }}>Stream-node</h2>
      <p className="admin-muted">Status refreshes every second.</p>

      <dl>
        <dt>Status</dt>
        <dd>{status.status}</dd>
        <dt>Desired state</dt>
        <dd>{desiredState}</dd>
        <dt>Last heartbeat</dt>
        <dd>{status.lastHeartbeatUtc ?? 'Never'}</dd>
        <dt>Failure reason</dt>
        <dd>{status.failureReason ?? '—'}</dd>
        <dt>Bitrate</dt>
        <dd>{status.bitrateKbps} kbps</dd>
        <dt>Restart generation</dt>
        <dd>Generation {restartGeneration}</dd>
      </dl>

      <div style={{ display: 'flex', gap: '8px', marginTop: '10px' }}>
        <button
          type="button"
          className="default"
          disabled={pendingAction !== null}
          onClick={() => void runControl('start')}
        >
          {pendingAction === 'start' ? 'Starting…' : 'Start'}
        </button>
        <button
          type="button"
          disabled={pendingAction !== null}
          onClick={() => void runControl('stop')}
        >
          {pendingAction === 'stop' ? 'Stopping…' : 'Stop'}
        </button>
        <button
          type="button"
          disabled={pendingAction !== null}
          onClick={() => void runControl('restart')}
        >
          {pendingAction === 'restart' ? 'Restarting…' : 'Restart'}
        </button>
      </div>

      {actionError !== null ? (
        <p role="alert" className="admin-error">
          {actionError}
        </p>
      ) : null}
      {actionSuccess !== null ? <p role="status">{actionSuccess}</p> : null}
    </section>
  );
}
