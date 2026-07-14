import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';

import type { FetchImpl } from '@web10/shared';

import { ToastProvider } from '../../shared/ui/toast';
import { PlaylistsPage } from './PlaylistsPage';

function json(body: object | readonly object[]): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('renders all playlist policy checkboxes as independently toggleable labeled controls', async () => {
  const fetchImpl: FetchImpl = async (url, init) => {
    if (url === '/api/v0/admin/playlists' && init?.method === 'GET') return json([]);
    return new Response(null, { status: 404 });
  };
  vi.stubGlobal('fetch', fetchImpl);

  render(
    <ToastProvider>
      <PlaylistsPage />
    </ToastProvider>,
  );

  fireEvent.click(screen.getByRole('button', { name: '＋ Новый' }));

  for (const label of ['в ротации эфира', 'Джингл', 'Прерывать трек', 'Избегать повторов']) {
    const checkbox = await screen.findByLabelText(label);
    if (!(checkbox instanceof HTMLInputElement)) throw new Error(`Expected ${label} to label a checkbox input.`);
    const before = checkbox.checked;
    fireEvent.click(checkbox);
    expect(checkbox.checked).toBe(!before);
  }
});
