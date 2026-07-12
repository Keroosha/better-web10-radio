import { useEffect, useState, type CSSProperties, type ReactElement } from 'react';

import {
  getBanners,
  getSocialLinks,
  replaceBanners,
  replaceSocialLinks,
  type Banner,
  type BannerPosition,
  type BannersReplaceRequest,
  type BannerStyle,
  type BannerType,
  type SocialKind,
  type SocialLink,
} from '@web10/shared';

import { errorMessage } from '../../shared/lib/errorMessage';
import { useToast } from '../../shared/ui/toast';
import { COLORS, formGrid, iconButton, panel } from '../../shared/ui/tokens';

const TYPE_LABEL: Record<BannerType, string> = {
  nowplaying: 'Сейчас играет',
  donation: 'Цель сбора',
  social: 'Соцсети',
  custom: 'Произвольный',
};

const ACCENTS = ['#c0392b', '#2ecc71', '#2980b9', '#f39c12'] as const;

const POSITION_STYLE: Record<BannerPosition, CSSProperties> = {
  'top-left': { top: '14px', left: '14px' },
  'top-center': { top: '14px', left: '50%', transform: 'translateX(-50%)' },
  'top-right': { top: '14px', right: '14px' },
  'bottom-left': { bottom: '14px', left: '14px' },
  'bottom-center': { bottom: '14px', left: '50%', transform: 'translateX(-50%)' },
  'bottom-right': { bottom: '14px', right: '14px' },
};

function toRequest(banners: readonly Banner[]): BannersReplaceRequest {
  return banners.map((banner) => ({
    id: banner.id.startsWith('new-') ? null : banner.id,
    type: banner.type,
    title: banner.title,
    subtitle: banner.subtitle === '' ? null : banner.subtitle,
    text: banner.text === '' ? null : banner.text,
    style: banner.style,
    screenPosition: banner.screenPosition,
    accent: banner.accent === '' ? null : banner.accent,
    enabled: banner.enabled,
    rotationSeconds: banner.rotationSeconds >= 2 ? Math.min(120, banner.rotationSeconds) : null,
  }));
}

/** Баннеры: список + редактор оверлеев с превью; для типа «Соцсети» — список ссылок. */
export function BannersPage(): ReactElement {
  const { showToast } = useToast();
  const [banners, setBanners] = useState<Banner[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    void getBanners()
      .then((loaded) => {
        setBanners(loaded);
        setSelectedId((current) => (current !== null && loaded.some((b) => b.id === current) ? current : loaded[0]?.id ?? null));
      })
      .catch((cause) => showToast(errorMessage(cause, 'Не удалось загрузить баннеры')));
  }, [showToast]);

  const selected = banners.find((b) => b.id === selectedId) ?? null;

  const patch = (change: Partial<Banner>): void => {
    setBanners((current) => current.map((b) => (b.id === selectedId ? { ...b, ...change } : b)));
  };

  const newBanner = (): void => {
    const id = `new-${Date.now()}`;
    const banner: Banner = {
      id,
      type: 'custom',
      title: 'Новый баннер',
      subtitle: '',
      text: 'Текст баннера',
      style: 'aero',
      screenPosition: 'bottom-center',
      accent: '#3498db',
      enabled: false,
      sortOrder: banners.length,
      rotationSeconds: 0,
    };
    setBanners((current) => [...current, banner]);
    setSelectedId(id);
  };

  const save = async (): Promise<void> => {
    setSaving(true);
    try {
      const saved = await replaceBanners(toRequest(banners));
      setBanners(saved);
      setSelectedId((current) => (current !== null && current.startsWith('new-') ? saved[saved.length - 1]?.id ?? null : current));
      showToast('Баннер сохранён');
    } catch (cause) {
      showToast(cause instanceof Error ? cause.message : 'Не удалось сохранить');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div style={{ display: 'flex', gap: '16px', flexWrap: 'wrap', alignItems: 'flex-start' }}>
      <div style={{ flex: 'none', width: '200px' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '6px' }}>
          <strong>Баннеры</strong>
          <button type="button" onClick={newBanner} style={{ minWidth: 0, padding: '2px 10px' }}>
            ＋
          </button>
        </div>
        <ul className="tree-view has-container">
          {banners.map((banner) => (
            <li
              key={banner.id}
              onClick={() => setSelectedId(banner.id)}
              style={{ cursor: 'pointer', padding: '6px', borderRadius: '4px', background: banner.id === selectedId ? COLORS.selection : 'transparent' }}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between' }}>
                <span style={{ fontWeight: 600 }}>{banner.title}</span>
                <span style={{ fontSize: '10px', color: banner.enabled ? COLORS.live : '#999' }}>{banner.enabled ? 'на стриме' : 'скрыт'}</span>
              </div>
              <div style={{ fontSize: '11px', color: '#89a' }}>{TYPE_LABEL[banner.type]}</div>
            </li>
          ))}
        </ul>
      </div>

      {selected !== null ? (
        <>
          <div style={{ flex: 1, minWidth: '260px', order: 3, ...panel }}>
            <strong style={{ fontSize: '14px' }}>Редактор баннера</strong>
            <div style={{ ...formGrid, marginTop: '10px' }}>
              <label htmlFor="bn-type">Тип</label>
              <select id="bn-type" value={selected.type} onChange={(event) => patch({ type: event.target.value as BannerType })}>
                <option value="nowplaying">Сейчас играет</option>
                <option value="donation">Цель сбора</option>
                <option value="social">Соцсети</option>
                <option value="custom">Произвольный</option>
              </select>
              <label htmlFor="bn-title">Заголовок</label>
              <input id="bn-title" value={selected.title} onChange={(event) => patch({ title: event.target.value })} />
              <label htmlFor="bn-subtitle">Подзаголовок</label>
              <input id="bn-subtitle" value={selected.subtitle} onChange={(event) => patch({ subtitle: event.target.value })} />
              <label htmlFor="bn-text">Текст</label>
              <textarea id="bn-text" rows={2} value={selected.text} onChange={(event) => patch({ text: event.target.value })} />
              <label htmlFor="bn-style">Стиль</label>
              <select id="bn-style" value={selected.style} onChange={(event) => patch({ style: event.target.value as BannerStyle })}>
                <option value="aero">Aero (стекло)</option>
                <option value="win9x">Win9x (классика)</option>
              </select>
              <label htmlFor="bn-pos">Позиция</label>
              <select id="bn-pos" value={selected.screenPosition} onChange={(event) => patch({ screenPosition: event.target.value as BannerPosition })}>
                <option value="top-left">Сверху слева</option>
                <option value="top-center">Сверху по центру</option>
                <option value="top-right">Сверху справа</option>
                <option value="bottom-left">Снизу слева</option>
                <option value="bottom-center">Снизу по центру</option>
                <option value="bottom-right">Снизу справа</option>
              </select>
              <label>Акцент</label>
              <div style={{ display: 'flex', gap: '6px' }}>
                {ACCENTS.map((accent) => (
                  <button
                    key={accent}
                    type="button"
                    title={accent}
                    onClick={() => patch({ accent })}
                    style={{ minWidth: 0, width: '22px', height: '22px', padding: 0, background: accent, outline: selected.accent === accent ? '2px solid #12354a' : 'none' }}
                  />
                ))}
              </div>
              <label htmlFor="bn-enabled">Показ</label>
              <div>
                <input id="bn-enabled" type="checkbox" checked={selected.enabled} onChange={(event) => patch({ enabled: event.target.checked })} />{' '}
                <span style={{ fontSize: '12px', color: COLORS.subtle }}>{selected.enabled ? 'Показывается на стриме' : 'Скрыт со стрима'}</span>
              </div>
              {selected.type === 'social' ? (
                <>
                  <label htmlFor="bn-rotation">Смена ссылок</label>
                  <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
                    <input
                      id="bn-rotation"
                      type="number"
                      min={2}
                      max={120}
                      placeholder="по умолч."
                      value={selected.rotationSeconds > 0 ? selected.rotationSeconds : ''}
                      onChange={(event) => {
                        const parsed = Number.parseInt(event.target.value, 10);
                        patch({ rotationSeconds: Number.isNaN(parsed) || parsed < 0 ? 0 : parsed });
                      }}
                      style={{ width: '70px' }}
                    />
                    <span style={{ fontSize: '12px', color: COLORS.subtle }}>сек · пусто = по умолчанию (~4 c)</span>
                  </div>
                </>
              ) : null}
            </div>
            <div style={{ marginTop: '14px' }}>
              <button type="button" className="default" onClick={() => void save()} disabled={saving}>
                {saving ? 'Сохранение…' : 'Сохранить баннер'}
              </button>
            </div>
            {selected.type === 'social' ? <SocialLinksEditor /> : null}
          </div>

          <div style={{ flex: 1, minWidth: '280px', order: 2 }}>
            <div style={{ fontSize: '11px', color: COLORS.subtle, marginBottom: '6px' }}>ПРЕВЬЮ НА СТРИМЕ</div>
            <BannerPreview banner={selected} />
            <p style={{ fontSize: '11px', color: '#89a', marginTop: '8px' }}>
              Так баннер отобразится поверх 3D-сцены эфира. Позиция и стиль применяются после сохранения.
            </p>
          </div>
        </>
      ) : (
        <p className="admin-muted">Выберите или создайте баннер.</p>
      )}
    </div>
  );
}

function BannerPreview({ banner }: { readonly banner: Banner }): ReactElement {
  const glass = banner.style === 'aero';
  const cardStyle: CSSProperties = {
    position: 'absolute',
    ...POSITION_STYLE[banner.screenPosition],
    minWidth: '150px',
    maxWidth: '210px',
    padding: '10px 12px',
    borderRadius: glass ? '10px' : '2px',
    fontSize: '12px',
    ...(glass
      ? {
          background: 'rgba(255,255,255,.62)',
          backdropFilter: 'blur(6px)',
          border: '1px solid rgba(255,255,255,.8)',
          boxShadow: '0 6px 18px rgba(0,40,70,.25)',
        }
      : {
          background: 'linear-gradient(#f0f0f0,#d6d6d6)',
          border: '2px solid',
          borderColor: '#fff #808080 #808080 #fff',
          boxShadow: '2px 2px 0 rgba(0,0,0,.2)',
        }),
  };
  return (
    <div
      style={{
        position: 'relative',
        width: '100%',
        aspectRatio: '16/9',
        borderRadius: '8px',
        overflow: 'hidden',
        border: '1px solid #7ea6c6',
        background: 'linear-gradient(#ffd1e8 0%,#ffe0c2 42%,#bfe9ff 58%,#7ec8f0 100%)',
      }}
    >
      <div style={cardStyle}>
        <div style={{ fontSize: '10px', fontWeight: 700, letterSpacing: '.5px', color: banner.accent || COLORS.subtle }}>{banner.title}</div>
        {banner.subtitle !== '' ? <div style={{ fontWeight: 700, fontSize: '13px' }}>{banner.subtitle}</div> : null}
        {banner.type === 'donation' ? (
          <div style={{ marginTop: '6px' }}>
            <div style={{ height: '8px', borderRadius: '4px', background: '#dfe', overflow: 'hidden', border: '1px solid #ccc' }}>
              <div style={{ height: '100%', width: '70%', background: COLORS.progress }} />
            </div>
          </div>
        ) : null}
        {banner.type === 'custom' && banner.text !== '' ? (
          <div style={{ marginTop: '4px', fontSize: '11px', color: '#345' }}>{banner.text}</div>
        ) : null}
        {banner.type === 'social' ? (
          <div style={{ display: 'flex', gap: '6px', alignItems: 'center', marginTop: '6px' }}>
            <span style={{ width: '30px', height: '30px', borderRadius: '4px', background: '#fff', border: '1px solid #ccc', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '9px', color: '#456' }}>
              QR
            </span>
            <span style={{ width: '16px', height: '16px', borderRadius: '3px', background: banner.accent }} />
          </div>
        ) : null}
      </div>
    </div>
  );
}

interface DraftLink {
  id: string | null;
  kind: SocialKind;
  name: string;
  handle: string;
  url: string;
  glyph: string;
  color: string;
  qrImageUrl: string;
  isFeatured: boolean;
}

function toDraft(link: SocialLink): DraftLink {
  return {
    id: link.id,
    kind: link.kind,
    name: link.name,
    handle: link.handle,
    url: link.url,
    glyph: link.glyph,
    color: link.color,
    qrImageUrl: link.qrImageUrl,
    isFeatured: link.isFeatured,
  };
}

const KIND_DEFAULTS: Record<SocialKind, { glyph: string; color: string; name: string }> = {
  telegram: { glyph: 'T', color: '#2aabee', name: 'Telegram' },
  youtube: { glyph: 'Y', color: '#e5342b', name: 'YouTube' },
  instagram: { glyph: 'I', color: '#c9379d', name: 'Instagram' },
  discord: { glyph: 'D', color: '#5865f2', name: 'Discord' },
  external: { glyph: '•', color: '#0a86c9', name: 'Ссылка' },
};

const MAX_QR_BYTES = 1024 * 1024;

function readFileAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = (): void => reject(new Error('Не удалось прочитать файл'));
    reader.onload = (): void => {
      const result = reader.result;
      if (typeof result === 'string') {
        resolve(result);
      } else {
        reject(new Error('Не удалось прочитать файл'));
      }
    };
    reader.readAsDataURL(file);
  });
}

/** Комплексный редактор ссылок соцсетей, встроенный в баннер типа «Соцсети». */
function SocialLinksEditor(): ReactElement {
  const { showToast } = useToast();
  const [links, setLinks] = useState<DraftLink[]>([]);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    void getSocialLinks()
      .then((loaded) =>
        setLinks(
          loaded.map((link) => {
            const draft = toDraft(link);
            const preset = KIND_DEFAULTS[draft.kind];
            return { ...draft, glyph: draft.glyph || preset.glyph, color: draft.color || preset.color };
          }),
        ),
      )
      .catch(() => setLinks([]));
  }, []);

  const setLink = (index: number, change: Partial<DraftLink>): void => {
    setLinks((current) => current.map((link, i) => (i === index ? { ...link, ...change } : link)));
  };

  // Selecting a kind fills the mock's glyph + brand colour so the FOLLOW US badge renders.
  const setKind = (index: number, kind: SocialKind): void => {
    const preset = KIND_DEFAULTS[kind];
    setLink(index, { kind, glyph: preset.glyph, color: preset.color });
  };

  const setQr = async (index: number, file: File | null): Promise<void> => {
    if (file === null) {
      return;
    }
    if (!file.type.startsWith('image/')) {
      showToast('QR должен быть изображением');
      return;
    }
    if (file.size > MAX_QR_BYTES) {
      showToast('QR слишком большой (макс. 1 МБ)');
      return;
    }
    try {
      setLink(index, { qrImageUrl: await readFileAsDataUrl(file) });
    } catch (cause) {
      showToast(cause instanceof Error ? cause.message : 'Не удалось загрузить QR');
    }
  };

  const addLink = (): void => {
    const preset = KIND_DEFAULTS.telegram;
    setLinks((current) => [
      ...current,
      { id: null, kind: 'telegram', name: preset.name, handle: '', url: 'https://', glyph: preset.glyph, color: preset.color, qrImageUrl: '', isFeatured: current.length === 0 },
    ]);
  };

  const save = async (): Promise<void> => {
    for (const link of links) {
      if (!/^https?:\/\/.+/.test(link.url)) {
        showToast(`Некорректный URL: ${link.name}`);
        return;
      }
      if (link.color !== '' && !/^#[0-9a-fA-F]{6}$/.test(link.color)) {
        showToast(`Цвет должен быть #RRGGBB: ${link.name}`);
        return;
      }
    }
    setSaving(true);
    try {
      const saved = await replaceSocialLinks(
        links.map((link) => ({
          id: link.id,
          kind: link.kind,
          name: link.name.trim(),
          handle: link.handle === '' ? null : link.handle,
          url: link.url.trim(),
          glyph: link.glyph === '' ? null : link.glyph,
          color: link.color === '' ? null : link.color,
          qrImageUrl: link.qrImageUrl === '' ? null : link.qrImageUrl,
          isFeatured: link.isFeatured,
        })),
      );
      setLinks(saved.map(toDraft));
      showToast('Ссылки соцсетей сохранены');
    } catch (cause) {
      showToast(cause instanceof Error ? cause.message : 'Не удалось сохранить ссылки');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div style={{ marginTop: '16px', borderTop: '1px solid #cddff0', paddingTop: '12px' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '6px' }}>
        <strong style={{ fontSize: '13px' }}>Ссылки соцсетей</strong>
        <button type="button" onClick={addLink} style={{ minWidth: 0, padding: '2px 10px' }}>
          ＋ Ссылка
        </button>
      </div>
      {links.length === 0 ? <p style={{ fontSize: '12px', color: '#89a' }}>Ссылок пока нет.</p> : null}
      <ul className="tree-view has-container">
        {links.map((link, index) => (
          <li key={link.id ?? `new-${index}`} style={{ display: 'grid', gridTemplateColumns: 'auto 1fr auto', gap: '6px', padding: '6px', alignItems: 'start' }}>
            <div style={{ display: 'grid', gap: '4px', justifyItems: 'center' }}>
              <select value={link.kind} onChange={(event) => setKind(index, event.target.value as SocialKind)} style={{ minWidth: 0 }}>
                <option value="telegram">Telegram</option>
                <option value="youtube">YouTube</option>
                <option value="instagram">Instagram</option>
                <option value="discord">Discord</option>
                <option value="external">Другое</option>
              </select>
              <span
                style={{
                  width: '26px',
                  height: '26px',
                  borderRadius: '6px',
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  color: '#fff',
                  fontWeight: 900,
                  fontSize: '13px',
                  background: /^#[0-9a-fA-F]{6}$/.test(link.color) ? link.color : '#9bb',
                }}
              >
                {link.glyph || '•'}
              </span>
            </div>
            <div style={{ display: 'grid', gap: '4px', minWidth: 0 }}>
              <input value={link.name} placeholder="Название" onChange={(event) => setLink(index, { name: event.target.value })} />
              <input value={link.url} placeholder="https://…" onChange={(event) => setLink(index, { url: event.target.value })} />
              <input value={link.handle} placeholder="@handle" onChange={(event) => setLink(index, { handle: event.target.value })} />
              <div style={{ display: 'flex', gap: '6px', alignItems: 'center' }}>
                <input value={link.glyph} placeholder="Значок" maxLength={2} onChange={(event) => setLink(index, { glyph: event.target.value })} style={{ width: '54px' }} />
                <input type="color" value={/^#[0-9a-fA-F]{6}$/.test(link.color) ? link.color : '#2aabee'} onChange={(event) => setLink(index, { color: event.target.value })} style={{ width: '38px', padding: 0 }} />
                <label style={{ fontSize: '11px', color: COLORS.subtle }}>
                  <input type="checkbox" checked={link.isFeatured} onChange={(event) => setLink(index, { isFeatured: event.target.checked })} /> Основная (с QR)
                </label>
              </div>
              <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
                <span
                  style={{
                    width: '34px',
                    height: '34px',
                    flex: 'none',
                    borderRadius: '4px',
                    border: '1px solid #ccdbea',
                    background: '#fff',
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    fontSize: '9px',
                    color: '#88919c',
                    overflow: 'hidden',
                  }}
                >
                  {link.qrImageUrl === '' ? 'QR' : <img src={link.qrImageUrl} alt="QR" style={{ width: '100%', height: '100%', objectFit: 'contain' }} />}
                </span>
                <label style={{ fontSize: '11px', color: COLORS.subtle, cursor: 'pointer' }}>
                  <input
                    type="file"
                    accept="image/*"
                    style={{ display: 'none' }}
                    onChange={(event) => {
                      void setQr(index, event.target.files?.[0] ?? null);
                      event.target.value = '';
                    }}
                  />
                  <span
                    style={{
                      display: 'inline-block',
                      padding: '3px 10px',
                      borderRadius: '3px',
                      border: '1px solid #7ea6c6',
                      background: 'linear-gradient(#fbfdff,#dcebfb)',
                      fontSize: '11px',
                      color: '#12354a',
                    }}
                  >
                    Загрузить QR
                  </span>
                </label>
                {link.qrImageUrl === '' ? null : (
                  <button type="button" style={iconButton} title="Убрать QR" onClick={() => setLink(index, { qrImageUrl: '' })}>
                    ✕
                  </button>
                )}
              </div>
            </div>
            <button type="button" title="Убрать" style={iconButton} onClick={() => setLinks((current) => current.filter((_, i) => i !== index))}>
              ✕
            </button>
          </li>
        ))}
      </ul>
      <div style={{ marginTop: '10px' }}>
        <button type="button" className="default" onClick={() => void save()} disabled={saving}>
          {saving ? 'Сохранение…' : 'Сохранить ссылки'}
        </button>
      </div>
    </div>
  );
}
