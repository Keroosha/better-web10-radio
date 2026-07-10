import { useCallback, useState, type ReactElement } from 'react';

import {
  ApiError,
  approveSayMessage,
  formatStars,
  formatUtcTime,
  getSayMessages,
  rejectSayMessage,
  type AdminSayMessage,
  type SuperChatStatus,
} from '@web10/shared';

import { useApiMutation } from '../../shared/lib/useApiMutation';
import { useApiResource } from '../../shared/lib/useApiResource';
import { ResourceView } from '../../shared/ui/ResourceView';

const STATUS_TABS: readonly SuperChatStatus[] = ['pending', 'approved', 'rejected'];

const REJECT_REASON_MIN = 1;
const REJECT_REASON_MAX = 500;

/** In-progress rejection: the row being rejected and the typed reason. */
interface RejectDraft {
  readonly id: string;
  readonly reason: string;
}

function mutationErrorText(error: Error): string {
  return error instanceof ApiError && error.code !== null
    ? `${error.code}: ${error.message}`
    : error.message;
}

/**
 * `/say` moderation queue. Drives the pinned SPEC §5 contract:
 * `GET /api/v0/admin/say-messages?status=`, `POST .../approve`, `POST .../reject`.
 * Pending rows can be approved (exact `{}`) or rejected with a trimmed 1–500 char reason;
 * a successful mutation (`204`) refetches the active tab.
 */
export function SayModerationPage(): ReactElement {
  const [status, setStatus] = useState<SuperChatStatus>('pending');
  const [reloadKey, setReloadKey] = useState(0);
  const [draft, setDraft] = useState<RejectDraft | null>(null);

  const load = useCallback((): Promise<AdminSayMessage[]> => getSayMessages(status), [status]);
  const resource = useApiResource(load, reloadKey);
  const mutation = useApiMutation();

  const busy = mutation.status === 'pending';
  const refresh = (): void => setReloadKey((n) => n + 1);

  const selectStatus = (next: SuperChatStatus): void => {
    setStatus(next);
    setDraft(null);
    mutation.reset();
  };

  const approve = (id: string): void => {
    mutation.run(() => approveSayMessage(id), refresh);
  };

  const submitReject = (id: string, reason: string): void => {
    const trimmed = reason.trim();
    if (trimmed.length < REJECT_REASON_MIN || trimmed.length > REJECT_REASON_MAX) {
      return;
    }
    mutation.run(() => rejectSayMessage(id, trimmed), () => {
      setDraft(null);
      refresh();
    });
  };

  return (
    <section>
      <h2 style={{ fontSize: '16px' }}>Say moderation</h2>
      <p style={{ fontSize: '12px', opacity: 0.7 }}>
        Approve or reject paid <code>/say</code> messages. Only <code>pending</code> messages are
        actionable; approved ones show on the stage, rejected ones stay hidden.
      </p>

      <div role="tablist" aria-label="Moderation status" style={{ display: 'flex', gap: '6px', margin: '10px 0' }}>
        {STATUS_TABS.map((tab) => (
          <button
            key={tab}
            type="button"
            role="tab"
            aria-selected={tab === status}
            onClick={() => selectStatus(tab)}
            style={{
              padding: '5px 12px',
              borderRadius: '6px',
              border: '1px solid #ccc',
              cursor: 'pointer',
              background: tab === status ? '#123' : '#fff',
              color: tab === status ? '#fff' : '#123',
              textTransform: 'capitalize',
            }}
          >
            {tab}
          </button>
        ))}
      </div>

      {mutation.status === 'error' && mutation.error !== null && (
        <p role="alert" style={{ color: '#b00020', fontSize: '13px' }}>
          Moderation failed: {mutationErrorText(mutation.error)}
        </p>
      )}

      <ResourceView resource={resource}>
        {(messages) =>
          messages.length === 0 ? (
            <p style={{ opacity: 0.7 }}>No {status} messages.</p>
          ) : (
            <ul style={{ listStyle: 'none', padding: 0, margin: 0, maxWidth: '680px' }}>
              {messages.map((message) => (
                <li
                  key={message.id}
                  style={{ border: '1px solid #e5e5e5', borderRadius: '8px', padding: '12px 14px', marginBottom: '10px' }}
                >
                  <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                    {message.color !== null && (
                      <span
                        aria-hidden="true"
                        style={{
                          width: '12px',
                          height: '12px',
                          borderRadius: '50%',
                          background: message.color,
                          display: 'inline-block',
                        }}
                      />
                    )}
                    <strong>{message.displayName}</strong>
                    <span style={{ opacity: 0.7 }}>{formatStars(message.amountStars)} ⭐</span>
                    <span style={{ marginLeft: 'auto', fontSize: '12px', opacity: 0.6 }}>
                      {message.submittedAtUtc === null ? '—' : formatUtcTime(message.submittedAtUtc)}
                    </span>
                  </div>
                  <p style={{ margin: '8px 0 0' }}>{message.text}</p>
                  {message.status === 'rejected' && message.moderationReason !== null && (
                    <p style={{ margin: '6px 0 0', fontSize: '12px', color: '#b00020' }}>
                      Reason: {message.moderationReason}
                    </p>
                  )}

                  {message.status === 'pending' && (
                    <div style={{ marginTop: '10px' }}>
                      {draft?.id === message.id ? (
                        <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                          <textarea
                            aria-label={`Rejection reason for ${message.displayName}`}
                            value={draft.reason}
                            maxLength={REJECT_REASON_MAX}
                            onChange={(event) => setDraft({ id: message.id, reason: event.target.value })}
                            rows={2}
                            style={{ padding: '6px', resize: 'vertical' }}
                          />
                          <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
                            <button
                              type="button"
                              disabled={busy || draft.reason.trim().length < REJECT_REASON_MIN}
                              onClick={() => submitReject(message.id, draft.reason)}
                              style={{ padding: '5px 12px' }}
                            >
                              Confirm reject
                            </button>
                            <button type="button" disabled={busy} onClick={() => setDraft(null)} style={{ padding: '5px 12px' }}>
                              Cancel
                            </button>
                            <span style={{ fontSize: '11px', opacity: 0.6 }}>
                              {draft.reason.trim().length}/{REJECT_REASON_MAX}
                            </span>
                          </div>
                        </div>
                      ) : (
                        <div style={{ display: 'flex', gap: '8px' }}>
                          <button type="button" disabled={busy} onClick={() => approve(message.id)} style={{ padding: '5px 12px' }}>
                            Approve
                          </button>
                          <button
                            type="button"
                            disabled={busy}
                            onClick={() => setDraft({ id: message.id, reason: '' })}
                            style={{ padding: '5px 12px' }}
                          >
                            Reject
                          </button>
                        </div>
                      )}
                    </div>
                  )}
                </li>
              ))}
            </ul>
          )
        }
      </ResourceView>
    </section>
  );
}
