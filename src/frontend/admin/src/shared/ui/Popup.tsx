import type { ReactElement, ReactNode } from 'react';

interface PopupProps {
  readonly title: string;
  readonly onClose: () => void;
  readonly children: ReactNode;
  readonly width?: number;
}

/**
 * Modal popup: dim overlay (click-to-close) holding a shared 7.css Aero window
 * with a close control in its title bar. Body layout is left to the caller.
 */
export function Popup({ title, onClose, children, width = 480 }: PopupProps): ReactElement {
  return (
    <div
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(10,40,55,.35)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 50,
      }}
      onClick={onClose}
    >
      <div
        className="window glass active"
        style={{ width: `${width}px`, maxWidth: '92vw' }}
        onClick={(event) => event.stopPropagation()}
      >
        <div className="title-bar">
          <div className="title-bar-text">{title}</div>
          <div className="title-bar-controls">
            <button type="button" aria-label="Close" onClick={onClose} />
          </div>
        </div>
        <div className="window-body">{children}</div>
      </div>
    </div>
  );
}
