/**
 * The home page's motion: the line-art figures, the pinned scene, the replaying mocks, the pill clock,
 * the session ribbon and Ctrl+Enter on the form. The page is complete without any of it.
 *
 * It never writes HTML from strings (no innerHTML), so the site's Content-Security-Policy can require
 * Trusted Types and allow no script but this file.
 */

const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
document.documentElement.classList.add('js');

const byId = <T extends HTMLElement | SVGElement = HTMLElement>(id: string) => document.getElementById(id) as T | null;
const clamp = (v: number, a = 0, b = 1) => Math.min(b, Math.max(a, v));
const ease = (v: number) => 1 - Math.pow(1 - v, 3);
const fmt = (v: number) => `${String(Math.floor(v / 60)).padStart(2, '0')}:${String(Math.floor(v % 60)).padStart(2, '0')}`;

/* ---------- Mocks replay once when they come into view ---------- */
const replay = new IntersectionObserver(
  (entries) => entries.forEach((e) => {
    if (e.isIntersecting && !reduce) {
      e.target.classList.add('play');
      replay.unobserve(e.target);
    }
  }),
  { threshold: 0.35 },
);
document.querySelectorAll('[data-replay]').forEach((el) => replay.observe(el));

const mockClock = document.querySelector<HTMLElement>('[data-clock]');
if (mockClock && !reduce) {
  let secs = 137;
  let timer = 0;
  // Ticks only while its mock is on screen.
  new IntersectionObserver(([e]) => {
    if (e?.isIntersecting && !timer) timer = window.setInterval(() => { secs += 1; mockClock.textContent = fmt(secs); }, 1000);
    else if (!e?.isIntersecting && timer) { clearInterval(timer); timer = 0; }
  }).observe(mockClock);
}

/* ---------- Line-art figures: 1px Iris strokes on canvas, drifting slowly ---------- */
const IRIS = '#8f74dc';
const FAINT = 'rgba(143,116,220,0.35)';
const INK = '#141030';
type Draw = (c: CanvasRenderingContext2D, w: number, h: number, t: number) => void;
const draw: Record<string, Draw> = {
  voice(c, w, h, t) {
    const n = 46, top = 28, bot = h - 14;
    for (let i = 0; i < n; i++) {
      const x = 12 + (w - 24) * i / (n - 1);
      const a = Math.sin(i * 0.33 + t * 1.4) * 0.5 + Math.sin(i * 0.11 - t * 0.7) * 0.5;
      const amp = (bot - top) / 2 * (0.15 + 0.85 * Math.abs(a));
      const mid = (top + bot) / 2;
      c.strokeStyle = i % 2 ? FAINT : IRIS;
      c.beginPath(); c.moveTo(x, mid - amp); c.lineTo(x, mid + amp); c.stroke();
    }
  },
  ribbon(c, w, h, t) {
    // The real mark as line art: both ribbon hooks hatched, the fold cross-hatched, a brighter band
    // sweeping along the ribbon, and a note being typed on the screen.
    const s = Math.min(w - 36, h - 44) / 64, ox = (w - 64 * s) / 2, oy = (h - 64 * s) / 2 + 9;
    const hook = new Path2D('M60 4 L54 16 H21 A5 5 0 0 0 21 26 H32 V38 H21 A17 17 0 0 1 21 4 Z');
    const fold = new Path2D('M16.17 19.71 L-10.04 29.05 L-8 64 L44 64 L44 19.71 Z');
    const sweep = (t * 14) % 150 - 75;
    c.save(); c.translate(ox, oy); c.scale(s, s); c.lineWidth = 1 / s;
    for (let r = 0; r < 2; r++) {
      c.save();
      if (r) { c.translate(32, 32); c.rotate(Math.PI); c.translate(-32, -32); }
      c.save(); c.clip(hook);
      for (let k = -72; k < 72; k += 1.7) {
        c.strokeStyle = Math.abs(k - sweep) < 5 ? IRIS : FAINT;
        c.beginPath(); c.moveTo(k - 10, -10); c.lineTo(k + 80, 80); c.stroke();
      }
      c.clip(fold);
      c.strokeStyle = FAINT;
      for (let k = -72; k < 150; k += 1.7) { c.beginPath(); c.moveTo(k, -10); c.lineTo(k - 90, 80); c.stroke(); }
      c.restore();
      c.strokeStyle = IRIS; c.stroke(hook);
      c.restore();
    }
    c.save(); c.beginPath(); c.roundRect(13, 17, 38, 30, 7); c.clip(); c.clearRect(-20, -20, 110, 110); c.restore();
    c.strokeStyle = IRIS; c.beginPath(); c.roundRect(16, 20, 32, 24, 4); c.stroke();
    c.fillStyle = IRIS;
    [21, 25.5, 30].forEach((x) => { c.beginPath(); c.arc(x, 25.5, 1.25, 0, Math.PI * 2); c.fill(); });
    const p = Math.min(1, (t * 0.35) % 1.4);
    c.fillStyle = INK; c.fillRect(20, 30.5, 22, 2.2);
    c.fillStyle = IRIS; c.fillRect(20, 36.5, 2 + 12 * p, 2.2);
    c.restore();
  },
  windows(c, w, h, t) {
    for (let k = 2; k >= 0; k--) {
      const x = 26 + k * 16, y = 30 + k * 10, ww = w - 80, hh = h - 64;
      c.strokeStyle = k ? FAINT : IRIS;
      c.strokeRect(x + 0.5, y + 0.5, ww, hh);
      c.beginPath(); c.moveTo(x, y + 10.5); c.lineTo(x + ww, y + 10.5); c.stroke();
    }
    const px = 26 + (w - 80) * (0.25 + 0.5 * (Math.sin(t * 0.8) * 0.5 + 0.5));
    const py = 30 + (h - 64) * (0.45 + 0.25 * Math.sin(t * 1.3));
    c.fillStyle = INK; c.beginPath(); c.moveTo(px, py); c.lineTo(px + 7, py + 11); c.lineTo(px + 2, py + 9); c.lineTo(px, py + 14); c.closePath(); c.fill();
    c.strokeStyle = IRIS; c.beginPath(); c.arc(px, py, 6 + (t * 6 % 6), 0, Math.PI * 2); c.stroke();
  },
  frames(c) {
    for (let k = 0; k < 5; k++) {
      const x = 18 + k * 30, y = 40 + k * 14, fw = 70, fh = 50;
      c.strokeStyle = k === 2 ? IRIS : FAINT;
      c.beginPath(); c.moveTo(x, y); c.lineTo(x + fw, y - 14); c.lineTo(x + fw, y - 14 + fh); c.lineTo(x, y + fh); c.closePath(); c.stroke();
      if (k === 2) { c.fillStyle = INK; c.beginPath(); c.moveTo(x + 14, y + 16); c.lineTo(x + 52, y + 8); c.lineTo(x + 52, y + 16); c.lineTo(x + 14, y + 24); c.closePath(); c.fill(); }
    }
  },
  mask(c, w, _h, t) {
    const gx = 16, gy = 12;
    for (let i = 0; i < gx; i++) for (let j = 0; j < 5; j++) {
      const x = 12 + i * (w - 24) / (gx - 1), y = 30 + j * gy;
      const masked = i > 4 && i < 12 && j === 2;
      c.fillStyle = masked ? INK : ((i + j + Math.floor(t * 2)) % 7 ? FAINT : IRIS);
      if (masked) c.fillRect(x - 4, y - 3, 9, 6); else c.fillRect(x - 1, y - 1, 2, 2);
    }
  },
  clicks(c, w, h, t) {
    const pts = [[0.15, 0.75], [0.3, 0.45], [0.48, 0.62], [0.62, 0.35], [0.8, 0.55], [0.88, 0.3]].map(([a, b]) => [a! * w, 20 + b! * (h - 26)] as const);
    c.strokeStyle = FAINT; c.beginPath(); pts.forEach(([x, y], i) => (i ? c.lineTo(x, y) : c.moveTo(x, y))); c.stroke();
    pts.forEach(([x, y], i) => { c.fillStyle = i === pts.length - 1 ? INK : IRIS; c.beginPath(); c.arc(x, y, 2.5, 0, Math.PI * 2); c.fill(); });
    const [lx, ly] = pts[pts.length - 1]!;
    c.strokeStyle = IRIS; c.beginPath(); c.arc(lx, ly, 4 + (t * 5 % 7), 0, Math.PI * 2); c.stroke();
  },
};

type Fig = { el: HTMLCanvasElement; ctx: CanvasRenderingContext2D; fn: Draw; on: boolean; w: number; h: number };
const figs: Fig[] = [];
document.querySelectorAll<HTMLCanvasElement>('canvas[data-draw]').forEach((el) => {
  const ctx = el.getContext('2d');
  const fn = draw[el.dataset.draw ?? ''];
  if (ctx && fn) figs.push({ el, ctx, fn, on: true, w: 0, h: 0 });
});
function sizeFig(o: Fig) {
  const r = o.el.getBoundingClientRect(), d = Math.min(2, devicePixelRatio || 1);
  o.w = r.width; o.h = r.height;
  o.el.width = Math.max(1, r.width * d); o.el.height = Math.max(1, r.height * d);
  o.ctx.setTransform(d, 0, 0, d, 0, 0);
}
function paint(t: number) {
  figs.forEach((o) => {
    if (!o.on || !o.w) return;
    o.ctx.clearRect(0, 0, o.w, o.h); o.ctx.lineWidth = 1;
    o.fn(o.ctx, o.w, o.h, t / 1000);
  });
}
figs.forEach(sizeFig);
paint(0);
// The loop runs only while a figure is on screen, and stops when none is.
let looping = false;
let last = 0;
const loop = (t: number) => {
  if (!figs.some((f) => f.on)) { looping = false; return; }
  if (t - last > 33) { paint(t); last = t; }
  requestAnimationFrame(loop);
};
const startLoop = () => { if (!reduce && !looping) { looping = true; requestAnimationFrame(loop); } };
const figVisible = new IntersectionObserver((es) => {
  es.forEach((e) => {
    const o = figs.find((f) => f.el === e.target);
    if (o) o.on = e.isIntersecting;
  });
  startLoop();
});
figs.forEach((o) => figVisible.observe(o.el));
// A rotation or a resized window changes each canvas's box; its pixels follow.
addEventListener('resize', () => { figs.forEach(sizeFig); paint(performance.now()); });

/* ---------- The pinned scene: scroll runs four beats ----------
   0 to .22 the problem wall; .22 to .48 every tile gets its note (the tally runs 38 to 46);
   .48 to .70 the tiles fly together; .62 to .92 they become the note for #48213; .88 published. */
const sceneEl = byId('scene');
const wall = byId('wall');
const stageEl = byId('stage');
const tallyEl = byId('tally');
const noteEl = byId('scene-note');
if (sceneEl && wall && stageEl && tallyEl && noteEl) {
  const tiles = Array.from(wall.children) as HTMLElement[];
  const caps = Array.from(document.querySelectorAll<HTMLElement>('.cap'));
  const noteParts = Array.from(noteEl.querySelectorAll<HTMLElement>('.sn-body h3, .sn-body .ln'));
  const stamp = noteEl.querySelector<HTMLElement>('.sn-stamp');
  let offsets: [number, number][] = [];
  let lastBeat = -1;
  const measure = () => {
    tiles.forEach((t) => { t.style.transform = ''; });
    const st = stageEl.getBoundingClientRect();
    const cx = st.left + st.width / 2, cy = st.top + st.height / 2;
    offsets = tiles.map((t) => {
      const r = t.getBoundingClientRect();
      return [cx - (r.left + r.width / 2), cy - (r.top + r.height / 2)];
    });
  };
  const tick = () => {
    const r = sceneEl.getBoundingClientRect();
    const p = clamp(-r.top / Math.max(1, r.height - innerHeight + 60));
    const beat = p < 0.22 ? 0 : p < 0.48 ? 1 : p < 0.74 ? 2 : 3;
    if (beat !== lastBeat) {
      lastBeat = beat;
      // The caption on screen is the one assistive technology reads.
      caps.forEach((c, i) => {
        c.classList.toggle('on', i === beat);
        if (i === beat) c.removeAttribute('aria-hidden'); else c.setAttribute('aria-hidden', 'true');
      });
    }
    tiles.forEach((t, i) => {
      const at = t.classList.contains('bad') ? 0.02 + (i % 8) * 0.018 : Number(t.dataset.r) * 0.2;
      t.classList.toggle('healed', p > 0.24 + at);
    });
    const target = Math.round(38 + 8 * clamp((p - 0.24) / 0.2));
    tallyEl.textContent = String(target);
    tallyEl.classList.toggle('full', target === 46);
    tiles.forEach((t, i) => {
      const q = ease(clamp((p - 0.48 - Number(t.dataset.r) * 0.06) / 0.18));
      const off = offsets[i];
      if (!off) return;
      t.style.transform = q ? `translate(${off[0] * q}px, ${off[1] * q}px) scale(${1 - 0.75 * q})` : '';
      t.style.opacity = q ? String(1 - q) : '';
    });
    const n = ease(clamp((p - 0.62) / 0.12));
    noteEl.style.opacity = String(n);
    noteEl.style.transform = `scale(${0.6 + 0.4 * n})`;
    noteParts.forEach((el, i) => el.style.setProperty('--o', clamp((p - 0.72 - i * 0.022) / 0.04).toFixed(3)));
    stamp?.classList.toggle('on', p > 0.88);
  };
  if (!reduce) {
    document.documentElement.classList.add('js-scene');
    measure();
    let queued = false;
    addEventListener('scroll', () => {
      if (queued) return;
      queued = true;
      requestAnimationFrame(() => { queued = false; tick(); });
    }, { passive: true });
    addEventListener('resize', () => { measure(); tick(); });
    void document.fonts?.ready.then(() => { measure(); tick(); });
    tick();
  } else {
    tiles.forEach((t) => t.classList.add('healed'));
    tallyEl.textContent = '46';
    tallyEl.classList.add('full');
  }
}

/* ---------- The pill clock: session time runs 00:00 to 10:00 down the page ---------- */
const pill = byId('clockpill');
const pillLabel = byId('cp-label');
const pillClock = byId('cp-clock');
const noteClock = byId('note-clock');
const lastRow = document.querySelector<HTMLElement>('.feature[data-pill="published"]');
if (pill && pillLabel && pillClock && lastRow) {
  const names: Record<string, string> = { rec: 'Recording', paused: 'Paused: sensitive field', drafting: 'Drafting', published: 'Published to #48213' };
  const marks = Array.from(document.querySelectorAll<HTMLElement>('[data-pill]'));
  const hero = document.querySelector<HTMLElement>('.hero');
  const form = document.getElementById('pilot-form');
  const narrow = matchMedia('(max-width: 960px)');
  let raf = 0;
  const tick = () => {
    raf = 0;
    const endY = lastRow.getBoundingClientRect().top + scrollY - innerHeight * 0.5;
    const t = fmt(clamp(scrollY / Math.max(1, endY)) * 600);
    pillClock.textContent = t;
    if (noteClock) noteClock.textContent = t;
    let s = 'rec';
    for (const m of marks) if (m.getBoundingClientRect().top <= innerHeight * 0.55) s = m.dataset.pill ?? s;
    if (pill.dataset.s !== s) { pill.dataset.s = s; pillLabel.textContent = names[s] ?? ''; }
    // Shown once the hero is passed, so it never covers the hero's buttons; on narrow screens it steps
    // aside while the form is in view, so it never covers a field.
    const heroBottom = hero ? hero.getBoundingClientRect().bottom : 0;
    const formRect = form?.getBoundingClientRect();
    const formInView = !!formRect && formRect.top < innerHeight && formRect.bottom > 0;
    pill.classList.toggle('shown', heroBottom < innerHeight * 0.4 && !(narrow.matches && formInView));
  };
  addEventListener('scroll', () => { if (!raf) raf = requestAnimationFrame(tick); }, { passive: true });
  addEventListener('resize', tick);
  tick();
}

/* ---------- The session ribbon: the logo's band threaded through the events ---------- */
const eventsBox = byId('events');
const ribbon = byId<SVGSVGElement>('ribbon');
if (eventsBox && ribbon) {
  const NS = 'http://www.w3.org/2000/svg';
  const drawRibbon = () => {
    const box = eventsBox.getBoundingClientRect();
    const pts = Array.from(eventsBox.querySelectorAll('.knot')).map((k) => {
      const r = k.getBoundingClientRect();
      return { x: r.left - box.left + 16, y: r.top - box.top + r.height / 2 };
    });
    if (pts.length < 2) return;
    const first = pts[0]!, lastPt = pts[pts.length - 1]!;
    const column = Math.abs(pts[1]!.x - first.x) < 4, W = column ? 6 : 10, edge = box.width + 6;
    const k = Math.max(16, Math.min(80, (innerWidth - box.right - 8) / 0.75));
    let d = column ? `M ${first.x} ${first.y - 40}` : `M ${-box.left} ${first.y}`;
    d += ` L ${first.x} ${first.y}`;
    const folds: string[] = [];
    for (let i = 1; i < pts.length; i++) {
      const a = pts[i - 1]!, b = pts[i]!;
      if (column || Math.abs(a.y - b.y) < 4) { d += ` L ${b.x} ${b.y}`; continue; }
      const turn = `C ${edge + k} ${a.y} ${edge + k} ${b.y} ${edge} ${b.y}`;
      d += ` L ${edge} ${a.y} ${turn} L ${b.x} ${b.y}`;
      folds.push(`M ${edge} ${a.y} ${turn}`);
    }
    if (!column) d += ` L ${-box.left} ${lastPt.y}`;
    const path = (cls: string, dd: string) => {
      const el = document.createElementNS(NS, 'path');
      el.setAttribute('class', cls);
      el.setAttribute('d', dd);
      el.setAttribute('stroke-width', String(W));
      return el;
    };
    ribbon.replaceChildren(path('band', d), ...folds.map((f) => path('fold', f)));
  };
  addEventListener('resize', drawRibbon);
  drawRibbon();
  void document.fonts?.ready.then(drawRibbon);
}

/* ---------- Ctrl+Enter sends the form, as Publish does in the app ---------- */
const form = byId<HTMLFormElement>('pilot-form');
form?.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
    e.preventDefault();
    form.requestSubmit();
  }
});
