import { fireEvent, render, screen } from '@testing-library/react';
import type { ChangeEvent } from 'react';
import { expect, test } from 'vitest';

import { Checkbox } from './Checkbox';

test('renders the adjacent 7.css input-label pair and dispatches the input change event', () => {
  const checkedValues: boolean[] = [];
  const onChange = (event: ChangeEvent<HTMLInputElement>): void => {
    checkedValues.push(event.target.checked);
  };
  const { container } = render(<Checkbox id="checkbox-contract" label="Показывать на стриме" checked={false} onChange={onChange} />);

  const input = screen.getByRole('checkbox', { name: 'Показывать на стриме' });
  if (!(input instanceof HTMLInputElement)) throw new Error('Expected checkbox input.');
  const label = screen.getByText('Показывать на стриме');

  expect(input.id).toBe('checkbox-contract');
  expect(input.checked).toBe(false);
  expect(input.nextElementSibling).toBe(label);
  expect(label.getAttribute('for')).toBe('checkbox-contract');
  expect(container.querySelectorAll('input[type="checkbox"] + label')).toHaveLength(1);

  fireEvent.click(input);
  expect(checkedValues).toEqual([true]);
});
