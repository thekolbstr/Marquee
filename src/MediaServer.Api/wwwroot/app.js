'use strict';

// All DOM is built with textContent / attributes (never HTML strings): titles and file names come from disk and TMDB.

// ================================================================ helpers
const $ = (sel, root = document) => root.querySelector(sel);
const view = $('#view');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat(Infinity)) if (kid != null && kid !== false) el.append(kid.nodeType ? kid : document.createTextNode(String(kid)));
  return el;
}

const SVG_NS = 'http://www.w3.org/2000/svg';
const ICONS = {
  play: { d: 'M8 5.14v13.72a1 1 0 0 0 1.5.86l11-6.86a1 1 0 0 0 0-1.72l-11-6.86A1 1 0 0 0 8 5.14z', fill: true },
  back: { d: 'M15 18l-6-6 6-6' },
  left: { d: 'M15 18l-6-6 6-6' },
  right: { d: 'M9 18l6-6-6-6' },
  check: { d: 'M20 6L9 17l-5-5' },
  refresh: { d: 'M21 12a9 9 0 1 1-2.64-6.36M21 4v5h-5' },
  next: { d: 'M5 5v14l10-7zM19 5v14', fill: true },
  star: { d: 'M12 2.5l2.9 6.1 6.6.9-4.8 4.6 1.2 6.6L12 17.6l-5.9 3.1 1.2-6.6-4.8-4.6 6.6-.9z' },
  list: { d: 'M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01' },
  close: { d: 'M18 6L6 18M6 6l12 12' },
  pause: { d: 'M7 4h3.5v16H7zM13.5 4H17v16h-3.5z', fill: true },
  rew: { d: 'M3 12a9 9 0 1 0 3-6.7M3 4v5h5' },
  fwd: { d: 'M21 12a9 9 0 1 1-3-6.7M21 4v5h-5' },
  volume: { d: 'M11 5L6 9H3v6h3l5 4zM15.5 8.5a5 5 0 0 1 0 7M18.5 5.5a9 9 0 0 1 0 13' },
  mute: { d: 'M11 5L6 9H3v6h3l5 4zM22 9l-6 6M16 9l6 6' },
  expand: { d: 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5' },
  cc: { d: 'M4 5h16a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1zM10 10.2a2.2 2.2 0 1 0 0 3.6M17 10.2a2.2 2.2 0 1 0 0 3.6' },
  dice: { d: 'M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM8 8h.01M16 8h.01M12 12h.01M8 16h.01M16 16h.01' },
  gear: { d: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.9.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.9 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.9l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.9.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.9-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.9V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z' },
  eye: { d: 'M1 12s4-7 11-7 11 7 11 7-4 7-11 7S1 12 1 12zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z' },
  plus: { d: 'M12 5v14M5 12h14' },
  folder: { d: 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z' },
  sliders: { d: 'M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6' },
};
function icon(name) {
  const def = ICONS[name];
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  const p = document.createElementNS(SVG_NS, 'path');
  p.setAttribute('d', def.d);
  if (def.fill) svg.setAttribute('fill', 'currentColor');
  else { svg.setAttribute('fill', 'none'); svg.setAttribute('stroke', 'currentColor'); svg.setAttribute('stroke-width', '2.2'); svg.setAttribute('stroke-linecap', 'round'); svg.setAttribute('stroke-linejoin', 'round'); }
  svg.append(p);
  return svg;
}

async function api(path, opts) {
  const res = await fetch(path, opts);
  if (res.status === 401 && !path.startsWith('/api/auth/')) { authLost(); throw new Error('Signed out'); }
  if (!res.ok) throw new Error(res.status === 404 ? 'Not found' : `Server error (${res.status})`);
  return res.status === 204 ? null : res.json();
}

function toast(msg, bad) {
  if (AUTH.locked) return;
  const t = h('div', { class: 'toast' + (bad ? ' bad' : '') }, msg);
  $('#toasts').append(t);
  setTimeout(() => t.remove(), 4500);
}

const fmtRuntime = (s) => {
  if (!s) return '';
  const m = Math.round(s / 60);
  if (m < 1) return '<1m';
  return m >= 60 ? `${Math.floor(m / 60)}h ${m % 60}m` : `${m}m`;
};
const fmtLeft = (pos, dur) => (dur && dur > pos ? `${fmtRuntime(dur - pos)} left` : '');
const resLabel = (w, hgt) => (!w ? null : w >= 3500 || hgt >= 2000 ? '4K' : w >= 1800 || hgt >= 1000 ? '1080p' : w >= 1200 || hgt >= 700 ? '720p' : 'SD');
const codecLabel = (c) => ({ hevc: 'HEVC', h264: 'H.264', vp9: 'VP9', av1: 'AV1', vc1: 'VC-1', mpeg4: 'MPEG-4' }[c] || (c ? c.toUpperCase() : null));

function store(key, value) { try { if (value === undefined) return localStorage.getItem(key); localStorage.setItem(key, value); } catch { /* storage unavailable */ } return null; }

// ---- cleanup registry: everything a page starts (timers, observers, listeners) is stopped when the route changes
let leaveFns = [];
const onLeave = (fn) => leaveFns.push(fn);
function runLeave() { const fns = leaveFns; leaveFns = []; for (const fn of fns.reverse()) { try { fn(); } catch { /* best effort */ } } }

// ================================================================ artwork: image over a generated gradient
function hashHue(s) { let n = 7; for (const ch of s) n = (n * 31 + ch.codePointAt(0)) % 360; return n; }

function art(src, title, { progress, badge, overlay, portrait } = {}) {
  const hue = hashHue(title || '?');
  const el = h('div', { class: 'art' + (portrait ? ' portrait' : ''), style: `background:linear-gradient(135deg,hsl(${hue} 42% 30%),hsl(${(hue + 45) % 360} 48% 16%))` },
    h('span', { class: 'initial' }, (title || '?').trim().charAt(0).toUpperCase()));
  if (src) {
    const img = h('img', { alt: '', loading: 'lazy', decoding: 'async', src });
    img.addEventListener('load', () => img.classList.add('loaded'));
    img.addEventListener('error', () => img.remove()); // the gradient stays
    el.append(img);
  }
  if (badge) el.append(h('span', { class: 'badge' }, badge));
  if (overlay) el.append(h('span', { class: 'play-overlay' }, h('span', {}, icon('play'))));
  if (progress > 0) el.append(h('span', { class: 'progress' }, h('i', { style: `width:${Math.min(100, Math.round(progress * 100))}%` })));
  return el;
}

function tile({ href, title, hover, sub, img, progress, badge, portrait }) {
  return h('a', { class: 'tile', href, title: hover || title }, art(img, title, { progress, badge, overlay: true, portrait }),
    h('div', { class: 't' }, title), sub ? h('div', { class: 's' }, sub) : null);
}

const itemHref = (i) => (i.type === 'series' ? `#/series/${encodeURIComponent(i.key)}` : `#/movie/${i.mediaId}`);
const itemSub = (i) => [i.year, i.type === 'series' ? `${i.episodeCount} episode${i.episodeCount === 1 ? '' : 's'}` : null].filter(Boolean).join(' · ');
// Real posters (portrait) when the library has them; otherwise landscape frames. A title without a poster in poster mode
// shows its frame cropped to the same shape so a grid stays tidy.
const POSTER_MIN = 0.5;
const posterShare = (items) => (items.length ? items.filter((i) => i.posterUrl).length / items.length : 0);
const itemTile = (i, poster = false) => tile({ href: itemHref(i), title: i.title, hover: [i.title, i.rating ? `★ ${i.rating.toFixed(1)}` : null, i.genres].filter(Boolean).join(' · '), sub: itemSub(i), img: poster ? i.posterUrl || i.imageUrl : i.imageUrl, badge: i.isNew ? 'NEW' : null, portrait: poster });
const continueTile = (c) => tile({
  href: `#/watch/${c.mediaId}`, title: c.title, img: c.imageUrl,
  sub: c.nextUp ? `Up next · ${c.subtitle}` : [c.subtitle, fmtLeft(c.positionSeconds, c.durationSeconds)].filter(Boolean).join(' · '),
  progress: c.durationSeconds ? c.positionSeconds / c.durationSeconds : 0,
});

function row(title, tiles, seeAll, poster) {
  const sc = h('div', { class: 'scroller' + (poster ? ' posters' : '') }, tiles);
  const go = (dir) => sc.scrollBy({ left: dir * sc.clientWidth * 0.85, behavior: 'smooth' });
  return h('section', { class: 'rowsec' },
    h('div', { class: 'rowhead' }, h('h2', {}, title), seeAll ? h('a', { href: seeAll }, 'See all ›') : null),
    h('button', { class: 'arrow l', type: 'button', 'aria-label': 'Scroll left', onclick: () => go(-1) }, icon('left')), sc,
    h('button', { class: 'arrow r', type: 'button', 'aria-label': 'Scroll right', onclick: () => go(1) }, icon('right')));
}

const skelRow = () => h('section', { class: 'rowsec' }, h('div', { class: 'rowhead' }, h('div', { class: 'skel', style: 'width:160px;height:22px' })),
  h('div', { class: 'scroller' }, Array.from({ length: 6 }, () => h('div', { class: 'tile' }, h('div', { class: 'skel card' })))));

const setView = (...nodes) => { view.replaceChildren(...nodes); window.scrollTo(0, 0); };
const showError = (e) => AUTH.locked ? null : setView(h('div', { class: 'error' }, e.message || String(e)));

// ================================================================ what this browser can decode (sent to the server)
// The server picks the cheapest path: direct play > direct stream (video untouched) > transcode.
function detectCaps() {
  const v = document.createElement('video');
  const can = (type) => { try { return !!v.canPlayType(type); } catch { return false; } }; // '' = no, 'maybe'/'probably' = yes
  const video = [], audio = [], containers = [];
  if (can('video/mp4; codecs="avc1.640028"')) video.push('h264');
  if (can('video/mp4; codecs="hvc1.1.6.L123.B0"')) video.push('hevc');
  if (can('video/mp4; codecs="hvc1.2.4.L153.B0"')) video.push('hevc10');
  if (can('video/mp4; codecs="hev1.1.6.L123.B0"')) video.push('hev1'); // Safari rejects HEVC tagged hev1 in MP4
  if (can('video/webm; codecs="vp09.00.10.08"')) video.push('vp9');
  if (can('video/mp4; codecs="av01.0.08M.08"')) video.push('av1');
  if (can('audio/mp4; codecs="mp4a.40.2"')) audio.push('aac');
  if (can('audio/mpeg')) audio.push('mp3');
  if (can('audio/webm; codecs="opus"')) audio.push('opus');
  if (can('audio/webm; codecs="vorbis"')) audio.push('vorbis');
  if (can('audio/flac')) audio.push('flac');
  if (can('audio/mp4; codecs="ac-3"')) audio.push('ac3');
  if (can('audio/mp4; codecs="ec-3"')) audio.push('eac3');
  if (video.length) containers.push('mp4');
  if (can('video/webm')) containers.push('webm');
  if (can('video/x-matroska; codecs="avc1.640028"')) containers.push('mkv');
  return { v: video.join(','), a: audio.join(','), c: containers.join(',') };
}
const CAPS = detectCaps();
const capsQuery = () => new URLSearchParams(CAPS).toString();
const IS_SAFARI = /^((?!chrome|chromium|android|crios|fxios|edg).)*safari/i.test(navigator.userAgent);

// ================================================================ router
function parseHash() {
  const raw = location.hash.slice(1) || '/';
  const [path, qs] = raw.split('?');
  return { parts: path.split('/').filter(Boolean).map(decodeURIComponent), params: new URLSearchParams(qs || '') };
}

// Back/forward returns to where you were: positions are remembered per page, and only restored when the history entry was visited before
// (a fresh click on a link starts at the top). Lists load more pages as needed to reach the old position.
const scrollMemo = new Map();
let navigating = false, navCounter = 0;
window.addEventListener('scroll', () => { if (!navigating) scrollMemo.set(location.hash || '#/', window.scrollY); }, { passive: true });
const jump = (y) => window.scrollTo({ top: y, behavior: 'instant' });

async function route() {
  const key = location.hash || '#/';
  const revisit = !!(history.state && history.state.n != null);
  if (!revisit) { try { history.replaceState({ n: ++navCounter }, ''); } catch { /* ignore */ } }
  const restoreY = revisit ? scrollMemo.get(key) || 0 : 0;
  navigating = true;
  runLeave();
  teardownPlayer();
  document.body.classList.remove('watching');
  document.title = APP.name;
  const { parts, params } = parseHash();
  const [page, arg] = parts;
  const section = !page ? 'home' : page === 'movies' || page === 'movie' ? 'movies' : page === 'shows' || page === 'series' ? 'shows' : page === 'live' ? 'live' : '';
  document.querySelectorAll('#nav a').forEach((a) => a.classList.toggle('active', a.dataset.route === section));
  if (page !== 'search') $('#search').value = '';
  else if (document.activeElement !== $('#search')) $('#search').value = arg || '';
  try {
    let restore = null;
    if (!page) await showHome();
    else if (page === 'movies') restore = await showList({ type: 'movie', title: 'Movies' });
    else if (page === 'shows') restore = await showList({ type: 'series', title: 'TV Shows' });
    else if (page === 'search') restore = await showList({ type: null, title: `Results for “${arg || ''}”`, query: arg || '' });
    else if (page === 'unmatched') restore = await showList({ type: null, title: 'Needs a match', matched: false });
    else if (page === 'system') location.replace('#/settings/system');
    else if (page === 'settings') await showSettings(arg);
    else if (page === 'welcome') await showWelcome();
    else if (page === 'live') { if (arg) await showLivePlayer(parseInt(arg, 10)); else await showLive(); }
    else if (page === 'movie' && arg) await showMovie(arg);
    else if (page === 'series' && arg) await showSeries(arg);
    else if (page === 'watch' && arg) await showPlayer(arg, params);
    else await showHome();
    if (restoreY > 0 && page !== 'watch' && page !== 'live') { if (restore) await restore(restoreY); else jump(restoreY); }
  } catch (e) { showError(e); }
  finally { navigating = false; }
}

// ================================================================ library scanning (header button + auto-scan awareness)
const scanBtn = $('#scanbtn');
const sysBtn = h('a', { class: 'icon-btn', href: '#/settings', title: 'Settings', 'aria-label': 'Settings' }, icon('gear'));
scanBtn.before(sysBtn);
scanBtn.append(icon('refresh'));
let scanWatcher = null;

function watchScan(announce) {
  if (scanWatcher) return scanWatcher;
  scanBtn.classList.add('spinning');
  scanWatcher = (async () => {
    try {
      let s;
      do { await sleep(1500); s = await api('/api/library/status'); } while (s.scanning);
      if (announce) {
        if (s.lastError) toast(s.lastError, true);
        else if (s.lastResult) {
          const r = s.lastResult;
          toast(r.added + r.updated + r.moved + r.removed === 0 ? 'Library is up to date' : `Library updated · ${r.added} added, ${r.removed} removed`);
        }
      }
      const { parts } = parseHash();
      if (parts[0] !== 'watch') route(); // refresh whatever page is open
    } catch { /* server restarting; try again on next click */ }
    finally { scanBtn.classList.remove('spinning'); scanWatcher = null; }
  })();
  return scanWatcher;
}

scanBtn.addEventListener('click', async () => {
  try {
    const res = await fetch('/api/library/scan?background=true', { method: 'POST' });
    if (res.status === 409) toast('A scan is already running');
    else if (!res.ok) { toast('Could not start the scan', true); return; }
    else toast('Scanning your library…');
    watchScan(true);
  } catch { toast('Could not reach the server', true); }
});

const greeting = () => { const hr = new Date().getHours(); return hr < 5 ? 'Still up' : hr < 12 ? 'Good morning' : hr < 18 ? 'Good afternoon' : 'Good evening'; };

// ================================================================ surprise me
async function surprise(kind, genre) {
  try {
    const qs = new URLSearchParams({ kind });
    if (genre) qs.set('genre', genre);
    const res = await fetch(`/api/random?${qs}`);
    if (res.status === 404) { toast(kind === 'episode' ? 'No episodes to pick from' : 'No movies to pick from', true); return; }
    if (!res.ok) throw new Error();
    const pick = await res.json();
    toast(`Tonight: ${pick.title}`);
    location.hash = `#/watch/${pick.mediaId}`;
  } catch { toast('Could not pick something', true); }
}

// ================================================================ home
async function showHome() {
  setView(h('div', { class: 'skel hero' }), h('div', { class: 'page' }, skelRow(), skelRow()));
  const [movies, shows, cont, status, live] = await Promise.all([
    api('/api/items?type=movie&sort=recent&limit=24'),
    api('/api/items?type=series&sort=recent&limit=24'),
    api('/api/continue?limit=12').catch(() => []),
    api('/api/library/status').catch(() => ({})),
    loadLiveChannelsForHome(),
  ]);

  if (movies.total + shows.total === 0) {
    if (status.scanning) {
      watchScan(false);
      setView(h('div', { class: 'empty' }, h('h2', {}, 'Scanning your library…'), h('p', {}, 'The first scan can take a few minutes. This page will fill in when it finishes.')));
    } else {
      setView(h('div', { class: 'empty' }, h('h2', {}, 'Your library is empty'),
        h('p', {}, status.lastError || 'Nothing has been indexed yet. Check that your media folder is mounted into the server, then scan.'),
        h('div', { class: 'notice-actions', style: 'justify-content:center' }, h('button', { class: 'btn', type: 'button', onclick: () => scanBtn.click() }, 'Scan library'),
          h('a', { class: 'btn glass', href: '#/settings' }, 'Settings'))));
    }
    return;
  }

  // Hero: the newest few titles, alternating movies and shows.
  const slides = [];
  for (let i = 0; slides.length < 5 && (i < movies.items.length || i < shows.items.length); i++) {
    if (movies.items[i]) slides.push(movies.items[i]);
    if (slides.length < 5 && shows.items[i]) slides.push(shows.items[i]);
  }
  const page = h('div', { class: 'page' });
  page.append(libraryNotice(status));
  if (pref('homeGreeting') === 'on' && AUTH.enabled && AUTH.user) page.append(h('h1', { class: 'greeting' }, `${greeting()}, `, h('span', {}, greetingName())));
  if (pref('homeQuick') === 'on') page.append(h('div', { class: 'quick' },
    h('button', { class: 'chip', type: 'button', onclick: () => surprise('movie') }, icon('dice'), 'Random movie'),
    h('button', { class: 'chip', type: 'button', onclick: () => surprise('episode') }, icon('dice'), 'Random episode'),
    live.length ? h('a', { class: 'chip', href: '#/live' }, 'Live TV') : null));
  if (cont.length && pref('homeContinue') === 'on') page.append(row('Continue Watching', cont.map(continueTile)));
  if (live.length && pref('homeLive') === 'on') page.append(row('Live TV', live.map(liveTile), '#/live'));
  const mPoster = posterShare(movies.items) >= POSTER_MIN, sPoster = posterShare(shows.items) >= POSTER_MIN;
  if (movies.items.length && pref('homeMovies') === 'on') page.append(row('Recently Added Movies', movies.items.map((i) => itemTile(i, mPoster)), '#/movies', mPoster));
  if (shows.items.length && pref('homeShows') === 'on') page.append(row('Recently Added Shows', shows.items.map((i) => itemTile(i, sPoster)), '#/shows', sPoster));
  setView(...(pref('homeHero') === 'on' ? [heroCarousel(slides)] : []), page);
}

// Favorite channels first (falling back to the first channels), each with what's on right now.
async function loadLiveChannelsForHome() {
  const st = await loadLiveStatus();
  if (!st.configured || !st.channels) return [];
  const fav = await api('/api/live/channels?favorites=true&limit=12').catch(() => ({ items: [] }));
  if (fav.items.length) return fav.items;
  return (await api('/api/live/channels?limit=12').catch(() => ({ items: [] }))).items;
}

// A nudge toward TMDB (posters, descriptions, ratings), or toward fixing titles that didn't match.
function libraryNotice(status) {
  if (!status.tmdbConfigured) {
    if (store('dismiss-tmdb')) return null;
    const box = h('div', { class: 'notice' }, h('div', {}, h('strong', {}, 'Want real posters? '),
      'Add a free TMDB API token and this library gets posters, descriptions and ratings automatically. Until then, artwork comes from frames of the videos themselves.'),
      h('div', { class: 'notice-actions' }, h('a', { class: 'btn small', href: '#/settings/metadata' }, 'Add token'),
        h('button', { class: 'btn glass small', type: 'button', onclick: () => { store('dismiss-tmdb', '1'); box.remove(); } }, 'Dismiss')));
    return box;
  }
  if (status.unmatched > 0)
    return h('div', { class: 'notice' }, h('div', {}, h('strong', {}, `${status.unmatched} title${status.unmatched === 1 ? '' : 's'} `), "couldn't be matched to TMDB, so they have no poster yet."),
      h('a', { class: 'btn glass small', href: '#/unmatched' }, 'Review'));
  return null;
}

function heroCarousel(items) {
  const slides = items.map((it, i) => {
    // Only the first slide loads up front; the others load when first shown (each is a large image the server may have to extract).
    const img = h('img', { alt: '', decoding: 'async', 'data-src': it.heroUrl });
    if (i === 0) img.src = it.heroUrl;
    img.addEventListener('load', () => img.classList.add('loaded'));
    img.addEventListener('error', () => img.remove());
    const meta = [it.year, it.type === 'series' ? `${it.episodeCount} episodes` : 'Movie'].filter(Boolean);
    return h('div', { class: 'hero-slide' + (i === 0 ? ' on' : ''), 'aria-hidden': i === 0 ? 'false' : 'true' },
      h('div', { class: 'hero-bg', style: `background:linear-gradient(135deg,hsl(${hashHue(it.title)} 42% 28%),hsl(${(hashHue(it.title) + 45) % 360} 48% 14%))` }, img),
      h('div', { class: 'hero-body' },
        h('div', { class: 'hero-kicker' }, it.isNew ? 'Recently added' : 'Featured'),
        h('h1', {}, it.title),
        h('div', { class: 'chips' }, h('span', {}, meta.join(' · '))),
        it.overview ? h('p', {}, it.overview) : null,
        h('div', { class: 'actions' },
          it.type === 'movie'
            ? [h('a', { class: 'btn', href: `#/watch/${it.mediaId}` }, icon('play'), 'Play'), h('a', { class: 'btn glass', href: itemHref(it) }, 'Details')]
            : h('a', { class: 'btn', href: itemHref(it) }, icon('play'), 'Browse episodes'))));
  });
  const dots = h('div', { class: 'dots' }, items.map((_, i) => h('button', { type: 'button', 'aria-label': `Show slide ${i + 1}`, class: i === 0 ? 'on' : '', onclick: () => show(i, true) })));
  const el = h('div', { class: 'hero' }, slides, items.length > 1 ? dots : null);
  let cur = 0, timer = null;
  function show(i, user) {
    cur = i;
    const next = slides[i].querySelector('img');
    if (next && !next.getAttribute('src')) next.src = next.dataset.src;
    slides.forEach((s, j) => { s.classList.toggle('on', j === i); s.setAttribute('aria-hidden', j === i ? 'false' : 'true'); });
    [...dots.children].forEach((d, j) => d.classList.toggle('on', j === i));
    if (user) restart();
  }
  function restart() { clearInterval(timer); if (items.length > 1 && pref('heroRotate') === 'on' && pref('motion') !== 'reduce' && !matchMedia('(prefers-reduced-motion: reduce)').matches) timer = setInterval(() => show((cur + 1) % items.length), 9000); }
  restart();
  el.addEventListener('mouseenter', () => clearInterval(timer));
  el.addEventListener('mouseleave', restart);
  onLeave(() => clearInterval(timer));
  return el;
}

// ================================================================ system status
const fmtBytes = (n) => (n >= 1e9 ? `${(n / 1e9).toFixed(1)} GB` : n >= 1e6 ? `${Math.round(n / 1e6)} MB` : `${Math.round(n / 1e3)} KB`);
const fmtUp = (s) => (s >= 86400 ? `${Math.floor(s / 86400)}d ${Math.floor(s % 86400 / 3600)}h` : s >= 3600 ? `${Math.floor(s / 3600)}h ${Math.floor(s % 3600 / 60)}m` : `${Math.max(1, Math.floor(s / 60))}m`);

async function mountSystem(body) {
  body.classList.add('sys');
  async function load() {
    const d = await api('/api/system');
    if (!body.isConnected) return;
    const kv = (rows) => h('dl', { class: 'kv' }, rows.map(([k, v]) => [h('dt', {}, k), h('dd', {}, v)]));
    const l = d.library;
    const streams = d.streams.length
      ? d.streams.map((st) => h('div', { class: 'stream' },
        h('div', {}, h('b', {}, st.title || (st.kind === 'live' ? 'Live channel' : 'Stream')), h('div', { class: 'dur' }, `${st.kind === 'live' ? 'Live · ' : ''}${st.mode}${st.reason ? ` — ${st.reason}` : ''}`)),
        h('button', { class: 'btn glass', type: 'button', onclick: async () => { try { await fetch(`/api/hls/${st.id}/stop`, { method: 'POST' }); toast('Stream stopped'); load(); } catch (e) { toast(e.message, true); } } }, 'Stop')))
      : [h('p', { class: 'dur' }, 'Nothing is streaming right now.')];
    body.replaceChildren(
      h('div', { class: 'panel' }, h('h3', {}, 'Library'), kv([['Movies', l.movies], ['Shows', `${l.shows} (${l.episodes} episodes)`], ['Files', l.files],
        ['Needs a match', l.unmatched ? h('a', { href: '#/unmatched', style: 'text-decoration:underline' }, l.unmatched) : 0],
        ['Scan', l.scanning ? 'Scanning…' : l.scanError ? `Failed: ${l.scanError}` : l.lastScan ? new Date(l.lastScan).toLocaleString() : 'Not yet'],
        ['TMDB', d.tmdb.configured ? 'Connected' : 'Not configured'],
        ['Live TV', d.live.configured ? `${d.live.channels} channels, ${d.live.programmes} programmes${d.live.error ? ` (${d.live.error})` : ''}` : 'Not configured']])),
      h('div', { class: 'panel' }, h('h3', {}, `Streams (${d.streams.length}/${d.encoder.maxStreams})`), ...streams),
      h('div', { class: 'panel' }, h('h3', {}, 'Server'), kv([['Uptime', fmtUp(d.uptimeSeconds)], ['Encoder', d.encoder.name],
        ['Artwork cache', fmtBytes(d.storageBytes.artwork)], ['Transcode temp', fmtBytes(d.storageBytes.transcode)]])));
  }
  await load();
  const t = setInterval(() => load().catch(() => {}), 5000);
  onLeave(() => clearInterval(t));
}

// ================================================================ lists (movies / shows / search) with infinite scroll
const PAGE = 48;

async function showList({ type, title, query, matched }) {
  const sortKey = 'sort:' + (type || (matched === false ? 'unmatched' : 'search'));
  let sort = query ? 'title' : store(sortKey) || 'title';
  const grid = h('div', { class: 'grid' });
  const countEl = h('span', { class: 'count' });
  const sentinel = h('div', { class: 'sentinel' });
  const select = query ? null : h('select', { class: 'sort', 'aria-label': 'Sort', onchange: (e) => { sort = e.target.value; store(sortKey, sort); reset(); } },
    [['title', 'A – Z'], ['recent', 'Recently added'], ['year', 'Year']].map(([v, l]) => h('option', { value: v, selected: v === sort }, l)));
  let genre = query || matched === false ? '' : store('genre:' + (type || 'all')) || '';
  const genreSel = h('select', { class: 'sort genre hidden', 'aria-label': 'Genre', onchange: (e) => { genre = e.target.value; store('genre:' + (type || 'all'), genre); reset(); } });
  if (!query && matched !== false) api(`/api/genres${type ? `?type=${type}` : ''}`).then((g) => {
    const list = Array.isArray(g) ? g : g.genres || [];
    if (!list.length || !genreSel.isConnected) return;
    genreSel.replaceChildren(h('option', { value: '' }, 'All genres'), ...list.map((x) => h('option', { value: x.name, selected: x.name === genre }, `${x.name} (${x.count})`)));
    genreSel.classList.remove('hidden');
  }).catch(() => {});
  const randomBtn = type ? h('button', { class: 'btn glass small', type: 'button', onclick: () => surprise(type === 'series' ? 'episode' : 'movie', genre) }, icon('dice'), type === 'series' ? 'Random episode' : 'Random movie') : null;
  const viewBox = h('div', { class: 'seg hidden', role: 'group', 'aria-label': 'View' });
  const intro = matched === false ? h('p', { class: 'dur' }, 'Open a title and choose “Fix match” to pick the right entry from TMDB.') : null;
  setView(h('div', { class: 'page' }, h('div', { class: 'page-head' }, h('h1', {}, title), countEl, randomBtn, viewBox, genreSel, select), intro, grid, sentinel));

  let offset = 0, total = Infinity, loading = false, gen = 0;
  const loaded = [];
  let view = store('view') || 'auto'; // auto | posters | thumbs
  const usePosters = () => (view === 'posters') || (view === 'auto' && posterShare(loaded) >= POSTER_MIN);

  function paint(items, replace) {
    const poster = usePosters();
    grid.classList.toggle('posters', poster);
    if (replace) grid.replaceChildren();
    grid.append(...items.map((i) => itemTile(i, poster)));
  }
  function renderView() {
    const any = loaded.some((i) => i.posterUrl);
    viewBox.classList.toggle('hidden', !any);
    viewBox.replaceChildren(...[['posters', 'Posters'], ['thumbs', 'Thumbnails']].map(([v, l]) => h('button', {
      type: 'button', class: (usePosters() ? 'posters' : 'thumbs') === v ? 'on' : '', 'aria-pressed': String((usePosters() ? 'posters' : 'thumbs') === v),
      onclick: () => { view = v; store('view', v); paint(loaded, true); renderView(); },
    }, l)));
  }

  async function more() {
    if (loading || offset >= total) return;
    loading = true;
    const my = gen;
    try {
      const qs = new URLSearchParams({ limit: PAGE, offset, sort });
      if (type) qs.set('type', type);
      if (query) qs.set('search', query);
      if (matched === false) qs.set('matched', 'false');
      if (genre) qs.set('genre', genre);
      const d = await api(`/api/items?${qs}`);
      if (my !== gen) return; // a newer request (sort change / navigation) superseded this one
      total = d.total;
      const first = loaded.length === 0;
      loaded.push(...d.items);
      paint(first ? loaded : d.items, first);
      renderView();
      offset += d.items.length;
      countEl.textContent = total ? `${total} ${total === 1 ? 'title' : 'titles'}` : '';
      if (total === 0) grid.replaceWith(h('div', { class: 'empty' }, h('h2', {}, query ? 'No results' : matched === false ? 'Everything is matched' : 'Nothing here yet'),
        h('p', {}, query ? `Nothing matched “${query}”.` : matched === false ? 'Every title has a TMDB match.' : 'Try scanning the library with the ↻ button.')));
    } catch (e) { toast(e.message, true); total = offset; }
    finally { loading = false; }
  }
  function reset() { gen++; offset = 0; total = Infinity; loading = false; loaded.length = 0; grid.replaceChildren(); more(); }

  const io = new IntersectionObserver((entries) => { if (entries.some((e) => e.isIntersecting)) more(); }, { rootMargin: '700px' });
  io.observe(sentinel);
  onLeave(() => io.disconnect());
  await more();
  return async (y) => {
    for (let i = 0; i < 40 && document.documentElement.scrollHeight < y + innerHeight + 300 && offset < total; i++) await more();
    jump(y);
  };
}

// ================================================================ fix a wrong TMDB match
function openMatchDialog({ type, query, mediaId, scope, current, onDone }) {
  const input = h('input', { type: 'search', class: 'dlg-input', value: query, placeholder: type === 'tv' ? 'Search TV shows on TMDB' : 'Search movies on TMDB', 'aria-label': 'Search TMDB' });
  const list = h('div', { class: 'dlg-list' });
  const close = () => { document.removeEventListener('keydown', onKey); dlg.remove(); };
  const onKey = (e) => { if (e.key === 'Escape') close(); };

  async function choose(hit) {
    try {
      await api(`/api/media/${mediaId}/match`, { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ tmdbType: type, tmdbId: hit.id, scope }) });
      toast(`Matched to ${hit.title}${hit.year ? ` (${hit.year})` : ''}`);
      close(); onDone();
    } catch (e) { toast(e.message, true); }
  }
  async function reset() {
    try { await fetch(`/api/media/${mediaId}/match?scope=${scope}`, { method: 'DELETE' }); toast('Back to automatic matching'); close(); onDone(); }
    catch (e) { toast(e.message, true); }
  }
  async function search() {
    const q = input.value.trim();
    if (!q) return;
    list.replaceChildren(h('div', { class: 'dur' }, 'Searching…'));
    try {
      const hits = await api(`/api/tmdb/search?type=${type}&q=${encodeURIComponent(q)}`);
      list.replaceChildren(...(hits.length ? hits.map((hit) => h('button', { class: 'hit', type: 'button', onclick: () => choose(hit) },
        hit.posterPath ? h('img', { alt: '', loading: 'lazy', src: `/api/tmdb/image?path=${encodeURIComponent(hit.posterPath)}&size=w92` }) : h('span', { class: 'noposter' }),
        h('span', { class: 'hit-text' }, h('strong', {}, hit.title, hit.year ? ` (${hit.year})` : ''), h('small', {}, hit.overview || 'No description'))))
        : [h('div', { class: 'dur' }, 'No results. Try a different spelling.')]));
    } catch (e) { list.replaceChildren(h('div', { class: 'error' }, e.message)); }
  }
  let timer;
  input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(search, 350); });
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { clearTimeout(timer); search(); } });

  const dlg = h('div', { class: 'dlg-backdrop', onclick: (e) => { if (e.target === dlg) close(); } },
    h('div', { class: 'dlg', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Fix match' },
      h('h2', {}, 'Fix match'),
      h('p', { class: 'dur' }, current ? `Currently matched to ${current}. ` : 'Not matched yet. ', 'Search and pick the right one.', scope === 'title' ? ' This applies to every episode.' : ''),
      input, list,
      h('div', { class: 'dlg-actions' }, h('button', { class: 'btn glass small', type: 'button', onclick: reset }, 'Use automatic matching'), h('button', { class: 'btn glass small', type: 'button', onclick: close }, 'Close'))));
  document.body.append(dlg);
  document.addEventListener('keydown', onKey);
  input.focus(); input.select();
  search();
}

// "Matched to X (2016) · Fix match" line, shown only when TMDB is configured.
async function matchLine({ type, query, mediaId, scope }) {
  const status = await api('/api/library/status').catch(() => ({}));
  if (!status.tmdbConfigured) return null;
  const m = await api(`/api/media/${mediaId}/match`).catch(() => null);
  const holder = h('div', { class: 'matchline' });
  const draw = () => {
    const name = m && m.title ? `${m.title}${m.year ? ` (${m.year})` : ''}` : null;
    holder.replaceChildren(h('span', { class: 'dur' }, name ? `TMDB: ${name}${m.state === 'manual' ? ' · chosen by you' : ''}` : 'No TMDB match'),
      h('button', { class: 'linkbtn', type: 'button', onclick: () => openMatchDialog({ type, query, mediaId, scope, current: name, onDone: () => route() }) }, 'Fix match'));
  };
  draw();
  return holder;
}

// ================================================================ detail pages
function heroBackdrop(url, title) {
  const hue = hashHue(title);
  const img = h('img', { alt: '', src: url, decoding: 'async' });
  img.addEventListener('load', () => img.classList.add('loaded'));
  img.addEventListener('error', () => img.remove());
  return h('div', { class: 'hero-bg', style: `background:linear-gradient(135deg,hsl(${hue} 42% 28%),hsl(${(hue + 45) % 360} 48% 14%))` }, img);
}

function posterBox(url, title) {
  if (!url) return null;
  const img = h('img', { alt: `${title} poster`, src: url });
  img.addEventListener('error', () => img.parentElement && img.parentElement.remove());
  return h('div', { class: 'detail-poster' }, img);
}

function techChips(f) {
  const res = resLabel(f.width, f.height);
  return [res && h('span', { class: 'chip accent' }, res), f.videoCodec && h('span', { class: 'chip' }, codecLabel(f.videoCodec)),
    f.audio[0] && h('span', { class: 'chip' }, [f.audio[0].codec && f.audio[0].codec.toUpperCase(), f.audio[0].channels > 2 ? `${f.audio[0].channels - 1}.1` : null].filter(Boolean).join(' '))];
}

function modeLine(f) {
  return h('span', { class: `mode ${f.mode}` }, h('i'), f.reason);
}

async function showMovie(id) {
  const m = await api(`/api/items/movie/${encodeURIComponent(id)}?${capsQuery()}`);
  document.title = `${m.title} · ${APP.name}`;
  const f = m.file;
  const resume = f.resumeSeconds > 0;
  const meta = [m.year, fmtRuntime(f.durationSeconds), m.rating ? `★ ${m.rating.toFixed(1)}` : null, m.genres].filter(Boolean);
  const tracks = [
    ['File', f.fileName],
    ['Video', [resLabel(f.width, f.height), f.width ? `${f.width}×${f.height}` : null, codecLabel(f.videoCodec)].filter(Boolean).join(' · ') || '—'],
    ['Audio', f.audio.length ? f.audio.map((a) => [a.language, a.codec && a.codec.toUpperCase(), a.channels ? `${a.channels}ch` : null].filter(Boolean).join(' ')).join(', ') : 'None'],
    ['Subtitles', f.subtitles.length ? f.subtitles.map((s) => s.language || s.codec).join(', ') : 'None'],
  ];
  const info = h('div', { class: 'section' }, h('div', { class: 'panel' }, h('h3', {}, 'Playback'),
    h('dl', { class: 'kv' }, h('dt', {}, 'This browser'), h('dd', {}, modeLine(f)), tracks.map(([k, v]) => [h('dt', {}, k), h('dd', {}, v)])),
    m.otherVersions.length ? h('p', { class: 'dur' }, 'Other versions: ', m.otherVersions.map((v) => h('a', { href: `#/watch/${v.mediaId}`, style: 'margin-right:14px;text-decoration:underline' }, v.width ? `${v.width}×${v.height}` : v.fileName))) : null));
  const matchSlot = h('div');
  matchLine({ type: 'movie', query: m.title, mediaId: f.mediaId, scope: 'file' }).then((el) => el && matchSlot.replaceWith(el));
  setView(
    h('div', { class: 'detail' }, heroBackdrop(m.backdropUrl, m.title),
      h('div', { class: 'detail-body' }, posterBox(m.posterUrl, m.title),
        h('div', { class: 'detail-info' },
          h('h1', {}, m.title),
          h('div', { class: 'chips' }, h('span', {}, meta.join(' · ')), techChips(f)),
          m.overview ? h('p', { class: 'overview' }, m.overview) : null,
          matchSlot,
          h('div', { class: 'detail-actions' },
            h('a', { class: 'btn', href: `#/watch/${f.mediaId}` }, icon('play'), resume ? `Resume · ${fmtLeft(f.resumeSeconds, f.durationSeconds) || fmtRuntime(f.resumeSeconds)}` : 'Play'),
            resume ? h('a', { class: 'btn glass', href: `#/watch/${f.mediaId}?t=0` }, 'Start over') : null)))),
    info);
}

async function showSeries(key) {
  const s = await api(`/api/items/series/${encodeURIComponent(key)}`);
  document.title = `${s.title} · ${APP.name}`;
  const total = s.seasons.reduce((n, x) => n + x.episodes.length, 0);
  const meta = [s.year, `${total} episode${total === 1 ? '' : 's'}`, s.rating ? `★ ${s.rating.toFixed(1)}` : null, s.genres].filter(Boolean);
  const all = s.seasons.flatMap((x) => x.episodes);
  const target = all.find((e) => e.mediaId === s.resumeMediaId) || all[0];
  const started = target && (target.positionSeconds > 30 || all.some((e) => e.watched));

  // open on the season being watched
  const matchSlot = h('div');
  matchLine({ type: 'tv', query: s.title, mediaId: s.matchMediaId, scope: 'title' }).then((el) => el && matchSlot.replaceWith(el));
  let current = Math.max(0, s.seasons.findIndex((x) => x.episodes.some((e) => e.mediaId === s.resumeMediaId)));
  const list = h('div', { class: 'eps' });
  const pills = h('div', { class: 'seasons' });

  const seasonInfo = new Map(); // season number -> Map(episode number -> TMDB info)
  function renderSeason() {
    [...pills.children].forEach((p, i) => p.classList.toggle('on', i === current));
    const sn = s.seasons[current].season;
    const info = seasonInfo.get(sn);
    list.replaceChildren(...s.seasons[current].episodes.map((e) => episodeRow(e, info && info.get(e.episode))));
    if (info || sn == null || !/^tmdb:\d+$/.test(key)) return;
    const mine = current;
    api(`/api/items/series/${encodeURIComponent(key)}/season/${sn}`).then((d) => {
      seasonInfo.set(sn, new Map(d.episodes.map((x) => [x.number, x])));
      if (current === mine && list.isConnected) renderSeason();
    }).catch(() => seasonInfo.set(sn, new Map())); // TMDB being down must never break the page
  }
  s.seasons.forEach((x, i) => pills.append(h('button', { class: 'pill', type: 'button', onclick: () => { current = i; renderSeason(); } },
    x.season === 0 ? 'Specials' : x.season == null ? 'Episodes' : `Season ${x.season}`)));
  if (s.seasons.length <= 1) pills.classList.add('hidden');

  setView(
    h('div', { class: 'detail' }, heroBackdrop(s.backdropUrl, s.title),
      h('div', { class: 'detail-body' }, posterBox(s.posterUrl, s.title),
        h('div', { class: 'detail-info' },
          h('h1', {}, s.title),
          h('div', { class: 'chips' }, h('span', {}, meta.join(' · '))),
          s.overview ? h('p', { class: 'overview' }, s.overview) : null,
          matchSlot,
          target ? h('div', { class: 'detail-actions' }, h('a', { class: 'btn', href: `#/watch/${target.mediaId}` }, icon('play'),
            started ? `Continue · ${target.season != null ? `S${target.season}:` : ''}E${target.episode ?? '?'}` : 'Play'),
            h('button', { class: 'btn glass', type: 'button', onclick: () => {
              const fresh = all.filter((e) => !e.watched);
              const pool = fresh.length ? fresh : all;
              location.hash = `#/watch/${pool[Math.floor(Math.random() * pool.length)].mediaId}`;
            } }, icon('dice'), 'Random episode')) : null))),
    h('div', { class: 'section' }, pills, list));
  renderSeason();
}

function episodeRow(e, info) {
  const prog = e.durationSeconds && e.positionSeconds > 0 && !e.watched ? e.positionSeconds / e.durationSeconds : 0;
  const num = e.episode != null ? `${e.season != null ? `S${e.season} · ` : ''}E${e.episode}${e.episodeEnd ? `–E${e.episodeEnd}` : ''}` : '';
  const check = h('button', { class: 'check' + (e.watched ? ' on' : ''), type: 'button', title: e.watched ? 'Mark as unwatched' : 'Mark as watched', 'aria-label': 'Toggle watched' }, icon('check'));
  const el = h('a', { class: 'ep' + (e.watched ? ' watched' : ''), href: `#/watch/${e.mediaId}` },
    art((info && info.stillUrl) || e.thumbUrl, e.title, { progress: prog, overlay: true }),
    h('div', {}, h('div', { class: 'num' }, num), h('div', { class: 'name' }, (info && info.name) || e.title),
      h('div', { class: 'dur' }, [fmtRuntime(e.durationSeconds), info && info.airDate].filter(Boolean).join(' · ')),
      info && info.overview ? h('p', { class: 'ep-over' }, info.overview) : null), check);
  check.addEventListener('click', async (ev) => {
    ev.preventDefault(); ev.stopPropagation();
    try {
      if (e.watched) await fetch(`/api/media/${e.mediaId}/progress`, { method: 'DELETE' });
      else await api(`/api/media/${e.mediaId}/progress`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ position: e.durationSeconds || 1, duration: e.durationSeconds || 1, watched: true }) });
      e.watched = !e.watched; e.positionSeconds = 0;
      el.replaceWith(episodeRow(e, info));
    } catch (err) { toast(err.message, true); }
  });
  return el;
}

// ================================================================ player
let activePlayback = null;
let playerToken = 0; // bumped on every navigation so a slow start can tell it was abandoned
function teardownPlayer() {
  playerToken++;
  if (!activePlayback) return;
  const ap = activePlayback;
  activePlayback = null;
  ap.dispose();
}

// A controller owns whatever must be cleaned up for the current stream (HLS session, timers, listeners).
function newController() {
  const cleanups = [];
  return { onStop: (fn) => cleanups.push(fn), stop() { while (cleanups.length) { try { cleanups.pop()(); } catch { /* best effort */ } } } };
}

// Our own transport controls. The browser's native ones can't be trusted here: Safari labels a still-growing (copied) stream
// "Live Broadcast" and shows a duration that isn't the movie's, so the bar is driven by the duration the server already knows.
function buildControls(video, stage, f, wake) {
  const fmt = (t) => {
    t = Math.max(0, Math.floor(t || 0));
    const hh = Math.floor(t / 3600), mm = Math.floor(t % 3600 / 60), ss = t % 60;
    return (hh ? `${hh}:${String(mm).padStart(2, '0')}` : `${mm}`) + `:${String(ss).padStart(2, '0')}`;
  };
  const total = () => f.durationSeconds || (Number.isFinite(video.duration) ? video.duration : 0);
  const btn = (name, label, onclick) => h('button', { class: 'cbtn', type: 'button', title: label, 'aria-label': label, onclick }, icon(name));
  const playBtn = btn('play', 'Play / pause', () => (video.paused ? video.play() : video.pause()));
  const swap = (b, name) => b.replaceChildren(icon(name));
  const skip = (dt) => { seekTo(video.currentTime + dt); wake(); };
  const muteBtn = btn('volume', 'Mute', () => { video.muted = !video.muted; });
  // playback speed (the choice is kept for next time) and remembered volume
  const SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 2];
  const speedBtn = h('button', { class: 'cbtn speed', type: 'button', title: 'Playback speed ( < > )', 'aria-label': 'Playback speed', 'aria-haspopup': 'true' }, '1×');
  const speedMenu = h('div', { class: 'qmenu cc-menu speed-menu hidden', role: 'menu' });
  const setSpeed = (r, remember = true) => {
    r = SPEEDS.includes(r) ? r : 1;
    video.defaultPlaybackRate = r; video.playbackRate = r;
    speedBtn.textContent = `${r}×`;
    if (remember) setPref('speed', String(r));
  };
  const stepSpeed = (d) => { const i = SPEEDS.indexOf(video.playbackRate); setSpeed(SPEEDS[Math.min(SPEEDS.length - 1, Math.max(0, (i < 0 ? 2 : i) + d))]); toast(`Speed ${video.playbackRate}×`); };
  speedBtn.addEventListener('click', () => {
    speedMenu.replaceChildren(...SPEEDS.map((r) => h('button', { type: 'button', role: 'menuitemradio', class: video.playbackRate === r ? 'on' : '', 'aria-checked': String(video.playbackRate === r),
      onclick: () => { setSpeed(r); speedMenu.classList.add('hidden'); } }, h('span', {}, r === 1 ? 'Normal' : `${r}×`), h('small', {}, video.playbackRate === r ? '✓' : ''))));
    speedMenu.classList.toggle('hidden'); wake();
  });
  stage.addEventListener('click', (e) => { if (!speedMenu.contains(e.target) && !speedBtn.contains(e.target)) speedMenu.classList.add('hidden'); });
  setSpeed(parseFloat(pref('speed')), false);
  if (pref('rememberVolume') === 'on') { video.volume = Math.min(1, Math.max(0, parseFloat(pref('volume')) || 1)); video.muted = pref('muted') === 'on'; }
  const fsBtn = btn('expand', 'Full screen', () => toggleFullscreen());
  const cur = h('span', { class: 'time' }, '0:00'), dur = h('span', { class: 'time' }, fmt(total()));
  const buf = h('i', { class: 'buf' }), fill = h('i', { class: 'fill' }), knob = h('b', { class: 'knob' });
  const hint = h('span', { class: 'scrub-hint hidden' });
  const bar = h('div', { class: 'seekbar', role: 'slider', tabindex: '0', 'aria-label': 'Seek', 'aria-valuemin': '0' }, buf, fill, knob, hint);
  const vol = h('input', { class: 'vol', type: 'range', min: '0', max: '1', step: '0.05', value: String(video.volume), 'aria-label': 'Volume' });
  vol.addEventListener('input', () => { video.volume = parseFloat(vol.value); video.muted = false; });

  function seekTo(t) {
    const d = total();
    let to = Math.max(0, d ? Math.min(d - 0.5, t) : t);
    const r = video.seekable;                       // a copied stream can only be entered where it has been produced so far
    if (r && r.length && to > r.end(r.length - 1)) to = r.end(r.length - 1);
    video.currentTime = to;
  }
  function toggleFullscreen() {
    if (document.fullscreenElement || document.webkitFullscreenElement) (document.exitFullscreen || document.webkitExitFullscreen).call(document);
    else if (stage.requestFullscreen || stage.webkitRequestFullscreen) (stage.requestFullscreen || stage.webkitRequestFullscreen).call(stage);
    else if (video.webkitEnterFullscreen) video.webkitEnterFullscreen(); // iPhone only fullscreens the video element itself
  }
  let scrubbing = false, scrubT = 0;
  const frac = (e) => { const b = bar.getBoundingClientRect(); return Math.min(1, Math.max(0, (e.clientX - b.left) / b.width)); };
  bar.addEventListener('pointerdown', (e) => { scrubbing = true; bar.setPointerCapture(e.pointerId); scrubT = frac(e) * total(); paint(); });
  bar.addEventListener('pointermove', (e) => {
    const x = frac(e), t = x * total();
    hint.textContent = fmt(t); hint.style.left = `${x * 100}%`; hint.classList.remove('hidden');
    if (scrubbing) { scrubT = t; paint(); }
  });
  bar.addEventListener('pointerleave', () => hint.classList.add('hidden'));
  bar.addEventListener('pointerup', () => { if (scrubbing) { scrubbing = false; seekTo(scrubT); wake(); } });
  bar.addEventListener('keydown', (e) => { if (e.key === 'ArrowLeft') { skip(-10); e.preventDefault(); e.stopPropagation(); } else if (e.key === 'ArrowRight') { skip(10); e.preventDefault(); e.stopPropagation(); } });

  function paint() {
    const d = total(), t = scrubbing ? scrubT : video.currentTime;
    const p = d ? Math.min(100, (t / d) * 100) : 0;
    fill.style.width = `${p}%`; knob.style.left = `${p}%`;
    cur.textContent = fmt(t); dur.textContent = fmt(d);
    bar.setAttribute('aria-valuemax', String(Math.round(d))); bar.setAttribute('aria-valuenow', String(Math.round(t)));
    const r = video.buffered;
    let end = 0;
    for (let i = 0; i < r.length; i++) if (r.start(i) <= t + 1 && r.end(i) > end) end = r.end(i);
    buf.style.width = `${d ? Math.min(100, (Math.max(end, t) / d) * 100) : 0}%`;
  }
  video.addEventListener('timeupdate', paint);
  video.addEventListener('progress', paint);
  video.addEventListener('durationchange', paint);
  video.addEventListener('play', () => swap(playBtn, 'pause'));
  video.addEventListener('pause', () => swap(playBtn, 'play'));
  video.addEventListener('volumechange', () => {
    if (pref('rememberVolume') === 'on') { store('pref:volume', String(video.volume)); store('pref:muted', video.muted ? 'on' : 'off'); }
    swap(muteBtn, video.muted || video.volume === 0 ? 'mute' : 'volume'); vol.value = video.muted ? '0' : String(video.volume);
  });
  // click the picture to pause/resume, double-click for full screen (not touch: a tap just reveals the controls)
  video.addEventListener('click', () => { if (matchMedia('(hover: hover)').matches) (video.paused ? video.play() : video.pause()); });
  video.addEventListener('dblclick', toggleFullscreen);
  let grow;
  const el = h('div', { class: 'controls' }, bar,
    h('div', { class: 'crow' }, playBtn, btn('rew', 'Skip back', () => skip(-skipSeconds())), btn('fwd', 'Skip forward', () => skip(skipSeconds())), cur, h('span', { class: 'sep' }, '/'), dur,
      grow = h('span', { class: 'grow' }), speedBtn, muteBtn, vol, fsBtn));
  paint();
  stage.append(speedMenu);
  return { el, skip, toggleFullscreen, slot: grow, toggleCaptions: null, stepSpeed };
}

// ---- captions (WebVTT from the server: text tracks inside the file, or .srt/.vtt files next to it)
const LANG_ALIASES = { eng: 'en', english: 'en', spa: 'es', spanish: 'es', fre: 'fr', fra: 'fr', french: 'fr', ger: 'de', deu: 'de', german: 'de', ita: 'it', italian: 'it',
  por: 'pt', portuguese: 'pt', jpn: 'ja', japanese: 'ja', kor: 'ko', korean: 'ko', chi: 'zh', zho: 'zh', chinese: 'zh', rus: 'ru', russian: 'ru', dut: 'nl', nld: 'nl', dutch: 'nl',
  swe: 'sv', swedish: 'sv', pol: 'pl', polish: 'pl', tur: 'tr', turkish: 'tr', ara: 'ar', arabic: 'ar', hin: 'hi', hindi: 'hi' };
const langKey = (l) => { const k = String(l || '').toLowerCase().split(/[-_ .]/)[0]; return LANG_ALIASES[k] || k.slice(0, 2); };

async function setupCaptions(video, stage, f, ui, wake) {
  const tracks = (await api(`/api/media/${f.mediaId}/captions`)).tracks || [];
  if (!tracks.length) return; // nothing to show: no button
  const btn = h('button', { class: 'cbtn', type: 'button', title: 'Captions (C)', 'aria-label': 'Captions', 'aria-haspopup': 'true' }, icon('cc'));
  const menu = h('div', { class: 'qmenu cc-menu hidden', role: 'menu' });
  let active = null;
  const src = (t) => `/api/media/${f.mediaId}/captions/${t.id}.vtt`;
  function select(t) {
    video.querySelectorAll('track').forEach((x) => x.remove());
    for (const x of video.textTracks) x.mode = 'disabled';
    active = t ? t.id : null;
    btn.classList.toggle('on', !!t);
    if (t) {
      const lang = /^[a-z]{2,3}$/i.test(t.language || '') ? t.language : 'und';
      const el = h('track', { kind: 'subtitles', label: t.label, srclang: lang, src: src(t), default: true });
      el.addEventListener('error', () => { toast('Could not load those captions', true); select(null); });
      video.append(el);
      el.track.mode = 'showing';
    }
  }
  function renderMenu() {
    menu.replaceChildren(...[{ id: null, label: 'Off' }, ...tracks].map((t) => h('button', {
      type: 'button', role: 'menuitemradio', 'aria-checked': String(active === t.id), class: active === t.id ? 'on' : '', disabled: t.supported === false ? true : null,
      onclick: () => { select(t.id ? t : null); menu.classList.add('hidden'); },
    }, h('span', {}, t.label, t.forced ? ' (forced)' : ''), h('small', {}, active === t.id ? '✓' : t.supported === false ? 'picture-based' : t.source === 'file' ? 'file' : ''))));
  }
  btn.addEventListener('click', () => { renderMenu(); menu.classList.toggle('hidden'); wake(); });
  stage.addEventListener('click', (e) => { if (!menu.contains(e.target) && !btn.contains(e.target)) menu.classList.add('hidden'); });
  ui.slot.after(btn);
  stage.append(menu);
  const usable = tracks.filter((t) => t.supported !== false);
  ui.toggleCaptions = () => {
    if (active) { select(null); toast('Captions off'); return; }
    const t = pickDefault(usable, true) || usable[0];
    if (t) { select(t); toast(`Captions: ${t.label}`); } else toast('No captions available');
  };
  const pick = pref('cc') === 'off' ? null : pickDefault(usable, pref('cc') === 'always');
  if (pick) select(pick);
}

function pickDefault(usable, fallbackToAny) {
  const want = langKey(pref('ccLang'));
  const inLang = usable.filter((t) => langKey(t.language || t.label) === want);
  return inLang.find((t) => !t.forced) || inLang[0] || (fallbackToAny ? usable.find((t) => !t.forced) || usable[0] : null) || null;
}

const MODE_LABEL = { direct: 'Direct play', remux: 'Direct stream', transcode: 'Transcoding' };
const QUALITIES = [
  { name: '1080p', width: 1920, mbps: 8 },
  { name: '720p', width: 1280, mbps: 4 },
  { name: '480p', width: 854, mbps: 1.8 },
  { name: '360p', width: 640, mbps: 0.9 },
];
// Offer only qualities the source can actually fill (never upscale).
const qualitiesFor = (f) => QUALITIES.filter((q) => f.width && q.width <= f.width * 1.05);
const savedQuality = () => store('quality') || 'original';

async function showPlayer(id, params) {
  const f = await api(`/api/media/${encodeURIComponent(id)}/play?${capsQuery()}`);
  const token = playerToken;
  if (token !== playerToken) return;
  document.title = `${f.title} · ${APP.name}`;
  document.body.classList.add('watching');

  const start = params.has('t') ? Math.max(0, parseFloat(params.get('t')) || 0) : pref('resume') === 'start' ? 0 : f.resumeSeconds;
  const video = h('video', { autoplay: pref('autoplay') === 'on', playsinline: true, preload: 'auto' });
  const spinner = h('div', { class: 'buffering hidden' }, h('i'));
  const chip = h('button', { class: 'mode-chip', type: 'button', title: 'How this is being played' }, 'Starting…');
  const options = qualitiesFor(f);
  const qBtn = options.length && APP.allowTranscoding ? h('button', { class: 'round', type: 'button', title: 'Quality', 'aria-label': 'Quality', 'aria-haspopup': 'true' }, icon('sliders')) : null;
  const qMenu = h('div', { class: 'qmenu hidden', role: 'menu' });
  const reason = h('div', { class: 'reason hidden' }, f.reason);
  const nextBtn = h('a', { class: 'btn next-btn hidden', href: f.nextMediaId ? `#/watch/${f.nextMediaId}` : '#' }, icon('next'), h('span', {}, 'Next episode', f.nextTitle ? h('small', {}, f.nextTitle) : null));
  const back = h('button', { class: 'round', type: 'button', 'aria-label': 'Back', onclick: () => leave() }, icon('back'));
  const heading = f.title.includes(' – ') ? [f.title.split(' – ')[0], h('small', {}, f.title.split(' – ').slice(1).join(' – '))] : f.title;
  const stage = h('div', { class: 'player' }, video, spinner,
    h('div', { class: 'player-top' }, back, h('div', { class: 'player-title' }, heading), chip, qBtn), reason, qMenu, nextBtn);
  setView(stage);
  window.scrollTo(0, 0);
  const wakeRef = () => wake();
  const ui = buildControls(video, stage, f, wakeRef);
  stage.append(ui.el);
  setupCaptions(video, stage, f, ui, wakeRef).catch(() => {}); // captions are optional: never let them hold up playback

  function leave() { if (history.length > 1) history.back(); else location.hash = '#/'; }
  let quality = savedQuality();
  if (quality !== 'original' && !options.some((q) => q.name === quality)) quality = 'original'; // saved choice doesn't fit this file
  function report(mode, text) {
    chip.textContent = (MODE_LABEL[mode] || mode) + (quality !== 'original' && mode === 'transcode' ? ` · ${quality}` : '');
    reason.textContent = text || '';
  }
  report(f.mode, f.reason);
  chip.addEventListener('click', () => reason.classList.toggle('hidden'));

  // ---- progress: saved to the server every 10s, on pause, and as the tab closes (so it resumes on any device)
  const url = `/api/media/${f.mediaId}/progress`;
  const body = () => {
    const d = f.durationSeconds || (Number.isFinite(video.duration) ? video.duration : undefined);
    return JSON.stringify({ position: video.currentTime, duration: d });
  };
  const canSave = () => Number.isFinite(video.currentTime) && video.currentTime > 5;
  const saveNow = () => { if (canSave()) fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: body(), keepalive: true }).catch(() => {}); };
  const saveBeacon = () => { if (canSave()) { try { navigator.sendBeacon(url, new Blob([body()], { type: 'application/json' })); } catch { /* ignore */ } } };
  const saveTimer = setInterval(() => { if (!video.paused) saveNow(); }, 10000);

  // ---- idle UI: hide the title bar while playing
  let idleTimer = null;
  const wake = () => { stage.classList.remove('idle'); clearTimeout(idleTimer); idleTimer = setTimeout(() => { if (!video.paused) stage.classList.add('idle'); }, 3200); };
  ['mousemove', 'touchstart', 'keydown'].forEach((ev) => stage.addEventListener(ev, wake, { passive: true }));
  video.addEventListener('play', () => { stage.classList.add('playing'); wake(); });
  video.addEventListener('pause', () => { stage.classList.remove('playing', 'idle'); saveNow(); });
  video.addEventListener('waiting', () => spinner.classList.remove('hidden'));
  ['playing', 'canplay', 'seeked'].forEach((ev) => video.addEventListener(ev, () => spinner.classList.add('hidden')));
  wake();

  // ---- next episode
  video.addEventListener('timeupdate', () => {
    const d = f.durationSeconds || video.duration;
    if (f.nextMediaId && d && d - video.currentTime < 30) nextBtn.classList.remove('hidden'); else nextBtn.classList.add('hidden');
  });
  video.addEventListener('ended', () => {
    fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ position: f.durationSeconds || video.duration, duration: f.durationSeconds || video.duration, watched: true }), keepalive: true }).catch(() => {});
    if (f.nextMediaId && pref('autonext') === 'on') location.hash = `#/watch/${f.nextMediaId}`; else leave();
  });

  // ---- quality menu: pick a resolution; playback continues from the same moment
  function renderQualityMenu() {
    const rows = [{ name: 'original', label: `Original${resLabel(f.width, f.height) ? ` (${resLabel(f.width, f.height)})` : ''}`, note: 'best available' }]
      .concat(options.map((q) => ({ name: q.name, label: q.name, note: `up to ${q.mbps} Mbps` })));
    qMenu.replaceChildren(...rows.map((r) => h('button', { type: 'button', role: 'menuitemradio', 'aria-checked': String(r.name === quality), class: r.name === quality ? 'on' : '', onclick: () => chooseQuality(r.name) },
      h('span', {}, r.label), h('small', {}, r.name === quality ? '✓' : r.note))));
  }
  async function chooseQuality(name) {
    qMenu.classList.add('hidden');
    if (name === quality) return;
    const at = video.currentTime || 0, wasPlaying = !video.paused;
    quality = name; store('quality', name);
    saveNow();
    ctl.stop(); // ends the current stream (direct listeners or the server-side session)
    video.removeAttribute('src'); video.load();
    await sleep(80); // let the element flush events queued by clearing the source
    report('remux', 'Switching quality…');
    try {
      if (name === 'original') await startPlayback(video, f, report, ctl, at);
      else await startHls(video, f, report, ctl, at, false, name);
      if (wasPlaying) video.play().catch(() => {});
    } catch (e) { report('error', e.message); toast(e.message || 'Could not switch quality', true); }
  }
  if (qBtn) {
    qBtn.addEventListener('click', () => { renderQualityMenu(); qMenu.classList.toggle('hidden'); wake(); });
    stage.addEventListener('click', (e) => { if (!qMenu.contains(e.target) && e.target !== qBtn && !qBtn.contains(e.target)) qMenu.classList.add('hidden'); });
  }

  // ---- keyboard
  const onKey = (e) => {
    if (e.target.closest && e.target.closest('input, textarea, select')) return;
    const seek = (dt) => ui.skip(dt);
    switch (e.key) {
      case ' ': case 'k': e.preventDefault(); if (video.paused) video.play(); else video.pause(); break;
      case 'ArrowLeft': case 'j': e.preventDefault(); seek(-skipSeconds()); break;
      case 'ArrowRight': case 'l': e.preventDefault(); seek(skipSeconds()); break;
      case 'c': if (ui.toggleCaptions) ui.toggleCaptions(); break;
      case '<': ui.stepSpeed(-1); break;
      case '>': ui.stepSpeed(1); break;
      case 'ArrowUp': e.preventDefault(); video.volume = Math.min(1, video.volume + 0.1); break;
      case 'ArrowDown': e.preventDefault(); video.volume = Math.max(0, video.volume - 0.1); break;
      case 'm': video.muted = !video.muted; break;
      case 'f': ui.toggleFullscreen(); break;
      case 'n': if (f.nextMediaId) location.hash = `#/watch/${f.nextMediaId}`; break;
      case 'Escape': if (!qMenu.classList.contains('hidden')) qMenu.classList.add('hidden'); else if (!document.fullscreenElement) leave(); break;
      default: return;
    }
    wake();
  };
  document.addEventListener('keydown', onKey);
  window.addEventListener('pagehide', saveBeacon);

  const ctl = newController();
  activePlayback = {
    dispose() {
      clearInterval(saveTimer); clearTimeout(idleTimer);
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('pagehide', saveBeacon);
      saveNow(); // final position
      try { video.pause(); } catch { /* ignore */ }
      ctl.stop(); // stops the HLS session => the server kills ffmpeg
      video.removeAttribute('src');
      video.load(); // aborts the in-flight media request so the server stops streaming
    },
  };

  try {
    if (quality !== 'original') await startHls(video, f, report, ctl, start, false, quality);
    else await startPlayback(video, f, report, ctl, start);
  }
  catch (e) { if (token === playerToken) { report('error', e.message); toast(e.message || 'Playback failed', true); } return; }
  if (token !== playerToken) ctl.stop(); // navigated away while the stream was starting
}

// ---- diagnostics: tell the server why playback fell back, so a "why did this transcode?" never needs guessing
function reportIssue(kind, f, detail) {
  try {
    const probe = document.createElement('video');
    const err = ($('video') || {}).error;
    fetch('/api/client-events', {
      method: 'POST', headers: { 'content-type': 'application/json' }, keepalive: true,
      body: JSON.stringify({ kind, mediaId: f ? f.mediaId : null, mode: f ? f.mode : null, detail: String(detail || '').slice(0, 300),
        mediaError: err ? `${err.code}:${String(err.message || '').slice(0, 120)}` : null,
        nativeHls: !!probe.canPlayType('application/vnd.apple.mpegurl'), mse: !!window.MediaSource, caps: CAPS }),
    }).catch(() => {});
  } catch { /* diagnostics must never break playback */ }
}

// "The video can't be decoded" is only knowable once frames should be flowing: some browsers (notably Safari's native HLS)
// report videoWidth 0 until the first frame renders, so checking at loadedmetadata wrongly condemned working streams.
function watchDecoding(video, ctl, onFail) {
  let timer;
  const onPlaying = () => {
    clearTimeout(timer);
    timer = setTimeout(() => { if (!video.paused && video.currentTime > 0.5 && video.videoWidth === 0) onFail(); }, 3000);
  };
  video.addEventListener('playing', onPlaying);
  ctl.onStop(() => { clearTimeout(timer); video.removeEventListener('playing', onPlaying); });
}

// A copied stream grows as the encoder works, so a resume point may not exist yet: seek once it does.
function seekWhenReady(video, ctl, t, report) {
  let n = 0;
  const timer = setInterval(() => {
    n++;
    const r = video.seekable;
    if (r && r.length && r.end(r.length - 1) >= t + 2) { clearInterval(timer); video.currentTime = t; }
    else if (n > 240) clearInterval(timer); // ~2 minutes: give up and play from the start
  }, 500);
  ctl.onStop(() => clearInterval(timer));
}

// Cheapest path first: direct play, then direct stream (HLS with the video copied), then transcode. Each step also
// acts as the fallback for the one before it, in case a browser claims support it doesn't actually have.
async function startPlayback(video, f, report, ctl, start) {
  if (f.mode !== 'direct') { await startHls(video, f, report, ctl, start, false); return; }
  report('direct', f.reason); // (also resets the label after a quality switch)

  let fellBack = false;
  const fallBack = (why) => {
    if (fellBack) return;
    fellBack = true;
    report('remux', `${why}, switching to a direct stream…`);
    startHls(video, f, report, ctl, video.currentTime || start, false).catch((e) => report('error', e.message));
  };
  // Clearing src (e.g. when switching quality) queues an "empty src" error event: only a failure if a source is actually loaded.
  const onError = () => { if (video.currentSrc) { reportIssue('direct-error', f, 'media error during direct play'); fallBack('Direct play failed'); } };
  const onMeta = () => { if (start > 1 && start < (video.duration || Infinity) - 5) video.currentTime = start; };
  // Audio can play while the video track is undecodable (videoWidth stays 0): that is a failure too, but only judge it once playing.
  watchDecoding(video, ctl, () => { reportIssue('video-not-decoded', f, 'direct play: audio plays but no video frames'); fallBack("This browser couldn't decode the video"); });
  video.addEventListener('error', onError);
  video.addEventListener('loadedmetadata', onMeta, { once: true });
  ctl.onStop(() => { video.removeEventListener('error', onError); video.removeEventListener('loadedmetadata', onMeta); });
  video.src = f.streamUrl;
}

async function startHls(video, f, report, ctl, resumeAt, forceTranscode, quality) {
  report('remux', 'Preparing stream…');
  const res = await fetch('/api/hls/start', {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ mediaId: f.mediaId, video: CAPS.v, audio: CAPS.a, containers: CAPS.c, forceTranscode, startSeconds: resumeAt > 1 ? resumeAt : 0, quality: quality && quality !== 'original' ? quality : null }),
  });
  if (!res.ok) {
    let detail = '';
    try { detail = (await res.json()).detail || ''; } catch { /* not json */ }
    throw new Error(`Could not start streaming (${res.status}). ${detail}`.trim());
  }
  const s = await res.json();
  const base = `/api/hls/${s.sessionId}`;

  // Everything below is registered for cleanup immediately so no path can leak the server-side ffmpeg.
  const stopSession = () => { try { navigator.sendBeacon(`${base}/stop`); } catch { /* ignore */ } };
  const ping = setInterval(() => fetch(`${base}/ping`, { method: 'POST' }).catch(() => {}), 15000);
  window.addEventListener('pagehide', stopSession);
  let hls = null;
  let retried = false;
  // A copied stream that the browser then can't decode is retried once as a real transcode.
  const retryAsTranscode = (why) => {
    reportIssue(forceTranscode || retried || s.mode !== 'remux' ? 'playback-error' : 'retry-as-transcode', f, why);
    if (forceTranscode || retried || s.mode !== 'remux') { report('error', `Playback error: ${why}`); return; }
    retried = true;
    const at = video.currentTime || resumeAt;
    ctl.stop();
    startHls(video, f, report, ctl, at, true, quality).catch((e) => report('error', e.message));
  };
  // Segmented transcodes can start anywhere (the playlist is complete). Copied streams grow as they're produced, so resume later.
  const lateSeek = resumeAt > 1 && !s.vod;
  const onMeta = () => { if (resumeAt > 1 && s.vod && !hls) video.currentTime = resumeAt; }; // native HLS (hls.js uses startPosition)
  if (s.mode === 'remux') watchDecoding(video, ctl, () => retryAsTranscode("the video couldn't be decoded"));
  const onNativeError = () => { if (video.currentSrc) retryAsTranscode('the stream could not be played (native HLS)'); };
  video.addEventListener('loadedmetadata', onMeta, { once: true });
  ctl.onStop(() => {
    clearInterval(ping);
    window.removeEventListener('pagehide', stopSession);
    video.removeEventListener('loadedmetadata', onMeta);
    video.removeEventListener('error', onNativeError);
    if (hls) hls.destroy();
    fetch(`${base}/stop`, { method: 'POST', keepalive: true }).catch(() => {});
  });

  // Safari plays HLS (including HEVC in fragmented MP4) natively; its MSE path does not handle that reliably, so prefer native there.
  const nativeHls = !!video.canPlayType('application/vnd.apple.mpegurl');
  if (nativeHls && (IS_SAFARI || !(window.Hls && Hls.isSupported()))) {
    video.addEventListener('error', onNativeError);
    video.src = s.playlistUrl;
  } else if (window.Hls && Hls.isSupported()) {
    // A far seek makes the server restart its encoder there, so a segment can legitimately take a while: don't time out early.
    const slow = { default: { maxTimeToFirstByteMs: 60000, maxLoadTimeMs: 120000, timeoutRetry: { maxNumRetry: 2, retryDelayMs: 0, maxRetryDelayMs: 0 }, errorRetry: { maxNumRetry: 3, retryDelayMs: 500, maxRetryDelayMs: 4000 } } };
    hls = new Hls({ startPosition: resumeAt > 1 && s.vod ? resumeAt : -1, fragLoadPolicy: slow });
    hls.on(Hls.Events.ERROR, (_e, d) => { if (d.fatal) retryAsTranscode(`${d.type}/${d.details}${d.response ? ' http ' + d.response.code : ''}`); });
    hls.loadSource(s.playlistUrl);
    hls.attachMedia(video);
  } else {
    throw new Error('This browser cannot play HLS.');
  }
  if (lateSeek) seekWhenReady(video, ctl, resumeAt, report);
  report(s.mode, s.reason || (s.mode === 'remux' ? 'Direct stream (video untouched)' : 'Transcoding'));
}

// ================================================================ search box + shortcuts + boot
let searchTimer;
$('#searchform').addEventListener('submit', (e) => e.preventDefault());
$('#search').addEventListener('input', (e) => {
  clearTimeout(searchTimer);
  const q = e.target.value.trim();
  searchTimer = setTimeout(() => { location.hash = q ? `#/search/${encodeURIComponent(q)}` : '#/'; }, 250);
});
document.addEventListener('keydown', (e) => {
  if (e.key === '/' && !e.target.closest('input, textarea, select') && !document.body.classList.contains('watching')) { e.preventDefault(); $('#search').focus(); }
});

applyPrefs();
let appStarted = false;
function startApp() {
  document.body.classList.remove('auth-screen');
  paintUserChip();
  refreshApp();
  if (!appStarted) { appStarted = true; window.addEventListener('hashchange', () => { if (!AUTH.locked) route(); }); }
  route();
  api('/api/library/status').then((s) => { if (s.scanning) watchScan(false); }).catch(() => {});
  loadLiveStatus(); // reveals the Live TV link when a source is configured
}

// Decide what to show first: the one-time setup, the sign-in form, or the app.
(async function boot() {
  let st;
  try { st = await (await fetch('/api/auth/state', { cache: 'no-store' })).json(); }
  catch { view.replaceChildren(h('div', { class: 'error' }, 'Can’t reach the server. Check that it’s running, then reload.')); return; }
  AUTH.enabled = st.authEnabled; AUTH.user = st.user; AUTH.name = st.name; AUTH.displayName = st.displayName; applyServerName(st.serverName);
  if (st.setupRequired) { showSetup(st, () => { location.hash = '#/welcome'; startApp(); }); return; }
  if (!st.authenticated) { showLogin(startApp); return; }
  startApp();
})();
