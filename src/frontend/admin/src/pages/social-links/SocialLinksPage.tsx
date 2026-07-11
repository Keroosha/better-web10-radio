import { useEffect, useRef, useState, type FormEvent, type ReactElement } from 'react';

import {
  ApiError,
  getSocialLinks,
  replaceSocialLinks,
  SocialKindSchema,
  type SocialKind,
  type SocialLink,
} from '@web10/shared';

import { useApiResource } from '../../shared/lib/useApiResource';
import { ResourceView } from '../../shared/ui/ResourceView';

const loadSocialLinks = (): Promise<SocialLink[]> => getSocialLinks();
const socialKinds: readonly SocialKind[] = ['telegram', 'youtube', 'instagram', 'discord', 'external'];

type SocialLinkReplacement = Parameters<typeof replaceSocialLinks>[0][number];

interface SocialLinkDraft {
  readonly draftKey: string;
  readonly label: string;
  readonly id: string | null;
  readonly kind: SocialKind;
  readonly name: string;
  readonly handle: string;
  readonly url: string;
  readonly glyph: string;
  readonly color: string;
  readonly qrImageUrl: string;
  readonly isFeatured: boolean;
}

function draftFromLink(link: SocialLink, draftKey: string): SocialLinkDraft {
  return {
    draftKey,
    label: link.name || 'social link',
    id: link.id,
    kind: link.kind,
    name: link.name,
    handle: link.handle ?? '',
    url: link.url,
    glyph: link.glyph ?? '',
    color: link.color ?? '',
    qrImageUrl: link.qrImageUrl ?? '',
    isFeatured: link.isFeatured,
  };
}

function newDraft(draftKey: string): SocialLinkDraft {
  return {
    draftKey,
    label: 'new social link',
    id: null,
    kind: 'external',
    name: '',
    handle: '',
    url: '',
    glyph: '',
    color: '',
    qrImageUrl: '',
    isFeatured: false,
  };
}

/** Maintains the complete, ordered replace-all social-link collection. */
export function SocialLinksPage(): ReactElement {
  const resource = useApiResource(loadSocialLinks);
  const [drafts, setDrafts] = useState<SocialLinkDraft[]>([]);
  const [savedLinks, setSavedLinks] = useState<SocialLink[] | null>(null);
  const [saveError, setSaveError] = useState<Error | null>(null);
  const [isSaving, setIsSaving] = useState(false);
  const nextDraftKey = useRef(0);

  useEffect(() => {
    if (resource.status === 'ready') {
      setDrafts(resource.data.map((link, index) => draftFromLink(link, `existing-${link.id}-${index}`)));
    }
  }, [resource]);

  const updateDraft = (draftKey: string, update: Partial<Omit<SocialLinkDraft, 'draftKey' | 'label'>>): void => {
    setDrafts((currentDrafts) =>
      currentDrafts.map((draft) => (draft.draftKey === draftKey ? { ...draft, ...update } : draft)),
    );
  };

  const moveDraft = (draftKey: string, direction: -1 | 1): void => {
    setDrafts((currentDrafts) => {
      const index = currentDrafts.findIndex((draft) => draft.draftKey === draftKey);
      const targetIndex = index + direction;
      if (index < 0 || targetIndex < 0 || targetIndex >= currentDrafts.length) {
        return currentDrafts;
      }
      const reordered = [...currentDrafts];
      const movingDraft = reordered[index];
      const targetDraft = reordered[targetIndex];
      if (movingDraft === undefined || targetDraft === undefined) {
        return currentDrafts;
      }
      reordered[index] = targetDraft;
      reordered[targetIndex] = movingDraft;
      return reordered;
    });
  };

  const saveLinks = async (event: FormEvent<HTMLFormElement>): Promise<void> => {
    event.preventDefault();
    if (isSaving) {
      return;
    }
    if (drafts.length > 50) {
      setSaveError(new Error('At most 50 social links may be saved.'));
      return;
    }

    const replacement: SocialLinkReplacement[] = [];
    for (const draft of drafts) {
      const name = draft.name.trim();
      const url = draft.url.trim();
      const color = draft.color.trim();
      if (name.length === 0 || name.length > 120) {
        setSaveError(new Error('Each social link needs a name containing 1–120 characters.'));
        return;
      }
      let parsedUrl: URL;
      try {
        parsedUrl = new URL(url);
      } catch {
        setSaveError(new Error('Each social link URL must be an absolute http or https URL.'));
        return;
      }
      if (parsedUrl.protocol !== 'http:' && parsedUrl.protocol !== 'https:') {
        setSaveError(new Error('Each social link URL must be an absolute http or https URL.'));
        return;
      }
      if (color.length > 0 && !/^#[0-9a-fA-F]{6}$/.test(color)) {
        setSaveError(new Error('Color must be #RRGGBB when supplied.'));
        return;
      }
      replacement.push({
        id: draft.id,
        kind: draft.kind,
        name,
        handle: draft.handle.trim() || null,
        url: parsedUrl.href,
        glyph: draft.glyph.trim() || null,
        color: color || null,
        qrImageUrl: draft.qrImageUrl.trim() || null,
        isFeatured: draft.isFeatured,
      });
    }

    setIsSaving(true);
    setSaveError(null);
    setSavedLinks(null);
    try {
      const canonicalLinks = await replaceSocialLinks(replacement);
      setSavedLinks(canonicalLinks);
      setDrafts(canonicalLinks.map((link, index) => draftFromLink(link, `existing-${link.id}-${index}`)));
    } catch (cause) {
      setSaveError(cause instanceof Error ? cause : new Error('Unable to save social links.'));
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <section>
      <h2>Social links</h2>
      <ResourceView resource={resource}>
        {(loadedLinks) => (
          <form onSubmit={saveLinks} style={{ maxWidth: '900px' }}>
            {loadedLinks.length === 0 && drafts.length === 0 ? (
              <p className="admin-muted">No social links configured.</p>
            ) : null}
            {drafts.length > 0 ? (
              <div className="admin-table-scroll">
                <table style={{ width: '100%' }}>
                  <thead>
                    <tr>
                      <th style={{ width: '18%' }}>Kind</th>
                      <th style={{ width: '54%' }}>Details</th>
                      <th style={{ width: '14%' }}>Order</th>
                      <th style={{ width: '14%' }}>Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {drafts.map((draft, index) => (
                      <tr key={draft.draftKey} style={{ verticalAlign: 'top' }}>
                        <td>
                          <div className="group">
                            <label htmlFor={`kind-${draft.draftKey}`}>Kind for {draft.label}</label>
                            <select
                              id={`kind-${draft.draftKey}`}
                              value={draft.kind}
                              onChange={(event) => {
                                const parsedKind = SocialKindSchema.safeParse(event.currentTarget.value);
                                if (parsedKind.success) {
                                  updateDraft(draft.draftKey, { kind: parsedKind.data });
                                }
                              }}
                              disabled={isSaving}
                            >
                              {socialKinds.map((kind) => <option key={kind} value={kind}>{kind}</option>)}
                            </select>
                          </div>
                        </td>
                        <td>
                          <p>{draft.name}</p>
                          <div className="group">
                            <label htmlFor={`name-${draft.draftKey}`}>Name for {draft.label}</label>
                            <input id={`name-${draft.draftKey}`} value={draft.name} onChange={(event) => updateDraft(draft.draftKey, { name: event.currentTarget.value })} disabled={isSaving} />
                          </div>
                          <div className="group">
                            <label htmlFor={`handle-${draft.draftKey}`}>Handle for {draft.label}</label>
                            <input id={`handle-${draft.draftKey}`} value={draft.handle} onChange={(event) => updateDraft(draft.draftKey, { handle: event.currentTarget.value })} disabled={isSaving} />
                          </div>
                          <div className="group">
                            <label htmlFor={`url-${draft.draftKey}`}>URL for {draft.label}</label>
                            <input id={`url-${draft.draftKey}`} value={draft.url} onChange={(event) => updateDraft(draft.draftKey, { url: event.currentTarget.value })} disabled={isSaving} />
                          </div>
                          <div className="group">
                            <label htmlFor={`glyph-${draft.draftKey}`}>Glyph for {draft.label}</label>
                            <input id={`glyph-${draft.draftKey}`} value={draft.glyph} onChange={(event) => updateDraft(draft.draftKey, { glyph: event.currentTarget.value })} disabled={isSaving} />
                          </div>
                          <div className="group">
                            <label htmlFor={`color-${draft.draftKey}`}>Color for {draft.label}</label>
                            <input id={`color-${draft.draftKey}`} value={draft.color} onChange={(event) => updateDraft(draft.draftKey, { color: event.currentTarget.value })} disabled={isSaving} />
                          </div>
                          <div className="group">
                            <label htmlFor={`qr-${draft.draftKey}`}>QR image URL for {draft.label}</label>
                            <input id={`qr-${draft.draftKey}`} value={draft.qrImageUrl} onChange={(event) => updateDraft(draft.draftKey, { qrImageUrl: event.currentTarget.value })} disabled={isSaving} />
                          </div>
                          <div>
                            <input id={`featured-${draft.draftKey}`} type="checkbox" checked={draft.isFeatured} onChange={(event) => updateDraft(draft.draftKey, { isFeatured: event.currentTarget.checked })} disabled={isSaving} />
                            <label htmlFor={`featured-${draft.draftKey}`}>Featured</label>
                          </div>
                        </td>
                        <td>
                          <div style={{ display: 'flex', gap: '4px' }}>
                            <button type="button" aria-label={`Move ${draft.label} up`} onClick={() => moveDraft(draft.draftKey, -1)} disabled={isSaving || index === 0}>↑</button>
                            <button type="button" aria-label={`Move ${draft.label} down`} onClick={() => moveDraft(draft.draftKey, 1)} disabled={isSaving || index === drafts.length - 1}>↓</button>
                          </div>
                        </td>
                        <td>
                          <button type="button" aria-label={`Remove ${draft.label}`} onClick={() => setDrafts((currentDrafts) => currentDrafts.filter((currentDraft) => currentDraft.draftKey !== draft.draftKey))} disabled={isSaving}>Remove</button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : null}
            <div style={{ display: 'flex', gap: '8px', marginTop: '12px' }}>
              <button type="button" onClick={() => { const draftKey = `new-${nextDraftKey.current}`; nextDraftKey.current += 1; setDrafts((currentDrafts) => [...currentDrafts, newDraft(draftKey)]); }} disabled={isSaving || drafts.length >= 50}>Add social link</button>
              <button type="submit" className="default" disabled={isSaving}>{isSaving ? 'Saving…' : 'Save social links'}</button>
            </div>
            {saveError !== null ? <p role="alert" className="admin-error">{saveError instanceof ApiError && saveError.code !== null ? saveError.code : saveError.message}</p> : null}
            {savedLinks !== null ? <p>Saved</p> : null}
          </form>
        )}
      </ResourceView>
    </section>
  );
}
