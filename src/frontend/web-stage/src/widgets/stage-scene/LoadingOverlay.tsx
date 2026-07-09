import type { CSSProperties, ReactElement } from 'react';

// The `web1radio.exe` Win9x loading window, shown until the Three.js scene reports its first
// rendered frame. Markup + styles recreated from the mock's `notReady` block.

const backdrop: CSSProperties = {
  position: 'fixed',
  inset: 0,
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  zIndex: 9,
  fontFamily: "'Tahoma', sans-serif",
};

const windowFrame: CSSProperties = {
  background: '#c3c7cb',
  boxShadow:
    'inset -1px -1px 0 #000, inset 1px 1px 0 #dfe3e6, inset -2px -2px 0 #85898c, inset 2px 2px 0 #fff',
  padding: 3,
  width: 280,
};

const titleBar: CSSProperties = {
  background: 'linear-gradient(90deg,#000080,#1084d0)',
  color: '#fff',
  fontWeight: 'bold',
  fontSize: 12,
  padding: '3px 5px',
};

const body: CSSProperties = { padding: '16px 14px', fontSize: 12, color: '#000' };

const progressTrack: CSSProperties = {
  height: 16,
  background: '#fff',
  boxShadow: 'inset 1px 1px 0 #85898c, inset -1px -1px 0 #dfe3e6',
  padding: 2,
  overflow: 'hidden',
};

const progressFill: CSSProperties = {
  height: '100%',
  width: '60%',
  background: 'repeating-linear-gradient(90deg,#000080 0 10px,transparent 10px 13px)',
};

export function LoadingOverlay(): ReactElement {
  return (
    <div style={backdrop}>
      <div style={windowFrame}>
        <div style={titleBar}>web1radio.exe</div>
        <div style={body}>
          <div style={{ marginBottom: 9 }}>Загрузка сцены…</div>
          <div style={progressTrack}>
            <div style={progressFill} />
          </div>
        </div>
      </div>
    </div>
  );
}
