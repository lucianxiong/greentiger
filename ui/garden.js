/* ============================================================
 * 绿虎 · 自然生长可视化（GreenTiger Garden）
 *
 * 渲染思路参考 Three.js：场景分层（天空 / 远山 / 草地 / 植株）、
 * 透视投影、按深度排序、逐帧 requestAnimationFrame 和缓动函数。
 * 不依赖 WebGL 或 CDN，用 Canvas 2D 实现，离线也能稳定生长。
 *
 * 对外 API：window.GreenTigerGarden
 *   mount(canvas, options) -> { replay, resize, setWind, togglePause, destroy, stats }
 *   selfCheck(host)        -> { ok, pass, total, checks, stats }
 * ============================================================ */
(function (global) {
  "use strict";

  const TAU = Math.PI * 2;
  const VERSION = "1.0.0";

  function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }
  function lerp(a, b, t) { return a + (b - a) * t; }
  function easeOutCubic(t) { t = clamp(t, 0, 1); return 1 - Math.pow(1 - t, 3); }
  function easeOutBack(t) {
    t = clamp(t, 0, 1);
    const c = 1.70158;
    return 1 + (c + 1) * Math.pow(t - 1, 3) + c * Math.pow(t - 1, 2);
  }
  function mulberry32(seed) {
    let a = seed >>> 0;
    return function () {
      a = (a + 0x6D2B79F5) >>> 0;
      let t = a;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }
  function newSeed() {
    return ((Date.now() & 0x7fffffff) ^ Math.floor(Math.random() * 0x7fffffff)) >>> 0;
  }

  const FLOWER_COLORS = [
    { petal: "#ffd6e7", edge: "#f7a9c4", center: "#f4c54f" },
    { petal: "#fff6c9", edge: "#f3d97a", center: "#e9a83c" },
    { petal: "#f3e4ff", edge: "#d5b5f2", center: "#e5b84d" },
    { petal: "#ffffff", edge: "#d8ead4", center: "#e6b84e" },
    { petal: "#ffd9c8", edge: "#f4b295", center: "#e9ac45" }
  ];

  function palette() {
    const dark = document.documentElement.dataset.theme === "dark";
    if (dark) {
      return {
        dark: true,
        skyTop: "#06110b", skyMid: "#0d2116", skyLow: "#173a24",
        sun: "#d7f5b7", sunGlow: "rgba(154, 226, 150, .20)",
        hillFar: "#123120", hillMid: "#174329", hillNear: "#1e5632",
        groundTop: "#1d5634", groundLow: "#0b2317",
        blade: ["#2c6f42", "#3e8a51", "#56a663", "#216039"],
        stem: "#4f9f63", stemLight: "#7bca86", leaf: "#3e8b53", leafLight: "#67b974",
        pollen: "rgba(214, 246, 192, .85)", haze: "rgba(170, 230, 180, .05)",
        ink: "#e6f2ea"
      };
    }
    return {
      dark: false,
      skyTop: "#f3fbf0", skyMid: "#e5f6de", skyLow: "#cfeac4",
      sun: "#fff6c6", sunGlow: "rgba(255, 238, 158, .42)",
      hillFar: "#d3ebc8", hillMid: "#b9dfae", hillNear: "#9bd094",
      groundTop: "#94ca8c", groundLow: "#5da268",
      blade: ["#4e9c5f", "#63b06f", "#7bc181", "#3d8851"],
      stem: "#4d9b5f", stemLight: "#7bc687", leaf: "#4a9559", leafLight: "#75bd7d",
      pollen: "rgba(255, 252, 214, .9)", haze: "rgba(255, 255, 255, .10)",
      ink: "#1c2a20"
    };
  }

  function startScene(canvas, options) {
    const opts = Object.assign({
      compact: false,
      seed: 20261027,
      reducedMotion: false
    }, options || {});
    const ctx = canvas.getContext("2d", { alpha: true });
    if (!ctx) return null;

    const state = {
      canvas: canvas,
      ctx: ctx,
      w: 0,
      h: 0,
      dpr: 1,
      compact: !!opts.compact,
      seed: opts.seed >>> 0,
      time: 0,
      started: 0,
      last: 0,
      raf: 0,
      running: false,
      paused: false,
      visible: true,
      wind: 0.55,
      windTarget: 0.55,
      pointerX: 0.5,
      plants: [],
      grass: [],
      motes: [],
      burst: [],
      fps: 0,
      frameCount: 0,
      fpsAt: 0,
      reduce: !!opts.reducedMotion || (global.matchMedia && global.matchMedia("(prefers-reduced-motion: reduce)").matches),
      ro: null,
      io: null,
      destroyed: false
    };

    function build() {
      const rand = mulberry32(state.seed);
      const widthHint = state.w || 960;
      const plantCount = state.compact ? 5 : Math.round(clamp(widthHint / 72, 10, 17));
      state.plants = [];
      for (let i = 0; i < plantCount; i++) {
        const z = state.compact ? 0.72 + rand() * 0.28 : Math.pow(rand(), 0.82);
        const flower = rand() > (state.compact ? 0.2 : 0.34);
        const leaves = [];
        const leafCount = 1 + Math.floor(rand() * 2.4);
        for (let l = 0; l < leafCount; l++) {
          leaves.push({
            at: 0.24 + rand() * 0.42,
            side: rand() > 0.5 ? 1 : -1,
            size: 0.58 + rand() * 0.55,
            angle: -0.18 + rand() * 0.36
          });
        }
        state.plants.push({
          // 正方小景把花草铺开写：x 覆盖 0.22~0.78，不再挤在中间 40% 里；
          // 高度也收一档，给太阳留出呼吸空间（原来顶花会蹭到太阳上）。
          x: state.compact ? 0.22 + (i / (plantCount - 1 || 1)) * 0.56 : 0.04 + rand() * 0.92,
          z: z,
          height: state.compact ? 0.2 + rand() * 0.16 : 0.11 + rand() * 0.17,
          lean: (rand() - 0.5) * 0.7,
          width: 1.1 + rand() * 1.7,
          delay: state.compact ? rand() * 0.24 : rand() * 0.85,
          leaves: leaves,
          flower: flower,
          flowerIndex: Math.floor(rand() * FLOWER_COLORS.length),
          petals: 5 + Math.floor(rand() * 3),
          bloom: 0.8 + rand() * 0.45,
          phase: rand() * TAU
        });
      }
      state.plants.sort(function (a, b) { return a.z - b.z; });

      const grassCount = state.compact ? 18 : Math.round(clamp(widthHint / 7, 42, 150));
      state.grass = [];
      for (let i = 0; i < grassCount; i++) {
        state.grass.push({
          x: rand(),
          z: Math.pow(rand(), 0.7),
          h: 0.035 + rand() * 0.07,
          bend: (rand() - 0.5) * 0.8,
          delay: rand() * 0.35,
          shade: Math.floor(rand() * 4),
          phase: rand() * TAU
        });
      }

      const moteCount = state.compact ? 7 : 34;
      state.motes = [];
      for (let i = 0; i < moteCount; i++) {
        state.motes.push({
          x: rand(),
          y: rand(),
          z: 0.2 + rand() * 0.8,
          r: 0.7 + rand() * 1.8,
          phase: rand() * TAU,
          speed: 0.15 + rand() * 0.35
        });
      }
      state.burst = [];
    }

    function resize() {
      if (state.destroyed) return;
      const rect = canvas.getBoundingClientRect();
      const w = Math.max(1, Math.round(rect.width || opts.width || canvas.width || 640));
      const h = Math.max(1, Math.round(rect.height || opts.height || canvas.height || 360));
      const dpr = Math.min(2, global.devicePixelRatio || 1);
      state.w = w;
      state.h = h;
      state.dpr = dpr;
      const pw = Math.round(w * dpr);
      const ph = Math.round(h * dpr);
      if (canvas.width !== pw || canvas.height !== ph) {
        canvas.width = pw;
        canvas.height = ph;
      }
      if (!state.plants.length) build();
      draw();
    }

    function project(x, z) {
      const w = state.w;
      const h = state.h;
      const spread = state.compact ? 0.8 + z * 0.35 : 0.62 + z * 0.72;
      const baseY = state.compact ? h * (0.74 + z * 0.14) : h * (0.53 + 0.41 * Math.pow(z, 0.92));
      return {
        x: w * 0.5 + (x - 0.5) * w * spread,
        y: baseY,
        scale: (state.compact ? 0.8 : 0.5) + z * 0.72
      };
    }

    function windAt(t, z, phase) {
      return Math.sin(t * 1.05 + phase * 0.7 + z * 2.4) * (0.55 + z * 0.45)
        + Math.sin(t * 2.2 + phase) * 0.12;
    }

    function paintSky(t) {
      const c = palette();
      const g = ctx.createLinearGradient(0, 0, 0, state.h);
      g.addColorStop(0, c.skyTop);
      g.addColorStop(0.58, c.skyMid);
      g.addColorStop(1, c.skyLow);
      ctx.fillStyle = g;
      ctx.fillRect(0, 0, state.w, state.h);

      // 太阳 / 月亮：柔光 + 缓慢浮动
      const sunX = state.w * (state.compact ? 0.5 : 0.76);
      const sunY = state.h * (state.compact ? 0.2 : 0.2) + Math.sin(t * 0.18) * 2;
      const sunR = Math.max(5, state.h * (state.compact ? 0.075 : 0.045));
      const glow = ctx.createRadialGradient(sunX, sunY, 0, sunX, sunY, sunR * 4.6);
      glow.addColorStop(0, c.sunGlow);
      glow.addColorStop(1, "rgba(255,255,255,0)");
      ctx.fillStyle = glow;
      ctx.beginPath();
      ctx.arc(sunX, sunY, sunR * 4.6, 0, TAU);
      ctx.fill();
      ctx.fillStyle = c.sun;
      ctx.beginPath();
      ctx.arc(sunX, sunY, sunR, 0, TAU);
      ctx.fill();

      if (!state.compact) {
        // 薄云：两团低透明度椭圆，做极慢的横向漂移
        ctx.fillStyle = c.haze;
        for (let i = 0; i < 2; i++) {
          const cx = state.w * (0.18 + i * 0.46) + Math.sin(t * 0.04 + i) * 12;
          const cy = state.h * (0.24 + i * 0.1);
          ctx.beginPath();
          ctx.ellipse(cx, cy, state.w * 0.13, state.h * 0.028, 0, 0, TAU);
          ctx.ellipse(cx + state.w * 0.07, cy + 3, state.w * 0.09, state.h * 0.02, 0, 0, TAU);
          ctx.fill();
        }
      }
    }

    function paintHills(t) {
      const c = palette();
      const layers = [
        { y: 0.52, amp: 0.045, color: c.hillFar, shift: 0.35 },
        { y: 0.57, amp: 0.055, color: c.hillMid, shift: 0.7 },
        { y: 0.63, amp: 0.042, color: c.hillNear, shift: 1.1 }
      ];
      layers.forEach(function (layer, index) {
        const base = state.h * layer.y;
        ctx.beginPath();
        ctx.moveTo(0, state.h);
        ctx.lineTo(0, base);
        const step = Math.max(12, state.w / 48);
        for (let x = 0; x <= state.w + step; x += step) {
          const y = base
            + Math.sin(x / state.w * 5.2 + layer.shift + t * 0.02) * state.h * layer.amp
            + Math.sin(x / state.w * 11.5 + index * 1.7) * state.h * layer.amp * 0.3;
          ctx.lineTo(x, y);
        }
        ctx.lineTo(state.w, state.h);
        ctx.closePath();
        ctx.fillStyle = layer.color;
        ctx.fill();
      });

      // 草地：从地平线铺到画面底部
      const g = ctx.createLinearGradient(0, state.h * 0.58, 0, state.h);
      g.addColorStop(0, c.groundTop);
      g.addColorStop(1, c.groundLow);
      ctx.fillStyle = g;
      ctx.beginPath();
      ctx.moveTo(0, state.h * 0.61);
      ctx.quadraticCurveTo(state.w * 0.5, state.h * 0.55, state.w, state.h * 0.62);
      ctx.lineTo(state.w, state.h);
      ctx.lineTo(0, state.h);
      ctx.closePath();
      ctx.fill();

      if (!state.compact) {
        ctx.fillStyle = c.haze;
        ctx.fillRect(0, state.h * 0.56, state.w, state.h * 0.12);
      }
    }

    function paintGrass(t) {
      const c = palette();
      const age = state.reduce ? 6 : Math.max(0, state.time - state.started);
      for (let i = 0; i < state.grass.length; i++) {
        const blade = state.grass[i];
        const g = easeOutCubic((age - blade.delay) / 1.35);
        if (g <= 0) continue;
        const base = project(blade.x, blade.z);
        const h = blade.h * state.h * g * (0.7 + blade.z * 0.9);
        const wind = windAt(t, blade.z, blade.phase) * state.wind;
        const topX = base.x + (blade.bend * 0.35 + wind * 0.10) * h;
        const sway = Math.sin(t * 1.3 + blade.phase) * state.wind * 0.06 * h;
        ctx.beginPath();
        ctx.moveTo(base.x, base.y);
        ctx.quadraticCurveTo(base.x + sway, base.y - h * 0.55, topX, base.y - h);
        ctx.strokeStyle = c.blade[blade.shade % c.blade.length];
        ctx.globalAlpha = 0.45 + blade.z * 0.45;
        ctx.lineWidth = Math.max(0.8, state.compact ? 0.8 : 1.05);
        ctx.lineCap = "round";
        ctx.stroke();
      }
      ctx.globalAlpha = 1;
    }

    function drawLeaf(x, y, size, side, angle, c) {
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate(angle + side * 0.36);
      ctx.beginPath();
      ctx.moveTo(0, 0);
      ctx.quadraticCurveTo(side * size * 0.58, -size * 0.5, side * size, 0);
      ctx.quadraticCurveTo(side * size * 0.5, size * 0.4, 0, 0);
      ctx.closePath();
      const g = ctx.createLinearGradient(0, -size * 0.5, side * size, size * 0.2);
      g.addColorStop(0, c.leafLight);
      g.addColorStop(1, c.leaf);
      ctx.fillStyle = g;
      ctx.fill();
      ctx.strokeStyle = c.leaf;
      ctx.globalAlpha = 0.6;
      ctx.lineWidth = 0.7;
      ctx.stroke();
      ctx.globalAlpha = 1;
      ctx.restore();
    }

    function drawFlower(x, y, radius, p, bloom, c, t) {
      if (bloom <= 0.02) return;
      const paletteFlower = FLOWER_COLORS[p.flowerIndex % FLOWER_COLORS.length];
      const r = radius * bloom;
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate(Math.sin(t * 0.9 + p.phase) * 0.08);
      for (let i = 0; i < p.petals; i++) {
        const a = (i / p.petals) * TAU;
        ctx.save();
        ctx.rotate(a);
        ctx.beginPath();
        ctx.ellipse(r * 0.55, 0, r * 0.58, r * 0.31, 0, 0, TAU);
        ctx.fillStyle = paletteFlower.petal;
        ctx.fill();
        ctx.strokeStyle = paletteFlower.edge;
        ctx.globalAlpha = 0.55;
        ctx.lineWidth = Math.max(0.5, r * 0.06);
        ctx.stroke();
        ctx.globalAlpha = 1;
        ctx.restore();
      }
      ctx.beginPath();
      ctx.arc(0, 0, Math.max(0.7, r * 0.28), 0, TAU);
      ctx.fillStyle = paletteFlower.center;
      ctx.fill();
      ctx.restore();
    }

    function paintPlant(p, t, age, c) {
      const g = easeOutCubic((age - p.delay) / 1.65);
      if (g <= 0) return;
      const base = project(p.x, p.z);
      const scale = base.scale;
      const wind = windAt(t, p.z, p.phase) * state.wind;
      const stemH = p.height * state.h * g * (0.62 + p.z * 0.55);
      const lean = p.lean * state.w * 0.018 * g;
      const sway = wind * state.w * 0.012 * g;
      const topX = base.x + lean + sway;
      const topY = base.y - stemH;
      const arm = stemH * 0.16;
      const c1x = base.x + (topX - base.x) * 0.25;
      const c1y = base.y - stemH * 0.42;
      const c2x = base.x + (topX - base.x) * 0.78 + wind * arm * 0.3;
      const c2y = base.y - stemH * 0.78;

      ctx.globalAlpha = 0.72 + p.z * 0.28;
      ctx.beginPath();
      ctx.moveTo(base.x, base.y);
      ctx.bezierCurveTo(c1x, c1y, c2x, c2y, topX, topY);
      ctx.strokeStyle = c.stem;
      ctx.lineWidth = Math.max(1, p.width * scale * g);
      ctx.lineCap = "round";
      ctx.stroke();

      // 茎上的高光，让近处植株更立体
      if (!state.compact) {
        ctx.beginPath();
        ctx.moveTo(base.x + 0.6 * scale, base.y);
        ctx.bezierCurveTo(c1x + 0.6, c1y, c2x + 0.6, c2y, topX + 0.6, topY);
        ctx.strokeStyle = c.stemLight;
        ctx.globalAlpha = 0.25;
        ctx.lineWidth = Math.max(0.5, p.width * scale * 0.35);
        ctx.stroke();
        ctx.globalAlpha = 0.72 + p.z * 0.28;
      }

      p.leaves.forEach(function (leaf) {
        const f = clamp(leaf.at * g, 0, 1);
        if (f <= 0.04) return;
        const lx = base.x + (topX - base.x) * f;
        const ly = base.y - stemH * f;
        const leafSize = leaf.size * state.h * 0.052 * scale * (0.55 + g * 0.45);
        drawLeaf(lx, ly, leafSize, leaf.side, leaf.angle + wind * 0.16, c);
      });

      if (p.flower) {
        const bloom = easeOutBack((g - 0.56) / 0.44);
        const radius = (state.compact ? state.h * 0.052 : state.h * 0.024) * scale * p.bloom;
        drawFlower(topX, topY, radius, p, bloom, c, t);
      } else if (g > 0.72) {
        // 未开花的植株：一颗小嫩芽
        const bud = (g - 0.72) / 0.28;
        ctx.beginPath();
        ctx.ellipse(topX, topY, 1.2 + 2.2 * bud * scale, 2 + 4 * bud * scale, -0.2, 0, TAU);
        ctx.fillStyle = c.leafLight;
        ctx.fill();
      }
      ctx.globalAlpha = 1;
    }

    function paintPlants(t, age, c) {
      // 深度排序：远 → 近，近处覆盖远处
      for (let i = 0; i < state.plants.length; i++) paintPlant(state.plants[i], t, age, c);
    }

    function paintMotes(t, c) {
      const count = state.motes.length;
      for (let i = 0; i < count; i++) {
        const m = state.motes[i];
        const x = (m.x * state.w + Math.sin(t * m.speed + m.phase) * state.w * 0.035 + state.w) % state.w;
        const y = m.y * state.h + Math.cos(t * m.speed * 1.3 + m.phase) * state.h * 0.05;
        const r = m.r * (0.6 + m.z * 0.6);
        ctx.beginPath();
        ctx.arc(x, y, r, 0, TAU);
        ctx.fillStyle = c.pollen;
        ctx.globalAlpha = 0.35 + m.z * 0.45;
        ctx.fill();
      }
      ctx.globalAlpha = 1;
    }

    function updateBurst(dt) {
      for (let i = state.burst.length - 1; i >= 0; i--) {
        const b = state.burst[i];
        b.life -= dt;
        if (b.life <= 0) { state.burst.splice(i, 1); continue; }
        b.x += b.vx * dt;
        b.y += b.vy * dt;
        b.vy += 24 * dt;
        b.vx *= 0.985;
      }
    }

    function paintBurst() {
      for (let i = 0; i < state.burst.length; i++) {
        const b = state.burst[i];
        const a = clamp(b.life / b.maxLife, 0, 1);
        ctx.globalAlpha = a;
        ctx.fillStyle = b.color;
        ctx.beginPath();
        ctx.arc(b.x, b.y, Math.max(1, b.r * a), 0, TAU);
        ctx.fill();
      }
      ctx.globalAlpha = 1;
    }

    function draw() {
      if (state.destroyed || !state.w || !state.h) return;
      const c = palette();
      const t = state.time;
      const age = state.reduce ? 6 : Math.max(0, state.time - state.started);
      ctx.setTransform(state.dpr, 0, 0, state.dpr, 0, 0);
      ctx.clearRect(0, 0, state.w, state.h);
      paintSky(t);
      paintHills(t);
      paintGrass(t);
      paintPlants(t, age, c);
      if (!state.compact) paintMotes(t, c);
      paintBurst();
    }

    function frame(ts) {
      if (!state.running || state.destroyed) return;
      if (!state.last) state.last = ts;
      const dt = Math.min(0.05, Math.max(0, (ts - state.last) / 1000));
      state.last = ts;
      state.frameCount++;
      if (ts - state.fpsAt > 500) {
        state.fps = Math.round(state.frameCount * 1000 / (ts - state.fpsAt));
        state.frameCount = 0;
        state.fpsAt = ts;
      }
      if (!state.paused && !state.reduce) {
        state.time += dt;
        updateBurst(dt);
        state.wind = lerp(state.wind, state.windTarget, 0.035);
      }
      draw();
      state.raf = global.requestAnimationFrame(frame);
    }

    function start() {
      if (state.running || state.destroyed) return;
      state.running = true;
      state.last = 0;
      state.fpsAt = global.performance.now();
      state.frameCount = 0;
      if (!state.started) state.started = state.time;
      if (state.reduce) { draw(); return; }
      state.raf = global.requestAnimationFrame(frame);
    }

    function stop() {
      state.running = false;
      if (state.raf) global.cancelAnimationFrame(state.raf);
      state.raf = 0;
    }

    function burst(x, y, count) {
      for (let i = 0; i < count; i++) {
        const a = Math.random() * TAU;
        const speed = 28 + Math.random() * 70;
        state.burst.push({
          x: x,
          y: y,
          vx: Math.cos(a) * speed,
          vy: Math.sin(a) * speed - 24,
          life: 0.7 + Math.random() * 0.7,
          maxLife: 1.4,
          r: 1 + Math.random() * 2.2,
          color: Math.random() > 0.45 ? "#c9efae" : "#f4d98a"
        });
      }
    }

    function replay(seed) {
      state.seed = (seed == null ? newSeed() : seed) >>> 0;
      build();
      state.time = 0;
      state.started = 0;
      state.wind = 0.55;
      state.windTarget = 0.55;
      state.paused = false;
      burst(state.w * 0.5, state.h * 0.82, state.compact ? 12 : 34);
      if (state.reduce) draw();
      else if (!state.running) start();
    }

    function setWind(v) {
      state.windTarget = clamp(Number(v) || 0, -1.5, 1.5);
      if (!state.running && !state.reduce) start();
      if (state.reduce) draw();
    }

    function togglePause() {
      state.paused = !state.paused;
      if (!state.running && !state.paused) start();
      draw();
      return state.paused;
    }

    function destroy() {
      state.destroyed = true;
      stop();
      if (state.ro) { try { state.ro.disconnect(); } catch (e) {} }
      if (state.io) { try { state.io.disconnect(); } catch (e) {} }
      if (state.themeMo) { try { state.themeMo.disconnect(); } catch (e) {} }
      canvas.removeEventListener("pointerdown", onPointerDown);
      canvas.removeEventListener("pointermove", onPointerMove);
      canvas.removeEventListener("pointerleave", onPointerLeave);
      document.removeEventListener("visibilitychange", onVisibility);
      if (canvas.__gardenApi === api) canvas.__gardenApi = null;
      state.plants = [];
    }

    function onPointerDown(ev) {
      if (ev.button !== 0) return;
      const rect = canvas.getBoundingClientRect();
      replay();
      burst(ev.clientX - rect.left, ev.clientY - rect.top, state.compact ? 10 : 26);
    }
    function onPointerMove(ev) {
      const rect = canvas.getBoundingClientRect();
      state.pointerX = clamp((ev.clientX - rect.left) / Math.max(1, rect.width), 0, 1);
      state.windTarget = 0.55 + (state.pointerX - 0.5) * 1.7;
    }
    function onPointerLeave() {
      state.windTarget = 0.55;
    }
    function onVisibility() {
      if (document.hidden) stop();
      else if (state.visible && !state.paused) start();
    }

    const api = {
      replay: replay,
      resize: resize,
      setWind: setWind,
      togglePause: togglePause,
      destroy: destroy,
      get stats() {
        return {
          width: state.w,
          height: state.h,
          plants: state.plants.length,
          grass: state.grass.length,
          age: state.reduce ? 6 : Math.max(0, state.time - state.started),
          paused: state.paused,
          running: state.running,
          reducedMotion: state.reduce,
          wind: state.wind,
          fps: state.fps
        };
      }
    };

    canvas.classList.add("garden-canvas");
    canvas.__gardenApi = api;
    canvas.addEventListener("pointerdown", onPointerDown);
    canvas.addEventListener("pointermove", onPointerMove);
    canvas.addEventListener("pointerleave", onPointerLeave);
    document.addEventListener("visibilitychange", onVisibility);
    // 主题跟随：调色板在绘制时读取 data-theme，但空闲/不可见时 RAF 已停、画布停留在
    // 旧主题的最后一帧——监听属性变化立即重绘一帧，深浅切换永远及时。
    if (global.MutationObserver) {
      state.themeMo = new MutationObserver(function () { draw(); });
      state.themeMo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    }

    resize();
    if (global.ResizeObserver) {
      state.ro = new ResizeObserver(function () { resize(); });
      state.ro.observe(canvas);
    } else {
      global.addEventListener("resize", resize);
    }
    if (global.IntersectionObserver) {
      state.io = new IntersectionObserver(function (entries) {
        state.visible = !!entries[0] && entries[0].isIntersecting;
        if (state.visible && !document.hidden && !state.paused) start();
        else stop();
      }, { threshold: 0.05 });
      state.io.observe(canvas);
    } else {
      start();
    }
    if (!state.io) start();
    // 初次挂载先画一帧，即使外层 hidden 也不会出现空白
    draw();
    return api;
  }

  function mount(canvas, options) {
    if (!canvas || !canvas.getContext) return null;
    if (canvas.__gardenApi && canvas.__gardenApi.destroy) {
      try { canvas.__gardenApi.destroy(); } catch (e) {}
    }
    return startScene(canvas, options || {});
  }

  function selfCheck(host) {
    const root = host || document;
    const canvas = root.querySelector ? root.querySelector("canvas.garden-canvas") : null;
    const api = canvas && canvas.__gardenApi;
    const stats = api ? api.stats : null;
    const checks = [
      { name: "画布已挂载", ok: !!canvas },
      { name: "场景已启动", ok: !!api && stats.plants > 0 },
      { name: "透视场景", ok: !!stats && stats.width > 0 && stats.height > 0 }
    ];
    return {
      ok: checks.every(function (c) { return c.ok; }),
      pass: checks.filter(function (c) { return c.ok; }).length,
      total: checks.length,
      checks: checks,
      stats: stats,
      version: VERSION
    };
  }

  global.GreenTigerGarden = {
    version: VERSION,
    mount: mount,
    selfCheck: selfCheck
  };
})(window);
