import type { CSSProperties, ReactElement } from 'react';

import { formatStars, type DonationGoal } from '@web10/shared';

import { OverlayWindow } from '../../shared/ui/OverlayWindow';
import type { StageTheme } from '../../shared/ui/theme';

interface DonationGoalWidgetProps {
  readonly donationGoal: DonationGoal;
  /** Progress percentage (0–100) from `selectDonationPercent`. */
  readonly percent: number;
  readonly theme: StageTheme;
  /** `{...theme.win, ...getBannerPositionStyle(banner.screenPosition)}`. */
  readonly windowStyle: CSSProperties;
  /** Title-bar caption; defaults to `DONATION GOAL` when the banner omits it. */
  readonly title?: string;
}

const vt323: CSSProperties = { fontFamily: "'VT323',monospace", lineHeight: 1 };

/**
 * DONATION GOAL widget (mock L74-106): top donator, raised/goal, a progress bar, and a
 * recent-donations list. Renders empty-safe — no top donator shows "—" and an empty
 * recent list is simply omitted.
 */
export function DonationGoalWidget({
  donationGoal,
  percent,
  theme,
  windowStyle,
  title,
}: DonationGoalWidgetProps): ReactElement {
  const { topDonator, raisedStars, goalStars, recent } = donationGoal;

  return (
    <OverlayWindow title={title ?? 'DONATION GOAL'} theme={theme} windowStyle={windowStyle}>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: '10px',
          marginBottom: '11px',
          padding: '8px 10px',
          borderRadius: '10px',
          background: theme.hiRow,
        }}
      >
        <div
          aria-hidden="true"
          style={{
            width: '38px',
            height: '38px',
            flex: 'none',
            borderRadius: '9px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            fontWeight: 900,
            fontSize: '17px',
            color: '#fff',
            background: 'linear-gradient(150deg,#ffd76a,#ff8f3c)',
            boxShadow: '0 2px 7px rgba(220,130,40,0.4)',
          }}
        >
          ★
        </div>
        <div style={{ minWidth: 0 }}>
          <div style={{ fontSize: '9px', fontWeight: 700, letterSpacing: '0.13em', opacity: 0.6 }}>
            TOP DONATOR
          </div>
          <div
            style={{
              fontWeight: 700,
              fontSize: '14px',
              whiteSpace: 'nowrap',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
            }}
          >
            {topDonator === null ? '—' : topDonator.displayName}
          </div>
        </div>
        <div style={{ marginLeft: 'auto', fontSize: '26px', color: theme.accent, ...vt323 }}>
          {topDonator === null ? '0' : formatStars(topDonator.amountStars)}
        </div>
      </div>

      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'baseline',
          marginBottom: '5px',
        }}
      >
        <span style={{ fontSize: '24px', ...vt323 }}>{formatStars(raisedStars)}</span>
        <span style={{ fontSize: '11px', opacity: 0.7 }}>цель {formatStars(goalStars)}</span>
      </div>
      <div style={theme.barTrack}>
        <div style={{ ...theme.barFill, width: `${percent}%` }} />
      </div>
      <div style={{ textAlign: 'right', fontSize: '10px', opacity: 0.65, marginTop: '4px' }}>
        {Math.round(percent)}% собрано
      </div>

      {recent.length > 0 && (
        <div style={{ marginTop: '11px', display: 'flex', flexDirection: 'column', gap: '5px' }}>
          {recent.map((entry) => (
            <div
              key={entry.id}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: '8px',
                padding: '5px 9px',
                borderRadius: '8px',
                background: theme.row,
                fontSize: '12px',
                animation: 'floatin 0.4s ease',
              }}
            >
              <span
                aria-hidden="true"
                style={{
                  width: '6px',
                  height: '6px',
                  borderRadius: '50%',
                  background: theme.accent,
                  flex: 'none',
                }}
              />
              <span style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {entry.displayName}
              </span>
              <span style={{ marginLeft: 'auto', fontSize: '17px', color: theme.accent, ...vt323 }}>
                {formatStars(entry.amountStars)}
              </span>
            </div>
          ))}
        </div>
      )}
    </OverlayWindow>
  );
}
