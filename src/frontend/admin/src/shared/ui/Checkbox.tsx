import { type ChangeEvent, type CSSProperties, type ReactElement } from 'react';

interface CheckboxProps {
  readonly id: string;
  readonly label: string;
  readonly checked: boolean;
  readonly onChange: (event: ChangeEvent<HTMLInputElement>) => void;
  readonly disabled?: boolean;
  readonly labelStyle?: CSSProperties;
}

/** 7.css checkbox semantic pair: its adjacent label renders the visible control. */
export function Checkbox({ id, label, checked, onChange, disabled = false, labelStyle }: CheckboxProps): ReactElement {
  return (
    <>
      <input id={id} type="checkbox" checked={checked} onChange={onChange} disabled={disabled} />
      <label htmlFor={id} style={labelStyle}>
        {label}
      </label>
    </>
  );
}
