import type { CSSProperties, ReactElement } from 'react';

import { formatStars, type SuperChatMessage } from '@web10/shared';

import { OverlayWindow } from '../../shared/ui/OverlayWindow';
import type { StageTheme } from '../../shared/ui/theme';

interface SuperChatWidgetProps {
  /** Admin-configured banner title; falls back for direct widget use. */
  readonly title?: string;
  /** Already filtered to approved + capped to the layout limit (`selectApprovedMessages`). */
  readonly messages: readonly SuperChatMessage[];
  readonly theme: StageTheme;
  readonly windowStyle: CSSProperties;
}

/**
 * SUPER CHAT widget (mock L108-124): paid `/say` messages shown as coloured cards
 * (header tinted by the message colour, then the text). Only approved messages reach
 * this widget (enforced upstream by `selectApprovedMessages`). Empty → a quiet placeholder.
 */
export function SuperChatWidget({ title, messages, theme, windowStyle }: SuperChatWidgetProps): ReactElement {
  return (
    <OverlayWindow title={title ?? 'SUPER CHAT'} theme={theme} windowStyle={windowStyle}>
      {messages.length === 0 ? (
        <div />
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '7px' }}>
          {messages.map((message) => (
            <div
              key={message.id}
              style={{
                borderRadius: '10px',
                overflow: 'hidden',
                background: theme.row,
                animation: 'floatin 0.45s ease',
                boxShadow: '0 1px 4px rgba(0,0,0,0.08)',
              }}
            >
              <div
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: '7px',
                  padding: '5px 9px',
                  background: message.color,
                  color: '#fff',
                }}
              >
                <span
                  style={{
                    fontWeight: 700,
                    fontSize: '12px',
                    whiteSpace: 'nowrap',
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                  }}
                >
                  {message.displayName}
                </span>
                <span
                  style={{
                    marginLeft: 'auto',
                    fontFamily: "'VT323',monospace",
                    fontSize: '18px',
                    lineHeight: 1,
                    textShadow: '0 1px 1px rgba(0,0,0,0.25)',
                  }}
                >
                  {formatStars(message.amountStars)}
                </span>
              </div>
              <div style={{ padding: '6px 10px 8px', fontSize: '12px', lineHeight: 1.35 }}>
                {message.text}
              </div>
            </div>
          ))}
        </div>
      )}
    </OverlayWindow>
  );
}
