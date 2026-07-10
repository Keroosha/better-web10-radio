import type { CSSProperties, ReactElement } from 'react';

import type { SocialLink } from '@web10/shared';

import { useSocialRotation } from '../../features/social-rotation';
import { OverlayWindow } from '../../shared/ui/OverlayWindow';
import type { StageTheme } from '../../shared/ui/theme';

interface FollowUsWidgetProps {
  readonly socials: readonly SocialLink[];
  readonly theme: StageTheme;
  /** `{...theme.win, ...layout.social}`. */
  readonly windowStyle: CSSProperties;
}

function glyphBox(social: SocialLink, size: number): CSSProperties {
  return {
    width: `${size}px`,
    height: `${size}px`,
    flex: 'none',
    borderRadius: '6px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontWeight: 900,
    color: '#fff',
    background: social.color,
  };
}

/**
 * FOLLOW US widget (mock L126-151): the featured social's QR plus its glyph/name/handle,
 * with a rotating "featured" slot and a strip of dimmed glyph chips. Renders nothing when
 * there are no socials (empty-state invariant). The rotation hook is always called so the
 * hook order stays stable.
 */
export function FollowUsWidget({ socials, theme, windowStyle }: FollowUsWidgetProps): ReactElement | null {
  const featuredIndex = useSocialRotation(socials.length);
  if (socials.length === 0) {
    return null;
  }
  const featured = socials[featuredIndex] ?? socials[0];
  if (featured === undefined) {
    return null;
  }

  return (
    <OverlayWindow title="FOLLOW US" theme={theme} windowStyle={windowStyle}>
      <div style={{ display: 'flex', gap: '11px', alignItems: 'center' }}>
        <div
          style={{
            width: '88px',
            height: '88px',
            flex: 'none',
            borderRadius: '10px',
            overflow: 'hidden',
            background: '#fff',
            boxShadow: 'inset 0 0 0 1px rgba(0,0,0,0.1), 0 2px 6px rgba(0,0,0,0.15)',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            fontSize: '10px',
            color: '#88919c',
          }}
        >
          {featured.qrImageUrl.trim() === '' ? (
            'QR'
          ) : (
            <img
              src={featured.qrImageUrl}
              alt={`QR for ${featured.name}`}
              style={{ width: '100%', height: '100%', objectFit: 'contain' }}
            />
          )}
        </div>
        <div style={{ minWidth: 0, flex: 1 }}>
          <div style={{ fontSize: '9px', fontWeight: 700, letterSpacing: '0.13em', opacity: 0.6 }}>
            СЕЙЧАС В ЭФИРЕ
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: '7px', marginTop: '3px' }}>
            <span style={{ ...glyphBox(featured, 22), fontSize: '12px' }}>{featured.glyph}</span>
            <div style={{ minWidth: 0 }}>
              <div style={{ fontWeight: 700, fontSize: '13px', lineHeight: 1.1 }}>
                {featured.name}
              </div>
              <div
                style={{
                  fontSize: '11px',
                  opacity: 0.7,
                  whiteSpace: 'nowrap',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                }}
              >
                {featured.handle}
              </div>
            </div>
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px', marginTop: '9px' }}>
            {socials.map((social, index) => (
              <span
                key={social.id}
                aria-hidden="true"
                style={{
                  ...glyphBox(social, 19),
                  fontSize: '10px',
                  opacity: index === featuredIndex ? 1 : 0.4,
                }}
              >
                {social.glyph}
              </span>
            ))}
          </div>
        </div>
      </div>
    </OverlayWindow>
  );
}
