import { fireEvent, render, screen } from '@testing-library/react';
import { expect, test, vi } from 'vitest';

import { Popup } from './Popup';

test('uses shared Aero window chrome and closes only from the overlay or title control', () => {
  const onClose = vi.fn();
  const { container } = render(
    <Popup title="⚠ Подтверждение удаления" onClose={onClose} width={560}>
      <button type="button">Содержимое</button>
    </Popup>,
  );

  const popup = container.querySelector('.window.glass.active');
  expect(popup).not.toBeNull();
  expect(popup?.querySelector('.title-bar-text')?.textContent).toBe('⚠ Подтверждение удаления');

  fireEvent.click(screen.getByRole('button', { name: 'Содержимое' }));
  expect(onClose).not.toHaveBeenCalled();

  fireEvent.click(screen.getByRole('button', { name: 'Close' }));
  expect(onClose).toHaveBeenCalledTimes(1);

  const overlay = container.firstElementChild;
  if (overlay === null) throw new Error('Expected popup overlay.');
  fireEvent.click(overlay);
  expect(onClose).toHaveBeenCalledTimes(2);
});
