import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import type { Banner, DonationGoal, FetchImpl } from '@web10/shared';

import { ToastProvider } from '../../shared/ui/toast';
import { BannersPage } from './BannersPage';

interface CapturedRequest {
  readonly url: string;
  readonly init: RequestInit | undefined;
}

const superChatBanner: Banner = {
  id: '01920000-0000-7000-8000-0000000000b1',
  type: 'superchat',
  title: 'SUPER CHAT',
  subtitle: '',
  text: '',
  style: 'aero',
  screenPosition: 'bottom-left',
  accent: '#e0439a',
  enabled: true,
  sortOrder: 0,
  rotationSeconds: 0,
};

const donationBanner: Banner = {
  id: '01920000-0000-7000-8000-0000000000b2',
  type: 'donation',
  title: 'DONATION GOAL',
  subtitle: '',
  text: '',
  style: 'aero',
  screenPosition: 'top-left',
  accent: '#2ecc71',
  enabled: true,
  sortOrder: 0,
  rotationSeconds: 0,
};

const donationGoal: DonationGoal = {
  title: 'Сбор на эфир',
  raisedStars: 125,
  goalStars: 5000,
  topDonator: { displayName: 'CyberDove', amountStars: 100 },
  recent: [],
};

function json(body: object | readonly object[]): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function renderPage(fetchImpl: FetchImpl): void {
  vi.stubGlobal('fetch', fetchImpl);
  render(
    <ToastProvider>
      <BannersPage />
    </ToastProvider>,
  );
}

function isPut(request: CapturedRequest, path: string): boolean {
  return request.url === path && request.init?.method === 'PUT';
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('keeps superchat visibility as a local draft until banner save', async () => {
  const requests: CapturedRequest[] = [];
  const disabled = { ...superChatBanner, enabled: false };
  const fetchImpl: FetchImpl = async (url, init) => {
    requests.push({ url, init });
    if (url === '/api/v0/admin/banners' && init?.method === 'GET') {
      return json([superChatBanner]);
    }
    if (url === '/api/v0/admin/banners' && init?.method === 'PUT') {
      return json([disabled]);
    }
    return new Response(null, { status: 404 });
  };

  renderPage(fetchImpl);

  const visibility = await screen.findByLabelText('Показывать на стриме');
  fireEvent.click(visibility);
  expect(requests.filter((request) => isPut(request, '/api/v0/admin/banners'))).toHaveLength(0);

  fireEvent.click(screen.getByRole('button', { name: 'Сохранить баннер' }));
  await waitFor(() => expect(requests.filter((request) => isPut(request, '/api/v0/admin/banners'))).toHaveLength(1));

  const replacement = requests.find((request) => isPut(request, '/api/v0/admin/banners'));
  expect(replacement?.init?.body).toBe(
    JSON.stringify([
      {
        id: superChatBanner.id,
        type: 'superchat',
        title: 'SUPER CHAT',
        subtitle: null,
        text: null,
        style: 'aero',
        screenPosition: 'bottom-left',
        accent: '#e0439a',
        enabled: false,
        rotationSeconds: null,
      },
    ]),
  );
});

test('edits the donation goal inside the selected donation banner without replacing banners', async () => {
  const requests: CapturedRequest[] = [];
  const updatedGoal = { ...donationGoal, title: 'Новая цель', goalStars: 7500 };
  const fetchImpl: FetchImpl = async (url, init) => {
    requests.push({ url, init });
    if (url === '/api/v0/admin/banners' && init?.method === 'GET') {
      return json([donationBanner]);
    }
    if (url === '/api/v0/admin/donation-goal' && init?.method === 'GET') {
      return json(donationGoal);
    }
    if (url === '/api/v0/admin/donation-goal' && init?.method === 'PUT') {
      return json(updatedGoal);
    }
    return new Response(null, { status: 404 });
  };

  renderPage(fetchImpl);

  const title = await screen.findByLabelText('Цель (текст)');
  fireEvent.change(title, { target: { value: '  Новая цель  ' } });
  fireEvent.change(screen.getByLabelText('Цель (⭐, число)'), { target: { value: '7500' } });
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить цель' }));

  await waitFor(() => expect(requests.filter((request) => isPut(request, '/api/v0/admin/donation-goal'))).toHaveLength(1));
  const update = requests.find((request) => isPut(request, '/api/v0/admin/donation-goal'));
  expect(update?.init?.body).toBe(JSON.stringify({ title: 'Новая цель', goalStars: 7500 }));
  expect(requests.filter((request) => isPut(request, '/api/v0/admin/banners'))).toHaveLength(0);
});

test('blocks invalid donation goals without issuing a goal PUT', async () => {
  const requests: CapturedRequest[] = [];
  const fetchImpl: FetchImpl = async (url, init) => {
    requests.push({ url, init });
    if (url === '/api/v0/admin/banners' && init?.method === 'GET') {
      return json([donationBanner]);
    }
    if (url === '/api/v0/admin/donation-goal' && init?.method === 'GET') {
      return json(donationGoal);
    }
    return new Response(null, { status: 404 });
  };

  renderPage(fetchImpl);

  const target = await screen.findByLabelText('Цель (⭐, число)');
  fireEvent.change(target, { target: { value: 'not-a-number' } });
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить цель' }));

  await waitFor(() => expect(screen.getByText('Заполните текст и цель (≥1 ⭐)')).toBeTruthy());
  expect(requests.filter((request) => isPut(request, '/api/v0/admin/donation-goal'))).toHaveLength(0);
});
