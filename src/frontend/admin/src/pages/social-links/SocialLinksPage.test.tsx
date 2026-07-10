import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import { SocialLinksPage } from './SocialLinksPage';

const links = [
  {
    id: '018f0aaa-0000-7000-8000-000000000001',
    kind: 'telegram',
    name: 'Telegram',
    handle: '@web10',
    url: 'https://t.me/web10',
    glyph: '',
    color: '#1084d0',
    qrImageUrl: '',
    isFeatured: true,
  },
  {
    id: '018f0aaa-0000-7000-8000-000000000002',
    kind: 'youtube',
    name: 'YouTube',
    handle: '',
    url: 'https://youtube.com/@web10',
    glyph: '',
    color: '',
    qrImageUrl: '',
    isFeatured: false,
  },
];

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

test('shows the empty state when no social links are configured', async () => {
  vi.stubGlobal('fetch', () => Promise.resolve(json([])));

  render(<SocialLinksPage />);

  await waitFor(() => expect(screen.getByText('No social links configured.')).toBeTruthy());
  expect(screen.getByRole('button', { name: 'Add social link' })).toBeTruthy();
});

test('reorders edited links and replaces the canonical ordered collection', async () => {
  const calls: RequestCall[] = [];
  const reordered = [
    { ...links[1], name: 'Video channel' },
    links[0],
  ];
  vi.stubGlobal('fetch', (_input: RequestInfo | URL, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    calls.push({ method, body: typeof init?.body === 'string' ? init.body : null });
    return Promise.resolve(method === 'PUT' ? json(reordered) : json(links));
  });

  render(<SocialLinksPage />);
  await waitFor(() => expect(screen.getByDisplayValue('Telegram')).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Name for Telegram'), { target: { value: 'Community chat' } });
  fireEvent.click(screen.getByRole('button', { name: 'Move Telegram down' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save social links' }));

  await waitFor(() => expect(screen.getByText('Saved')).toBeTruthy());
  const replacement = calls.find((call) => call.method === 'PUT');
  expect(replacement?.body).toBe(
    JSON.stringify([
      {
        id: links[1]!.id,
        kind: 'youtube',
        name: 'YouTube',
        handle: null,
        url: 'https://youtube.com/@web10',
        glyph: null,
        color: null,
        qrImageUrl: null,
        isFeatured: false,
      },
      {
        id: links[0]!.id,
        kind: 'telegram',
        name: 'Community chat',
        handle: '@web10',
        url: 'https://t.me/web10',
        glyph: null,
        color: '#1084d0',
        qrImageUrl: null,
        isFeatured: true,
      },
    ]),
  );
  const rows = screen.getAllByRole('row');
  expect(rows[1]?.textContent).toContain('Video channel');
  expect(rows[2]?.textContent).toContain('Telegram');
});

test('removes a drafted link without sending an invalid replacement', async () => {
  const calls: RequestCall[] = [];
  vi.stubGlobal('fetch', (_input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : null,
    });
    return Promise.resolve(json(links));
  });

  render(<SocialLinksPage />);
  await waitFor(() => expect(screen.getByRole('button', { name: 'Add social link' })).toBeTruthy());
  fireEvent.click(screen.getByRole('button', { name: 'Add social link' }));
  fireEvent.click(screen.getByRole('button', { name: 'Remove new social link' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save social links' }));

  await waitFor(() => expect(screen.getByText('Saved')).toBeTruthy());
  const replacement = calls.find((call) => call.method === 'PUT');
  expect(replacement?.body).toBe(
    JSON.stringify([
      { ...links[0], glyph: null, qrImageUrl: null },
      { ...links[1], handle: null, glyph: null, color: null, qrImageUrl: null },
    ]),
  );
});

test('surfaces invalid URL feedback instead of sending a malformed social replacement', async () => {
  const calls: RequestCall[] = [];
  vi.stubGlobal('fetch', (_input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : null,
    });
    return Promise.resolve(json(links));
  });

  render(<SocialLinksPage />);
  await waitFor(() => expect(screen.getByDisplayValue('https://t.me/web10')).toBeTruthy());
  fireEvent.change(screen.getByLabelText('URL for Telegram'), { target: { value: 'not-a-url' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save social links' }));

  expect(screen.getByRole('alert').textContent).toContain('http');
  expect(calls.filter((call) => call.method === 'PUT')).toHaveLength(0);
});
