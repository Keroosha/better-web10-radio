import { cleanup, render, screen, fireEvent } from '@testing-library/react';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';

import { App } from './App';

// Admin pages fetch via the shared client; stub fetch so the shell mounts offline.
beforeEach(() => {
  vi.stubGlobal('fetch', () => Promise.reject(new Error('no network in test')));
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test('admin shows the auth gate before a token is entered', () => {
  render(<App />);
  expect(screen.getByLabelText('admin bearer token')).toBeTruthy();
  // The cabinet is not rendered yet.
  expect(screen.queryByRole('heading', { name: 'Dashboard' })).toBeNull();
});

test('entering a token reveals the FSD cabinet with nav and dashboard', () => {
  render(<App />);
  fireEvent.change(screen.getByLabelText('admin bearer token'), { target: { value: 'tok' } });
  fireEvent.click(screen.getByText('Enter'));

  // The dashboard heading and an unpinned nav entry are both present.
  expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeTruthy();
  expect(screen.getByText('Say moderation')).toBeTruthy();
});
