import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';

import { DonationGoalPage } from './DonationGoalPage';

const goal = {
  title: 'Сбор на сервер',
  raisedStars: 1200,
  goalStars: 5000,
  topDonator: { displayName: 'CyberDove', amountStars: 500 },
  recent: [],
};

beforeEach(() => {
  vi.stubGlobal('fetch', () =>
    Promise.resolve(
      new Response(JSON.stringify(goal), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  );
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('renders the donation goal returned by the admin GET', async () => {
  render(<DonationGoalPage />);
  await waitFor(() => expect(screen.getByText('Сбор на сервер')).toBeTruthy());
  expect(screen.getByText(/CyberDove \(500 ⭐\)/)).toBeTruthy();
  expect(screen.getByText(/1,200 \/ 5,000/)).toBeTruthy();
});
