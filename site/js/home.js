// The home page: tabs, the videos recorded from the app, the feature tiles' small animations, and the dot floor behind the hero.
// In its own scope: names like `top` are taken by the browser's window object (a global `const top` breaks the whole script).
(() => {
document.documentElement.classList.add('js'); // what waits to be revealed stays hidden only when this script runs
const still = matchMedia('(prefers-reduced-motion: reduce)');

// Tabs: click or arrow keys switch the video and its caption. A marker slides under the chosen tab.
const tabs = [...document.querySelectorAll('[role="tab"]')];
const caption = document.getElementById('caption');
const tabList = document.querySelector('[role="tablist"]');
function mark() {
  const on = tabs.find(t => t.getAttribute('aria-selected') === 'true');
  if (!on) return;
  tabList.style.setProperty('--x', `${on.offsetLeft}px`);
  tabList.style.setProperty('--w', `${on.offsetWidth}px`);
}
function select(tab) {
  for (const t of tabs) {
    const on = t === tab;
    t.setAttribute('aria-selected', on);
    t.tabIndex = on ? 0 : -1;
    document.getElementById(t.getAttribute('aria-controls')).hidden = !on;
  }
  const panel = document.getElementById(tab.getAttribute('aria-controls'));
  caption.textContent = panel.dataset.caption;
  const video = panel.querySelector('video');
  if (video?.getAttribute('src')) video.currentTime = 0; // each tab's video starts from the beginning
  mark();
}
mark();
new ResizeObserver(mark).observe(tabList);
document.fonts?.ready.then(mark); // the tabs' widths change once Inter has loaded
tabs.forEach((t, i) => {
  t.addEventListener('click', () => select(t));
  t.addEventListener('keydown', e => {
    const step = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
    if (!step) return;
    const next = tabs[(i + step + tabs.length) % tabs.length];
    next.focus(); select(next);
  });
});
// A hairline under the top bar once the page scrolls.
const bar = document.getElementById('top');
addEventListener('scroll', () => bar.classList.toggle('scrolled', scrollY > 8), { passive: true });

// The videos recorded from the app (media/<name>-<theme>.mp4, first frame as .jpg): the visitor's theme, loaded only
// once on screen (hidden tabs never are), playing only while visible; just the first frame for reduced motion.
const dark = matchMedia('(prefers-color-scheme: dark)');
const videos = [...document.querySelectorAll('video[data-name]')];
const seen = new WeakSet();
function show(video) {
  const file = `media/${video.dataset.name}-${dark.matches ? 'dark' : 'light'}`;
  video.poster = `${file}.jpg`;
  if (still.matches || !seen.has(video)) { if (video.getAttribute('src')) { video.removeAttribute('src'); video.load(); } return; }
  if (video.getAttribute('src') !== `${file}.mp4`) video.src = `${file}.mp4`;
  video.play().catch(() => {});
}
const watcher = new IntersectionObserver(entries => {
  for (const { target, isIntersecting } of entries) {
    if (isIntersecting) { seen.add(target); show(target); }
    else target.pause();
  }
});
for (const video of videos) { show(video); watcher.observe(video); }
const refresh = () => videos.forEach(show);
dark.addEventListener('change', refresh);
still.addEventListener('change', refresh);

// Feature tiles: their small animations start once a tile is on screen (and the numbers' columns rise). The search box types and the Reply button
// changes language only while theirs is on screen; for reduced motion both just show their result.
const tiles = new IntersectionObserver(entries => {
  for (const { target, isIntersecting } of entries)
    if (isIntersecting) { target.classList.add('in'); tiles.unobserve(target); }
}, { rootMargin: '0px 0px -12% 0px' });
document.querySelectorAll('.tile, .numbers').forEach(t => tiles.observe(t));

// Runs step() every `ms` while `el` is on screen and the page is visible.
function whileVisible(el, ms, step) {
  let timer = 0, visible = false;
  const sync = () => {
    const go = visible && !document.hidden && !still.matches;
    if (go && !timer) timer = setInterval(step, ms);
    if (!go && timer) { clearInterval(timer); timer = 0; }
  };
  new IntersectionObserver(([e]) => { visible = e.isIntersecting; sync(); }).observe(el);
  document.addEventListener('visibilitychange', sync);
  still.addEventListener('change', sync);
}

// Search: types the words letter by letter, shows what it finds, waits, and starts again.
const finder = document.querySelector('.finder');
if (finder) {
  const q = finder.querySelector('.q'), words = q.dataset.type, hint = q.innerHTML;
  let tick = 0;
  const show = n => {
    if (n === 0) { q.innerHTML = hint; q.classList.remove('typing'); }
    else { q.textContent = words.slice(0, n); q.classList.add('typing'); }
    finder.classList.toggle('empty', n < words.length);
  };
  if (still.matches) show(words.length);
  else {
    show(0);
    // 0-9: the hint; then a letter a tick; then the results stay for about 4 seconds.
    whileVisible(finder, 110, () => {
      tick = (tick + 1) % (10 + words.length + 36);
      show(Math.max(0, Math.min(words.length, tick - 10)));
    });
  }
  still.addEventListener('change', () => show(still.matches ? words.length : 0));
}

// In your language: the Reply button in each of the app's languages, each with one of its accent colours.
const reply = document.querySelector('.reply');
if (reply) {
  const say = [['Reply', 'English'], ['Antworten', 'Deutsch'], ['Răspunde', 'Română'], ['Responder', 'Español']];
  const word = reply.querySelector('.word'), tongue = document.querySelector('.tongue');
  const swatches = [...document.querySelectorAll('.swatches i')];
  let n = 0;
  const paint = () => {
    const c = swatches[n % swatches.length];
    swatches.forEach(s => s.classList.toggle('on', s === c));
    reply.style.setProperty('--c', c.style.getPropertyValue('--c'));
  };
  paint();
  whileVisible(reply, 2200, () => {
    n++;
    word.classList.add('out'); tongue.style.opacity = 0;
    setTimeout(() => {
      [word.textContent, tongue.textContent] = say[n % say.length];
      word.classList.remove('out'); tongue.style.opacity = '';
      paint();
    }, 250);
  });
}

// Free: the currency sign rolls past a few others, quickly, and lands on the next; the 0 stays put.
const cur = document.querySelector('.cur');
if (cur) {
  const reel = cur.firstElementChild;
  const signs = ['€', '$', '£', '¥', 'lei', '₹', 'zł', '₩', 'kr', 'R$', '₺', 'CHF'];
  const sign = text => Object.assign(document.createElement('b'), { textContent: text });
  let at = 0, rolling = false;
  reel.addEventListener('transitionend', () => {
    reel.replaceChildren(sign(signs[at]));
    reel.style.transition = 'none'; reel.style.transform = '';
    rolling = false;
  });
  whileVisible(cur, 2000, () => {
    if (rolling) return;
    rolling = true;
    const from = at;
    at = (at + 1) % signs.length;
    // Four others flick past on the way, then the next one.
    const path = [from, ...[3, 5, 7, 9].map(k => (from + k) % signs.length), at].map(i => sign(signs[i]));
    cur.style.width = `${cur.offsetWidth}px`;
    reel.replaceChildren(...path);
    const last = path[path.length - 1];
    void reel.offsetHeight; // lay the reel out at the top before it moves
    reel.style.transition = 'transform .6s cubic-bezier(.15, .75, .2, 1)';
    reel.style.transform = `translateY(-${last.offsetTop}px)`;
    cur.style.width = `${last.offsetWidth}px`;
  });
}

// Phishing warnings: the app's warnings take turns.
const banners = [...document.querySelectorAll('.banners .banner')];
if (banners.length > 1) {
  let shown = 0;
  whileVisible(banners[0].parentElement, 4200, () => {
    banners[shown].classList.remove('on');
    shown = (shown + 1) % banners.length;
    banners[shown].classList.add('on');
  });
}

// Hero backdrop: a dot grid laid out as a floor that runs back into the distance. Every so often one or a few signals
// run along its rows, left to right or right to left. The dots a signal passes light up and fade behind it, leaving a short trail.
const canvas = document.getElementById('dots');
const ctx = canvas.getContext('2d');
const FAR = 4;     // depth of the back row; the front row is at 1
const FADE = 0.5;  // seconds for a lit dot to fade to about a third
let w = 0, h = 0, cx = 0, horizon = 0, depth = 0, step = 0, rows = [], signals = [];
let prev = 0, wait = 0, running = false, onScreen = true, base = '', core = '', halo = null;

function readColours() {
  const css = getComputedStyle(document.documentElement);
  base = css.getPropertyValue('--dot').trim();
  core = css.getPropertyValue('--spark').trim();
  // One soft round glow, drawn once and stamped behind every lit dot.
  const n = parseInt(core.slice(1), 16), rgb = `${n >> 16 & 255},${n >> 8 & 255},${n & 255}`;
  halo = document.createElement('canvas'); halo.width = halo.height = 48;
  const g = halo.getContext('2d'), grad = g.createRadialGradient(24, 24, 0, 24, 24, 24);
  grad.addColorStop(0, `rgba(${rgb},.5)`); grad.addColorStop(.3, `rgba(${rgb},.14)`); grad.addColorStop(1, `rgba(${rgb},0)`);
  g.fillStyle = grad; g.fillRect(0, 0, 48, 48);
}

// The ground's height under a dot, as a share of the eye's height above the floor: two soft hills, one dip in front
// of the middle, and a faint ripple everywhere. u is across (-1…1 spans the front row), z is depth (1 front, FAR back).
const bump = (u, z, u0, z0, su, sz, a) => a * Math.exp(-(((u - u0) / su) ** 2) - (((z - z0) / sz) ** 2));
const ground = (u, z) =>
  bump(u, z, -.7, 2.3, .55, .65, .15) + bump(u, z, .75, 3.1, .7, .7, .17) + bump(u, z, .05, 1.55, .5, .35, -.07)
  + .015 * Math.sin(u * 5 + z * 1.7);

// Perspective: a dot k columns from the centre, z deep, standing g high, is drawn at
// (cx + k*step/z, horizon + depth*(1 - g)/z): raised ground comes up the screen, and less so the farther away it is.
const yAt = (k, z) => horizon + depth * (1 - ground(k * step / cx, z)) / z;
function layout() {
  const r = canvas.getBoundingClientRect(), dpr = Math.min(devicePixelRatio || 1, 2);
  w = r.width; h = r.height;
  canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr);
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  const narrow = w < 700, back = h * .07;  // where the back row sits
  cx = w / 2;
  depth = (h - back) / (1 - 1 / FAR);      // puts the front row on the bottom edge
  horizon = h - depth;
  step = narrow ? 40 : 54;                 // dot spacing on the front row, in px
  const count = narrow ? 46 : 60;
  rows = [];
  // Rows evenly spaced in depth, so they bunch up towards the back as on a real floor.
  for (let i = 0; i < count; i++) {
    const z = 1 + (FAR - 1) * i / (count - 1), half = Math.ceil(cx * z / step) + 1, dots = [];
    for (let k = -half; k <= half; k++) dots.push({ x: cx + k * step / z, y: yAt(k, z), lit: 0, peak: .35 + Math.random() * .3 });
    rows.push({ z, half, dots, near: 1 - (z - 1) / (FAR - 1) }); // near: 1 at the front, 0 at the back
  }
  signals = []; wait = 0;
}

function launch() {
  const n = Math.random() < .3 ? 2 + (Math.random() < .4) : 1; // now and then two or three at once
  for (let i = 0; i < n && signals.length < 7; i++) {
    const row = rows[Math.floor(Math.random() * rows.length)], dir = Math.random() < .5 ? 1 : -1;
    signals.push({ row, dir, k: -dir * (row.half + 1), speed: 9 + Math.random() * 10 }); // columns per second
  }
  wait = .4 + Math.random() * 1.3;
}

function draw(dt) {
  ctx.clearRect(0, 0, w, h);
  // The floor: dots shrink and fade with distance.
  ctx.fillStyle = base;
  for (const row of rows) {
    const rad = .7 + 1.0 * row.near;
    ctx.globalAlpha = .5 + .5 * row.near;
    ctx.beginPath();
    for (const d of row.dots) { ctx.moveTo(d.x + rad, d.y); ctx.arc(d.x, d.y, rad, 0, 7); }
    ctx.fill();
  }
  if (dt === null) { ctx.globalAlpha = 1; return; }

  // Move the signals; every dot a signal passed since the last frame lights up, so a slow frame never leaves gaps.
  if ((wait -= dt) <= 0) launch();
  for (const s of signals) {
    const from = s.k; s.k += s.dir * s.speed * dt;
    const lo = Math.min(from, s.k) - .5, hi = Math.max(from, s.k) + .5, off = s.row.half;
    for (let k = Math.ceil(lo); k <= Math.floor(hi); k++) {
      const d = s.row.dots[k + off];
      if (d) d.lit = Math.max(d.lit, d.peak);
    }
  }
  signals = signals.filter(s => Math.abs(s.k) <= s.row.half + 2);

  // Lit dots: a glow plus a brighter, larger dot, both fading over time.
  const fade = Math.exp(-dt / FADE);
  ctx.fillStyle = core;
  for (const row of rows) {
    const scale = .5 + .5 * row.near;
    for (const d of row.dots) {
      if (d.lit < .02) { d.lit = 0; continue; }
      d.lit *= fade;
      const s = (8 + 12 * d.lit) * scale;
      ctx.globalAlpha = d.lit;
      ctx.drawImage(halo, d.x - s, d.y - s, s * 2, s * 2);
      ctx.globalAlpha = Math.min(1, .15 + d.lit);
      ctx.beginPath(); ctx.arc(d.x, d.y, (1.1 + 1.1 * d.lit) * scale, 0, 7); ctx.fill();
    }
  }
  // Each signal's head: a brighter point between dots.
  for (const s of signals) {
    const scale = .5 + .5 * s.row.near, x = cx + s.k * step / s.row.z, y = yAt(s.k, s.row.z), g = 22 * scale;
    ctx.globalAlpha = .6;
    ctx.drawImage(halo, x - g, y - g, g * 2, g * 2);
    ctx.beginPath(); ctx.arc(x, y, 2.4 * scale, 0, 7); ctx.fill();
  }
  ctx.globalAlpha = 1;
}

function frame(now) {
  if (!running) return;
  const t = now / 1000, dt = prev ? Math.min(t - prev, .1) : 0;
  prev = t;
  draw(dt);
  requestAnimationFrame(frame);
}

function update() {
  const go = onScreen && !still.matches && !document.hidden;
  if (go && !running) { running = true; prev = 0; requestAnimationFrame(frame); }
  if (!go) { running = false; if (still.matches) { for (const row of rows) for (const d of row.dots) d.lit = 0; signals = []; draw(null); } }
}

readColours(); layout(); draw(null);
new ResizeObserver(() => { layout(); if (!running) draw(null); }).observe(canvas);
new IntersectionObserver(([e]) => { onScreen = e.isIntersecting; update(); }).observe(canvas);
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => { readColours(); if (!running) draw(null); });
still.addEventListener('change', update);
document.addEventListener('visibilitychange', update);
update();
})();
