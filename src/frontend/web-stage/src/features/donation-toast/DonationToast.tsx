import type { CSSProperties, ReactElement } from 'react';

import { formatStars, type RecentDonation } from '@web10/shared';

import type { StageTheme } from '../../shared/ui/theme';
import { useDonationToast } from './useDonationToast';

interface DonationToastProps {
  /** The newest unseen donation from the stage-state hook (or `null`). */
  readonly newDonation: RecentDonation | null;
  readonly theme: StageTheme;
}

// Fixed wrapper owns horizontal centering (stable translateX), so the `toastpop`
// keyframe can animate translateY only without fighting the centering transform.
const wrapperStyle: CSSProperties = {
  position: 'fixed',
  bottom: '22px',
  left: '50%',
  transform: 'translateX(-50%)',
  zIndex: 20,
  pointerEvents: 'none',
};

const badgeStyle: CSSProperties = {
  width: '40px',
  height: '40px',
  flex: 'none',
  borderRadius: '10px',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  fontSize: '20px',
  fontWeight: 900,
  color: '#fff',
  background: 'linear-gradient(150deg,#ffd76a,#ff7a3c)',
  boxShadow: '0 3px 10px rgba(220,120,40,0.5)',
};

const eyebrowStyle: CSSProperties = {
  fontSize: '10px',
  fontWeight: 700,
  letterSpacing: '0.12em',
  opacity: 0.7,
};

/**
 * The "НОВЫЙ ДОНАТ" toast, shown for a few seconds when a `player.donation` SSE event
 * carries a donation we have not seen. Renders `null` when idle.
 */
export function DonationToast({ newDonation, theme }: DonationToastProps): ReactElement | null {
  const toast = useDonationToast(newDonation);
  if (toast === null) {
    return null;
  }

  const cardStyle: CSSProperties = {
    ...theme.win,
    padding: '12px 16px',
    animation: 'toastpop 0.5s ease both',
  };

  return (
    <div style={wrapperStyle}>
      <div style={cardStyle}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
          <div style={badgeStyle} aria-hidden="true">
            ♥
          </div>
          <div>
            <div style={eyebrowStyle}>НОВЫЙ ДОНАТ</div>
            <div style={{ fontWeight: 700, fontSize: '15px' }}>
              {toast.displayName} ·{' '}
              <span
                style={{ fontFamily: "'VT323',monospace", fontSize: '20px', color: theme.accent }}
              >
                {formatStars(toast.amountStars)}
              </span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
