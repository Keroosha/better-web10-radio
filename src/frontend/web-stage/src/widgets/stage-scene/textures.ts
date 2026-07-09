// Procedural canvas textures for the radio stage, recreated from the design mock
// `web-stage/mocks/project/Web 1.0 Radio Scene.dc.html` (`_radialTex`, `_checkerTex`,
// `_trayTex`, `_spineTex`, `_roundRect`, `_fit`, `updateTrackArt`).
//
// These run only in the browser (they touch the 2D canvas context, which jsdom does not
// implement), so the real scene factory is exercised manually / in the dev server rather
// than in unit tests. Everything here is pure: given the same inputs it draws the same
// pixels and returns fresh Three.js textures the caller owns and disposes.
import * as THREE from 'three';

/** Track metadata the nameplate and album art render. */
export interface SceneTrack {
  readonly title: string;
  readonly artist: string;
}

/** The neutral placeholder shown before live `nowPlaying` data exists (Phase F2). */
export const PLACEHOLDER_TRACK: SceneTrack = {
  title: 'Web10.Radio',
  artist: '24/7 · @netscapedidnothingwrong',
};

function create2dCanvas(width: number, height: number): {
  canvas: HTMLCanvasElement;
  ctx: CanvasRenderingContext2D;
} {
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext('2d');
  if (ctx === null) {
    throw new Error('2D canvas context is unavailable');
  }
  return { canvas, ctx };
}

/** Soft radial gradient sprite used for the sun glow and clouds. */
export function createRadialTexture(inner: string, outer: string): THREE.CanvasTexture {
  const { canvas, ctx } = create2dCanvas(128, 128);
  const grd = ctx.createRadialGradient(64, 64, 4, 64, 64, 64);
  grd.addColorStop(0, inner);
  grd.addColorStop(1, outer);
  ctx.fillStyle = grd;
  ctx.fillRect(0, 0, 128, 128);
  return new THREE.CanvasTexture(canvas);
}

/** Two-tone checker used for the floor plane (tiled + nearest-filtered by the caller). */
export function createCheckerTexture(): THREE.CanvasTexture {
  const { canvas, ctx } = create2dCanvas(64, 64);
  ctx.fillStyle = '#ffffff';
  ctx.fillRect(0, 0, 64, 64);
  ctx.fillStyle = '#5cc7f5';
  ctx.fillRect(0, 0, 32, 32);
  ctx.fillRect(32, 32, 32, 32);
  return new THREE.CanvasTexture(canvas);
}

/** Black CD tray with rings, rosette hub, finger teeth and a "COMPACT disc" mark. */
export function createTrayTexture(): THREE.CanvasTexture {
  const size = 512;
  const { canvas, ctx } = create2dCanvas(size, size);

  const bg = ctx.createRadialGradient(size / 2, size / 2, 40, size / 2, size / 2, size * 0.72);
  bg.addColorStop(0, '#33363b');
  bg.addColorStop(0.7, '#212327');
  bg.addColorStop(1, '#15161a');
  ctx.fillStyle = bg;
  ctx.fillRect(0, 0, size, size);

  const cx = size / 2;
  const cy = size / 2;

  // disc impression rings
  ctx.strokeStyle = 'rgba(255,255,255,0.05)';
  ctx.lineWidth = 2;
  for (const r of [232, 214, 92]) {
    ctx.beginPath();
    ctx.arc(cx, cy, r, 0, 7);
    ctx.stroke();
  }
  ctx.fillStyle = 'rgba(255,255,255,0.03)';
  ctx.beginPath();
  ctx.arc(cx, cy, 226, 0, 7);
  ctx.fill();

  // central hub rosette
  const hubR = 66;
  const hb = ctx.createRadialGradient(cx, cy, 8, cx, cy, hubR);
  hb.addColorStop(0, '#8b9096');
  hb.addColorStop(0.6, '#63676c');
  hb.addColorStop(1, '#3a3d42');
  ctx.fillStyle = hb;
  ctx.beginPath();
  ctx.arc(cx, cy, hubR, 0, 7);
  ctx.fill();

  // finger teeth
  const teeth = 11;
  for (let i = 0; i < teeth; i++) {
    const a = (i / teeth) * Math.PI * 2;
    ctx.save();
    ctx.translate(cx, cy);
    ctx.rotate(a);
    ctx.fillStyle = '#c7ccd2';
    ctx.beginPath();
    ctx.moveTo(16, -6);
    ctx.lineTo(hubR - 6, -3);
    ctx.lineTo(hubR - 6, 3);
    ctx.lineTo(16, 6);
    ctx.closePath();
    ctx.fill();
    ctx.restore();
  }

  // star burst + centre hole
  ctx.fillStyle = '#e8ecf0';
  ctx.beginPath();
  for (let i = 0; i < 16; i++) {
    const a = (i / 16) * Math.PI * 2;
    const r = i % 2 ? 9 : 22;
    const x = cx + Math.cos(a) * r;
    const y = cy + Math.sin(a) * r;
    if (i === 0) {
      ctx.moveTo(x, y);
    } else {
      ctx.lineTo(x, y);
    }
  }
  ctx.closePath();
  ctx.fill();
  ctx.fillStyle = '#17181c';
  ctx.beginPath();
  ctx.arc(cx, cy, 8, 0, 7);
  ctx.fill();

  // corner clip hints
  ctx.fillStyle = 'rgba(255,255,255,0.06)';
  for (const [x, y] of [
    [40, 40],
    [size - 40, 40],
    [40, size - 40],
    [size - 40, size - 40],
  ] as const) {
    ctx.fillRect(x - 14, y - 6, 28, 12);
  }

  // compact disc mark
  ctx.fillStyle = 'rgba(200,205,210,0.4)';
  ctx.font = '600 15px Tahoma, sans-serif';
  ctx.fillText('COMPACT', 34, size - 46);
  ctx.fillText('disc', 34, size - 30);

  return new THREE.CanvasTexture(canvas);
}

/** Ridged black hinge spine on the left edge of the jewel case. */
export function createSpineTexture(): THREE.CanvasTexture {
  const { canvas, ctx } = create2dCanvas(48, 256);
  ctx.fillStyle = '#0b0c0e';
  ctx.fillRect(0, 0, 48, 256);
  for (let y = 0; y < 256; y += 6) {
    ctx.fillStyle = 'rgba(255,255,255,0.09)';
    ctx.fillRect(0, y, 48, 1);
    ctx.fillStyle = 'rgba(0,0,0,0.5)';
    ctx.fillRect(0, y + 3, 48, 2);
  }
  return new THREE.CanvasTexture(canvas);
}

function roundRect(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  w: number,
  h: number,
  r: number,
): void {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}

/** Truncate `text` with an ellipsis until it fits within `maxWidth` at the current font. */
function fitText(ctx: CanvasRenderingContext2D, text: string, maxWidth: number): string {
  let t = text;
  while (ctx.measureText(t).width > maxWidth && t.length > 4) {
    t = t.slice(0, -2);
  }
  return t === text ? text : `${t}…`;
}

/**
 * A canvas + its Three.js texture, kept alive so the caller can redraw and flag
 * `texture.needsUpdate` when the track changes (Phase F3).
 */
export interface DynamicTexture {
  readonly canvas: HTMLCanvasElement;
  readonly texture: THREE.CanvasTexture;
}

function createDynamicTexture(width: number, height: number): DynamicTexture {
  const { canvas } = create2dCanvas(width, height);
  return { canvas, texture: new THREE.CanvasTexture(canvas) };
}

/** Temple nameplate texture (1024×260); redraw with {@link drawNameplate}. */
export function createNameplateTexture(): DynamicTexture {
  return createDynamicTexture(1024, 260);
}

/** CD insert album-art texture (512×512); redraw with {@link drawAlbumArt}. */
export function createAlbumTexture(): DynamicTexture {
  return createDynamicTexture(512, 512);
}

/** Draw the temple nameplate for `track` onto its canvas and flag the texture for upload. */
export function drawNameplate(target: DynamicTexture, track: SceneTrack): void {
  const ctx = target.canvas.getContext('2d');
  if (ctx === null) {
    return;
  }
  ctx.clearRect(0, 0, 1024, 260);
  ctx.fillStyle = 'rgba(10,32,54,0.82)';
  roundRect(ctx, 20, 20, 984, 220, 26);
  ctx.fill();
  ctx.fillStyle = '#66e0ff';
  roundRect(ctx, 20, 20, 984, 8, 6);
  ctx.fill();
  ctx.textAlign = 'center';
  ctx.fillStyle = '#ffffff';
  ctx.font = "700 78px 'Noto Sans JP', sans-serif";
  ctx.fillText(fitText(ctx, track.title, 940), 512, 130);
  // The mock sets an invalid `#bdff6` here which the canvas ignores, so the artist renders
  // in the preceding white; we make that effective colour explicit.
  ctx.fillStyle = '#ffffff';
  ctx.font = "400 46px 'Tahoma', sans-serif";
  ctx.fillText(track.artist, 512, 196);
  target.texture.needsUpdate = true;
}

/** Draw the vaporwave album-art placeholder for `track` and flag the texture for upload. */
export function drawAlbumArt(target: DynamicTexture, track: SceneTrack): void {
  const ctx = target.canvas.getContext('2d');
  if (ctx === null) {
    return;
  }
  const grd = ctx.createLinearGradient(0, 0, 0, 512);
  grd.addColorStop(0, '#ffd1ec');
  grd.addColorStop(0.5, '#cfe9ff');
  grd.addColorStop(1, '#bff3e0');
  ctx.fillStyle = grd;
  ctx.fillRect(0, 0, 512, 512);

  // sun
  const sg = ctx.createRadialGradient(256, 200, 20, 256, 200, 150);
  sg.addColorStop(0, 'rgba(255,244,214,0.95)');
  sg.addColorStop(1, 'rgba(255,180,120,0)');
  ctx.fillStyle = sg;
  ctx.beginPath();
  ctx.arc(256, 200, 150, 0, 7);
  ctx.fill();

  // horizon grid
  ctx.strokeStyle = 'rgba(255,255,255,0.55)';
  ctx.lineWidth = 2;
  for (let i = 0; i <= 10; i++) {
    ctx.beginPath();
    ctx.moveTo(0, 320 + i * 20);
    ctx.lineTo(512, 320 + i * 20);
    ctx.stroke();
  }
  for (let i = -6; i <= 6; i++) {
    ctx.beginPath();
    ctx.moveTo(256, 320);
    ctx.lineTo(256 + i * 90, 512);
    ctx.stroke();
  }

  ctx.textAlign = 'center';
  ctx.fillStyle = 'rgba(20,50,80,0.85)';
  ctx.font = "700 40px 'Noto Sans JP', sans-serif";
  ctx.fillText(fitText(ctx, track.title, 460), 256, 300);
  ctx.font = "16px 'Tahoma', monospace";
  ctx.fillStyle = 'rgba(20,50,80,0.6)';
  ctx.fillText(track.artist, 256, 470);

  target.texture.needsUpdate = true;
}
