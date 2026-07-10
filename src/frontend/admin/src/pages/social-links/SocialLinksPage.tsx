import type { ReactElement } from 'react';

import { getSocialLinks, type SocialLink } from '@web10/shared';

import { useApiResource } from '../../shared/lib/useApiResource';
import { ResourceView } from '../../shared/ui/ResourceView';

const loadSocialLinks = (): Promise<SocialLink[]> => getSocialLinks();

/**
 * Social links — read-only. `GET /api/v0/admin/social-links` is implemented; the `PUT`
 * is still `501 admin.contract_unpinned`, so editing is disabled until the backend pins
 * the request body (F4 follow-up).
 */
export function SocialLinksPage(): ReactElement {
  const resource = useApiResource(loadSocialLinks);

  return (
    <section>
      <h2 style={{ fontSize: '16px' }}>Social links</h2>
      <p style={{ fontSize: '12px', opacity: 0.7 }}>
        Read-only — editing (PUT) lands once the backend pins the admin contract.
      </p>
      <ResourceView resource={resource}>
        {(links) =>
          links.length === 0 ? (
            <p style={{ opacity: 0.7 }}>No social links configured.</p>
          ) : (
            <table style={{ borderCollapse: 'collapse', width: '100%', maxWidth: '640px' }}>
              <thead>
                <tr style={{ textAlign: 'left', borderBottom: '2px solid #ddd' }}>
                  <th style={{ padding: '6px' }}>Kind</th>
                  <th style={{ padding: '6px' }}>Name</th>
                  <th style={{ padding: '6px' }}>Handle</th>
                  <th style={{ padding: '6px' }}>Featured</th>
                </tr>
              </thead>
              <tbody>
                {links.map((link) => (
                  <tr key={link.id} style={{ borderBottom: '1px solid #eee' }}>
                    <td style={{ padding: '6px' }}>{link.kind}</td>
                    <td style={{ padding: '6px' }}>{link.name}</td>
                    <td style={{ padding: '6px' }}>{link.handle}</td>
                    <td style={{ padding: '6px' }}>{link.isFeatured ? '★' : ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        }
      </ResourceView>
    </section>
  );
}
