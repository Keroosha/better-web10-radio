import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import type { AdminSayMessage } from '@web10/shared';

import { SayModerationPage } from './SayModerationPage';

function jsonResponse<TBody>(body: TBody): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function problemResponse(status: number, code: string, message: string): Response {
  return new Response(JSON.stringify({ status, code, message, traceId: 't' }), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

const pending: AdminSayMessage = {
  id: 'msg-1',
  telegramUserId: 7,
  displayName: 'CyberDove',
  text: 'greetings from the queue',
  amountStars: 50,
  color: '#33ccff',
  status: 'pending',
  submittedAtUtc: '2026-07-10T12:00:00Z',
  paidAtUtc: '2026-07-10T12:00:05Z',
  moderatedAtUtc: null,
  moderationReason: null,
};

interface Call {
  readonly url: string;
  readonly method: string;
  readonly body: string | null;
}

/** A fetch stub: GET returns the current queue; any POST clears it (moderated → leaves pending). */
function stubModerationFetch(initial: AdminSayMessage[]): Call[] {
  let queue = [...initial];
  const calls: Call[] = [];
  vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    const body = typeof init?.body === 'string' ? init.body : null;
    calls.push({ url, method, body });
    if (method === 'POST') {
      queue = [];
      return Promise.resolve(new Response(null, { status: 204 }));
    }
    return Promise.resolve(jsonResponse(queue));
  });
  return calls;
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('renders the pending queue from the status GET', async () => {
  const calls = stubModerationFetch([pending]);
  render(<SayModerationPage />);

  await waitFor(() => expect(screen.getByText('CyberDove')).toBeTruthy());
  expect(screen.getByText('greetings from the queue')).toBeTruthy();
  expect(calls[0]?.url).toBe('/api/v0/admin/say-messages?status=pending');
});

test('empty queue shows the empty state', async () => {
  stubModerationFetch([]);
  render(<SayModerationPage />);

  await waitFor(() => expect(screen.getByText('No pending messages.')).toBeTruthy());
});

test('approve POSTs {} to the approve route and refetches', async () => {
  const calls = stubModerationFetch([pending]);
  render(<SayModerationPage />);

  await waitFor(() => expect(screen.getByText('CyberDove')).toBeTruthy());
  fireEvent.click(screen.getByText('Approve'));

  await waitFor(() => expect(screen.getByText('No pending messages.')).toBeTruthy());
  const approve = calls.find((c) => c.method === 'POST');
  expect(approve?.url).toBe('/api/v0/admin/say-messages/msg-1/approve');
  expect(approve?.body).toBe('{}');
});

test('reject requires a reason then POSTs it', async () => {
  const calls = stubModerationFetch([pending]);
  render(<SayModerationPage />);

  await waitFor(() => expect(screen.getByText('CyberDove')).toBeTruthy());
  fireEvent.click(screen.getByText('Reject'));

  const confirm = screen.getByText('Confirm reject');
  expect(confirm.hasAttribute('disabled')).toBe(true); // empty reason blocked

  fireEvent.change(screen.getByLabelText('Rejection reason for CyberDove'), {
    target: { value: 'off-topic' },
  });
  fireEvent.click(screen.getByText('Confirm reject'));

  await waitFor(() => expect(screen.getByText('No pending messages.')).toBeTruthy());
  const reject = calls.find((c) => c.method === 'POST');
  expect(reject?.url).toBe('/api/v0/admin/say-messages/msg-1/reject');
  expect(reject?.body).toBe(JSON.stringify({ reason: 'off-topic' }));
});

test('surfaces a moderation conflict error with its code', async () => {
  vi.stubGlobal('fetch', (_url: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    if (method === 'POST') {
      return Promise.resolve(problemResponse(409, 'say.state_conflict', 'Already moderated'));
    }
    // GET keeps the row present so its Approve button stays available.
    return Promise.resolve(jsonResponse([pending]));
  });

  render(<SayModerationPage />);
  await waitFor(() => expect(screen.getByText('Approve')).toBeTruthy());
  fireEvent.click(screen.getByText('Approve'));

  await waitFor(() =>
    expect(screen.getByRole('alert').textContent).toContain('say.state_conflict'),
  );
});
