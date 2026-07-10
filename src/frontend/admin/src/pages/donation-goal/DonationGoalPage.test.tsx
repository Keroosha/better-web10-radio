import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import { DonationGoalPage } from './DonationGoalPage';

const goal = {
  title: 'Сбор на сервер',
  raisedStars: 1200,
  goalStars: 5000,
  topDonator: { displayName: 'CyberDove', amountStars: 500 },
  recent: [],
};

interface RequestCall {
  readonly method: string;
  readonly body: string | null;
}

function json(body: object, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('rejects a blank title and non-positive goal before attempting the update', async () => {
  const calls: RequestCall[] = [];
  vi.stubGlobal('fetch', (_input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : null,
    });
    return Promise.resolve(json(goal));
  });

  render(<DonationGoalPage />);
  await waitFor(() => expect(screen.getByDisplayValue('Сбор на сервер')).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Goal title'), { target: { value: '   ' } });
  fireEvent.change(screen.getByLabelText('Goal in Stars'), { target: { value: '0' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save goal' }));

  expect(screen.getByRole('alert').textContent).toContain('title');
  expect(calls.filter((call) => call.method === 'PUT')).toHaveLength(0);
});

test('updates the title and positive star target while preserving the raised amount returned by the server', async () => {
  const calls: RequestCall[] = [];
  const updated = { ...goal, title: 'Новый сервер', goalStars: 7500 };
  vi.stubGlobal('fetch', (_input: RequestInfo | URL, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    calls.push({ method, body: typeof init?.body === 'string' ? init.body : null });
    return Promise.resolve(method === 'PUT' ? json(updated) : json(goal));
  });

  render(<DonationGoalPage />);
  await waitFor(() => expect(screen.getByDisplayValue('Сбор на сервер')).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Goal title'), { target: { value: 'Новый сервер' } });
  fireEvent.change(screen.getByLabelText('Goal in Stars'), { target: { value: '7500' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save goal' }));

  await waitFor(() => expect(screen.getByText(/1,200 \/ 7,500/)).toBeTruthy());
  const update = calls.find((call) => call.method === 'PUT');
  expect(update?.body).toBe(JSON.stringify({ title: 'Новый сервер', goalStars: 7500 }));
  expect(screen.getByText('Saved')).toBeTruthy();
});

test('surfaces a failed goal update and keeps the editor available for correction', async () => {
  vi.stubGlobal('fetch', (_input: RequestInfo | URL, init?: RequestInit) => {
    if ((init?.method ?? 'GET') === 'PUT') {
      return Promise.resolve(json({ status: 409, code: 'donation.goal.conflict', message: 'Concurrent update' }, 409));
    }
    return Promise.resolve(json(goal));
  });

  render(<DonationGoalPage />);
  await waitFor(() => expect(screen.getByDisplayValue('Сбор на сервер')).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Goal title'), { target: { value: 'Новый сервер' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save goal' }));

  await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('donation.goal.conflict'));
  expect(screen.getByLabelText('Goal title')).toBeTruthy();
});

test('prevents a duplicate goal update while the first request is pending', async () => {
  const updated = { ...goal, title: 'Новый сервер', goalStars: 7500 };
  const updateResolver: { current: ((response: Response) => void) | null } = { current: null };
  const pendingUpdate = new Promise<Response>((resolve) => {
    updateResolver.current = resolve;
  });
  let updateCalls = 0;
  vi.stubGlobal('fetch', (_input: RequestInfo | URL, init?: RequestInit) => {
    if ((init?.method ?? 'GET') === 'PUT') {
      updateCalls += 1;
      return pendingUpdate;
    }
    return Promise.resolve(json(goal));
  });

  render(<DonationGoalPage />);
  await screen.findByDisplayValue('Сбор на сервер');
  fireEvent.change(screen.getByLabelText('Goal title'), { target: { value: 'Новый сервер' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save goal' }));

  await waitFor(() => expect(screen.getByRole('button', { name: 'Saving…' }).hasAttribute('disabled')).toBe(true));
  fireEvent.click(screen.getByRole('button', { name: 'Saving…' }));
  expect(updateCalls).toBe(1);
  if (updateResolver.current === null) {
    throw new Error('The save action did not create a pending request.');
  }
  updateResolver.current(json(updated));
  await waitFor(() => expect(screen.getByText('Saved')).toBeTruthy());
});
