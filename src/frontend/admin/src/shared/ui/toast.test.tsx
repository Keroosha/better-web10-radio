import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { ReactElement } from 'react';
import { afterEach, expect, test, vi } from 'vitest';

import { ToastProvider, useToast } from './toast';

function ToastTrigger(): ReactElement {
  const { showToast } = useToast();
  return <button type="button" onClick={() => showToast('Сохранено')}>Показать уведомление</button>;
}

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

test('positions the dismissible toast host at the lower-right above the transport bar', () => {
  render(
    <ToastProvider>
      <ToastTrigger />
    </ToastProvider>,
  );

  fireEvent.click(screen.getByRole('button', { name: 'Показать уведомление' }));
  const host = screen.getByRole('status');

  expect(host.style.position).toBe('fixed');
  expect(host.style.right).toBe('24px');
  expect(host.style.bottom).toBe('84px');
  expect(host.style.left).toBe('');
  expect(host.style.transform).toBe('');

  fireEvent.click(screen.getByRole('tooltip'));
  expect(screen.queryByRole('status')).toBeNull();
});

test('dismisses a toast after the replacement timer expires', () => {
  vi.useFakeTimers();
  render(
    <ToastProvider>
      <ToastTrigger />
    </ToastProvider>,
  );

  fireEvent.click(screen.getByRole('button', { name: 'Показать уведомление' }));
  act(() => vi.advanceTimersByTime(2200));
  expect(screen.queryByRole('status')).toBeNull();
});
