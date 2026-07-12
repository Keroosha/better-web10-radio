import type { CSSProperties, ReactElement, ReactNode } from 'react';

interface PopupProps {
  readonly title: string;
  readonly onClose: () => void;
  readonly children: ReactNode;
  readonly width?: number;
  /** Warning-coloured title bar for destructive/dependency confirmations (ПРАВИЛА §6). */
  readonly warning?: boolean;
}

/**
 * Modal popup: dim overlay (click-to-close) holding a plain 7.css `.window.active`
 * with a close control in its title bar. Body layout is left to the caller.
 */
export function Popup({ title, onClose, children, width = 480, warning = false }: PopupProps): ReactElement {
  const titleBarStyle: CSSProperties | undefined = warning ? { ['--w7-w-bg' as string]: '#b5651d' } : undefined;
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
        className="window active"
        style={{ width: `${width}px`, maxWidth: '92vw' }}
        onClick={(event) => event.stopPropagation()}
      >
        <div className="title-bar" style={titleBarStyle}>
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
