// Imperative Three.js scene for the public stage, recreated from the design mock
// `web-stage/mocks/project/Web 1.0 Radio Scene.dc.html` (`initThree`). This is the external
// system the React `StageScene` effect synchronises with: `StageScene` builds it on mount
// and calls `dispose()` on unmount. No React, no app state here.
//
// Everything the GPU allocates (geometries, materials, textures, the renderer) is registered
// so `dispose()` frees it, and the whole graphics layer is rebuilt on `webglcontextrestored`
// — this stage runs 24/7 and must survive GPU resets and tab idling.
import * as THREE from 'three';

import {
  PLACEHOLDER_TRACK,
  createAlbumTexture,
  createCheckerTexture,
  createNameplateTexture,
  createRadialTexture,
  createSpineTexture,
  createTrayTexture,
  drawAlbumArt,
  drawNameplate,
  type SceneTrack,
} from './textures';

export interface RadioSceneOptions {
  /** Initial nameplate / album-art track. Defaults to {@link PLACEHOLDER_TRACK}. */
  readonly track?: SceneTrack;
  /** Enable mouse-parallax camera. Off = static camera (stream-node capture). Default true. */
  readonly pointerEnabled?: boolean;
  /** Called once, on the first rendered frame, so the caller can hide its loading overlay. */
  readonly onReady?: () => void;
}

/** Live handle to a running scene. `dispose()` is idempotent and fully tears the scene down. */
export interface RadioSceneHandle {
  /** Re-fit the renderer + camera to the canvas size (call on container resize). */
  resize(): void;
  /** Stop the frame loop, remove listeners, and free all GPU resources. Idempotent. */
  dispose(): void;
}

interface Graphics {
  resize(): void;
  stop(): void;
  dispose(): void;
}

export function createRadioScene(
  canvas: HTMLCanvasElement,
  options: RadioSceneOptions = {},
): RadioSceneHandle {
  const track = options.track ?? PLACEHOLDER_TRACK;
  const pointerEnabled = options.pointerEnabled ?? true;
  const onReady = options.onReady;

  // Shared across context-restore rebuilds so parallax stays continuous.
  const mouse = { x: 0, y: 0 };
  let onReadyFired = false;
  let disposed = false;
  let graphics: Graphics | null = null;
  let buildGeneration = 0;

  function buildGraphics(): Graphics {
    const generation = ++buildGeneration;
    const geometries = new Set<THREE.BufferGeometry>();
    const materials = new Set<THREE.Material>();
    const textures = new Set<THREE.Texture>();
    const regGeo = <T extends THREE.BufferGeometry>(g: T): T => {
      geometries.add(g);
      return g;
    };
    const regMat = <T extends THREE.Material>(m: T): T => {
      materials.add(m);
      return m;
    };
    const regTex = <T extends THREE.Texture>(t: T): T => {
      textures.add(t);
      return t;
    };

    const width = canvas.clientWidth || window.innerWidth;
    const height = canvas.clientHeight || window.innerHeight;

    const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.setSize(width, height, false);

    const scene = new THREE.Scene();
    scene.fog = new THREE.Fog(0xffc9c0, 90, 260);

    const camera = new THREE.PerspectiveCamera(50, width / height, 0.1, 2000);
    camera.position.set(0, 7.6, 26);
    camera.lookAt(0, 5.6, 0);

    // ---- lights ----
    scene.add(new THREE.HemisphereLight(0xbfe4ff, 0xffc9ad, 0.62));
    scene.add(new THREE.AmbientLight(0xffffff, 0.18));
    const sun = new THREE.DirectionalLight(0xffe2c4, 1.65);
    sun.position.set(-16, 18, 14);
    scene.add(sun);
    const rim = new THREE.DirectionalLight(0x8fd0ff, 0.7);
    rim.position.set(18, 8, -16);
    scene.add(rim);

    // ---- sky dome ----
    const skyMat = regMat(
      new THREE.ShaderMaterial({
        side: THREE.BackSide,
        depthWrite: false,
        uniforms: {
          top: { value: new THREE.Color(0x5fa8f0) },
          mid: { value: new THREE.Color(0xffcbe4) },
          bot: { value: new THREE.Color(0xffab7e) },
        },
        vertexShader: `varying vec3 vP; void main(){ vP=(modelMatrix*vec4(position,1.0)).xyz; gl_Position=projectionMatrix*modelViewMatrix*vec4(position,1.0);} `,
        fragmentShader: `uniform vec3 top; uniform vec3 mid; uniform vec3 bot; varying vec3 vP;
          void main(){ float hgt=normalize(vP).y; float t=clamp(hgt,-0.15,1.0);
            vec3 c = t>0.18 ? mix(mid, top, smoothstep(0.18,0.75,t)) : mix(bot, mid, smoothstep(-0.15,0.18,t));
            gl_FragColor=vec4(c,1.0);} `,
      }),
    );
    scene.add(new THREE.Mesh(regGeo(new THREE.SphereGeometry(800, 32, 20)), skyMat));

    // ---- sun glow ----
    const glowTex = regTex(createRadialTexture('#fff7e6', 'rgba(255,179,122,0)'));
    const glow = new THREE.Sprite(
      regMat(
        new THREE.SpriteMaterial({
          map: glowTex,
          transparent: true,
          depthWrite: false,
          opacity: 0.9,
          blending: THREE.AdditiveBlending,
        }),
      ),
    );
    glow.position.set(-30, 26, -120);
    glow.scale.set(90, 90, 1);
    scene.add(glow);

    // ---- clouds ----
    const cloudTex = regTex(createRadialTexture('#ffffff', 'rgba(255,255,255,0)'));
    const clouds: { sprite: THREE.Sprite; speed: number }[] = [];
    for (let i = 0; i < 14; i++) {
      const sprite = new THREE.Sprite(
        regMat(
          new THREE.SpriteMaterial({
            map: cloudTex,
            transparent: true,
            depthWrite: false,
            opacity: 0.55 + Math.random() * 0.3,
          }),
        ),
      );
      const sc = 22 + Math.random() * 34;
      sprite.scale.set(sc, sc * 0.62, 1);
      sprite.position.set(
        -140 + Math.random() * 280,
        22 + Math.random() * 46,
        -60 - Math.random() * 160,
      );
      scene.add(sprite);
      clouds.push({ sprite, speed: 0.6 + Math.random() * 1.4 });
    }

    // ---- checker floor ----
    const checkerTex = regTex(createCheckerTexture());
    checkerTex.wrapS = THREE.RepeatWrapping;
    checkerTex.wrapT = THREE.RepeatWrapping;
    checkerTex.repeat.set(30, 30);
    checkerTex.magFilter = THREE.NearestFilter;
    const floor = new THREE.Mesh(
      regGeo(new THREE.PlaneGeometry(600, 600)),
      regMat(new THREE.MeshBasicMaterial({ map: checkerTex })),
    );
    floor.rotation.x = -Math.PI / 2;
    floor.position.y = -1.6;
    scene.add(floor);

    // ---- water ----
    // Hold the uniform directly so the frame loop avoids an indexed lookup into
    // `material.uniforms` (which `noUncheckedIndexedAccess` types as possibly undefined).
    const waterTime = { value: 0 };
    const waterMat = regMat(
      new THREE.ShaderMaterial({
        transparent: true,
        depthWrite: false,
        uniforms: { uTime: waterTime },
        vertexShader: `uniform float uTime; varying float vW; varying vec2 vUv;
          void main(){ vUv=uv; vec3 p=position;
            float w = sin(p.x*0.055 + uTime*0.7)*0.15 + cos(p.y*0.07 + uTime*0.5)*0.15 + sin((p.x+p.y)*0.028 + uTime*0.35)*0.08;
            p.z += w; vW=w;
            gl_Position=projectionMatrix*modelViewMatrix*vec4(p,1.0);} `,
        fragmentShader: `varying float vW; varying vec2 vUv;
          void main(){ vec3 deep=vec3(0.10,0.40,0.72); vec3 crest=vec3(0.66,0.90,1.0);
            float f=smoothstep(-0.15,0.25,vW); vec3 col=mix(deep,crest,f);
            col += pow(max(f,0.0),3.0)*0.30;
            gl_FragColor=vec4(col, 0.60);} `,
      }),
    );
    const water = new THREE.Mesh(regGeo(new THREE.PlaneGeometry(600, 600, 140, 140)), waterMat);
    water.rotation.x = -Math.PI / 2;
    water.position.y = 0.35;
    scene.add(water);

    // ---- temple ----
    const temple = new THREE.Group();
    const marble = regMat(
      new THREE.MeshStandardMaterial({ color: 0xf0e6dc, roughness: 0.5, metalness: 0.06 }),
    );
    const marble2 = regMat(
      new THREE.MeshStandardMaterial({ color: 0xe4d6ce, roughness: 0.58, metalness: 0.06 }),
    );

    const step1 = new THREE.Mesh(regGeo(new THREE.CylinderGeometry(8.4, 8.8, 1.4, 40)), marble2);
    step1.position.y = 0.7;
    temple.add(step1);
    const step2 = new THREE.Mesh(regGeo(new THREE.CylinderGeometry(7.2, 7.6, 0.8, 40)), marble);
    step2.position.y = 1.7;
    temple.add(step2);
    const deck = new THREE.Mesh(regGeo(new THREE.CylinderGeometry(6.6, 6.9, 0.5, 40)), marble2);
    deck.position.y = 2.2;
    temple.add(deck);
    const deckTopY = 2.45;

    // columns
    const colH = 8;
    const cx = 4.4;
    const colPos: readonly (readonly [number, number])[] = [
      [cx, cx],
      [-cx, cx],
      [cx, -cx],
      [-cx, -cx],
    ];
    for (const [x, z] of colPos) {
      const base = new THREE.Mesh(regGeo(new THREE.BoxGeometry(1.5, 0.5, 1.5)), marble);
      base.position.set(x, deckTopY + 0.25, z);
      temple.add(base);
      const shaft = new THREE.Mesh(
        regGeo(new THREE.CylinderGeometry(0.52, 0.62, colH, 20)),
        marble,
      );
      shaft.position.set(x, deckTopY + 0.5 + colH / 2, z);
      temple.add(shaft);
      const cap = new THREE.Mesh(regGeo(new THREE.BoxGeometry(1.6, 0.5, 1.6)), marble);
      cap.position.set(x, deckTopY + 0.5 + colH + 0.25, z);
      temple.add(cap);
    }

    // open entablature (frame, no roof)
    const topY = deckTopY + 0.5 + colH + 0.7;
    const mkBeam = (len: number, x: number, z: number, rotY: number): void => {
      const beam = new THREE.Mesh(regGeo(new THREE.BoxGeometry(len, 0.7, 0.9)), marble);
      beam.position.set(x, topY, z);
      beam.rotation.y = rotY;
      temple.add(beam);
    };
    mkBeam(cx * 2 + 1.6, 0, cx, 0);
    mkBeam(cx * 2 + 1.6, 0, -cx, 0);
    mkBeam(cx * 2 + 1.6, cx, 0, Math.PI / 2);
    mkBeam(cx * 2 + 1.6, -cx, 0, Math.PI / 2);

    // podium
    const podium = new THREE.Mesh(
      regGeo(new THREE.CylinderGeometry(1.7, 2.1, 2.6, 32)),
      regMat(new THREE.MeshStandardMaterial({ color: 0xf7eff4, roughness: 0.4, metalness: 0.1 })),
    );
    podium.position.set(0, deckTopY + 1.3, 0);
    temple.add(podium);
    const podTop = new THREE.Mesh(regGeo(new THREE.CylinderGeometry(1.9, 1.9, 0.3, 32)), marble);
    podTop.position.set(0, deckTopY + 2.75, 0);
    temple.add(podTop);

    // nameplate
    const nameplate = createNameplateTexture();
    regTex(nameplate.texture);
    drawNameplate(nameplate, track);
    const plate = new THREE.Mesh(
      regGeo(new THREE.PlaneGeometry(3.5, 0.9)),
      regMat(new THREE.MeshBasicMaterial({ map: nameplate.texture, transparent: true })),
    );
    plate.position.set(0, deckTopY + 1.5, 2.12);
    temple.add(plate);

    scene.add(temple);

    // ---- CD jewel case ----
    const cd = new THREE.Group();
    const album = createAlbumTexture();
    regTex(album.texture);
    drawAlbumArt(album, track);

    const artMat = regMat(new THREE.MeshBasicMaterial({ map: album.texture }));
    let active = true;
    const coverUrl = track.coverImageUrl.trim();
    if (coverUrl !== '') {
      new THREE.TextureLoader().load(
        coverUrl,
        (coverTexture) => {
          // ImageLoader cannot be cancelled portably. Dispose a late result instead of
          // attaching it to a disposed/context-replaced material.
          if (!active || disposed || generation !== buildGeneration) {
            coverTexture.dispose();
            return;
          }
          coverTexture.colorSpace = THREE.SRGBColorSpace;
          artMat.map = coverTexture;
          artMat.needsUpdate = true;
          textures.add(coverTexture);
          textures.delete(album.texture);
          album.texture.dispose();
        },
        undefined,
        (): void => {
          // Keep the generated album texture when an external/managed cover fails.
        },
      );
    }

    const trayMat = regMat(
      new THREE.MeshStandardMaterial({ map: regTex(createTrayTexture()), roughness: 0.5, metalness: 0.15 }),
    );
    const rimMat = regMat(new THREE.MeshStandardMaterial({ color: 0x0e0f12, roughness: 0.6 }));
    // material order: +x,-x,+y,-y,+z(front cover),-z(back tray)
    const insert = new THREE.Mesh(regGeo(new THREE.BoxGeometry(3.4, 3.4, 0.34)), [
      rimMat,
      rimMat,
      rimMat,
      rimMat,
      artMat,
      trayMat,
    ]);
    cd.add(insert);

    // clear plastic shell (very transparent, glossy)
    const caseMat = regMat(
      new THREE.MeshPhysicalMaterial({
        color: 0xeaf6ff,
        roughness: 0.04,
        metalness: 0,
        transparent: true,
        opacity: 0.13,
        clearcoat: 1,
        clearcoatRoughness: 0.02,
        side: THREE.DoubleSide,
      }),
    );
    cd.add(new THREE.Mesh(regGeo(new THREE.BoxGeometry(3.66, 3.66, 0.5)), caseMat));

    // black ridged hinge spine on the left
    const spineMat = regMat(
      new THREE.MeshStandardMaterial({ map: regTex(createSpineTexture()), roughness: 0.55, metalness: 0.2 }),
    );
    const spine = new THREE.Mesh(regGeo(new THREE.BoxGeometry(0.24, 3.68, 0.56)), spineMat);
    spine.position.set(-1.84, 0, 0);
    cd.add(spine);

    // clear corner clips
    const tabMat = regMat(
      new THREE.MeshPhysicalMaterial({ color: 0xffffff, roughness: 0.1, transparent: true, opacity: 0.35, clearcoat: 1 }),
    );
    const tabGeo = regGeo(new THREE.BoxGeometry(0.34, 0.34, 0.54));
    for (const [x, y] of [
      [1.55, 1.55],
      [-1.55, 1.55],
      [1.55, -1.55],
      [-1.55, -1.55],
    ] as const) {
      const tab = new THREE.Mesh(tabGeo, tabMat);
      tab.position.set(x, y, 0);
      cd.add(tab);
    }

    cd.position.set(0, 8.6, 0);
    scene.add(cd);

    // ---- frame loop ----
    let rafId = 0;
    const timer = new THREE.Timer();
    const animate = (): void => {
      if (!active) {
        return;
      }
      timer.update();
      const t = timer.getElapsed();
      waterTime.value = t;
      for (const cloud of clouds) {
        cloud.sprite.position.x += cloud.speed * 0.02;
        if (cloud.sprite.position.x > 160) {
          cloud.sprite.position.x = -160;
        }
      }
      cd.rotation.y = t * 0.6;
      cd.position.y = 8.6 + Math.sin(t * 1.1) * 0.5;
      const tx = mouse.x * 5;
      const ty = mouse.y * 2.5;
      camera.position.x += (tx - camera.position.x) * 0.03;
      camera.position.y += (7.6 - ty - camera.position.y) * 0.03;
      camera.lookAt(0, 5.6, 0);
      renderer.render(scene, camera);
      if (!onReadyFired) {
        onReadyFired = true;
        onReady?.();
      }
      rafId = requestAnimationFrame(animate);
    };
    rafId = requestAnimationFrame(animate);

    const resize = (): void => {
      const nw = canvas.clientWidth || window.innerWidth;
      const nh = canvas.clientHeight || window.innerHeight;
      renderer.setSize(nw, nh, false);
      camera.aspect = nw / nh;
      camera.updateProjectionMatrix();
    };

    const stop = (): void => {
      active = false;
      if (rafId !== 0) {
        cancelAnimationFrame(rafId);
        rafId = 0;
      }
    };

    const dispose = (): void => {
      stop();
      for (const g of geometries) {
        g.dispose();
      }
      for (const m of materials) {
        m.dispose();
      }
      for (const tex of textures) {
        tex.dispose();
      }
      renderer.dispose();
    };

    return { resize, stop, dispose };
  }

  // ---- window + canvas listeners (added once; survive context-restore rebuilds) ----
  const onResize = (): void => {
    graphics?.resize();
  };
  const onMouseMove = (event: MouseEvent): void => {
    mouse.x = event.clientX / window.innerWidth - 0.5;
    mouse.y = event.clientY / window.innerHeight - 0.5;
  };
  const onContextLost = (event: Event): void => {
    // Keep the drawing buffer so the browser can restore it; just halt rendering.
    event.preventDefault();
    graphics?.stop();
  };
  const onContextRestored = (): void => {
    if (disposed) {
      return;
    }
    graphics?.dispose();
    graphics = buildGraphics();
  };

  canvas.addEventListener('webglcontextlost', onContextLost);
  canvas.addEventListener('webglcontextrestored', onContextRestored);
  window.addEventListener('resize', onResize);
  if (pointerEnabled) {
    window.addEventListener('mousemove', onMouseMove);
  }

  graphics = buildGraphics();

  return {
    resize: onResize,
    dispose: (): void => {
      if (disposed) {
        return;
      }
      disposed = true;
      canvas.removeEventListener('webglcontextlost', onContextLost);
      canvas.removeEventListener('webglcontextrestored', onContextRestored);
      window.removeEventListener('resize', onResize);
      if (pointerEnabled) {
        window.removeEventListener('mousemove', onMouseMove);
      }
      graphics?.dispose();
      graphics = null;
    },
  };
}
