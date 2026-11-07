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
  home: { d: 'M3 11l9-8 9 8v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z' },
  film: { d: 'M4 4h16v16H4zM8 4v16M16 4v16M4 9h4M16 9h4M4 15h4M16 15h4' },
  tv: { d: 'M3 7h18v12H3zM8 3l4 4 4-4' },
  live: { d: 'M12 11a1 1 0 1 0 0 2 1 1 0 0 0 0-2zM7.8 7.8a6 6 0 0 0 0 8.4M16.2 7.8a6 6 0 0 1 0 8.4M4.9 4.9a10 10 0 0 0 0 14.2M19.1 4.9a10 10 0 0 1 0 14.2' },
  rec: { d: 'M12 6a6 6 0 1 0 0 12 6 6 0 0 0 0-12z' },
  download: { d: 'M12 4v11M7 11l5 5 5-5M5 20h14' },
  search: { d: 'M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM21 21l-4.6-4.6' },
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
  audio: { d: 'M11 5L6 9H3v6h3l5 4zM15.5 8.5a5 5 0 0 1 0 7M19 5a9 9 0 0 1 0 14' },
  cast: { d: 'M3 18a3 3 0 0 1 3 3M3 14a7 7 0 0 1 7 7M3 10a11 11 0 0 1 11 11M3 6h16a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-3' },
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

// Every call to the server goes through here: a time limit, one place that notices a signed-out session, and errors a person can read.
async function api(path, { timeoutMs = 30000, ...opts } = {}) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), timeoutMs);
  let res;
  try { res = await fetch(path, { ...opts, signal: opts.signal || ctl.signal }); }
  catch (e) { throw new Error(e.name === 'AbortError' ? 'The server took too long to answer.' : 'Can’t reach the server. Check your connection.'); }
  finally { clearTimeout(timer); }
  if (res.status === 401 && !path.startsWith('/api/auth/')) { authLost(); throw new Error('Signed out'); }
  if (!res.ok) {
    let message = '';
    try { message = (await res.json()).error || ''; } catch { /* the body wasn't JSON */ }
    throw new Error(message || ({ 403: 'You don’t have permission to do that.', 404: 'Not found', 429: 'Too many requests. Try again in a moment.' }[res.status] || `Server error (${res.status})`));
  }
  return res.status === 204 ? null : res.json();
}

// Anything unexpected that escapes the page's own handling is reported once to the server log (a handful per page load), so a bug in the page
// never goes unnoticed.
let reportedErrors = 0;
function reportClientError(kind, detail) {
  if (++reportedErrors > 5) return;
  try {
    fetch('/api/client-events', { method: 'POST', headers: { 'content-type': 'application/json' }, keepalive: true,
      body: JSON.stringify({ kind, detail: String(detail || '').slice(0, 300), mode: null, mediaId: null, mediaError: null, nativeHls: false, mse: !!window.MediaSource }) }).catch(() => {});
  } catch { /* reporting must never throw */ }
}
window.addEventListener('error', (e) => reportClientError('js-error', `${e.message} @ ${String(e.filename || '').split('/').pop()}:${e.lineno}`));
window.addEventListener('unhandledrejection', (e) => reportClientError('js-rejection', (e.reason && e.reason.message) || e.reason));

function toast(msg, bad) {
  if (AUTH.locked) return;
  const t = h('div', { class: 'toast' + (bad ? ' bad' : '') }, msg);
  $('#toasts').append(t);
  setTimeout(() => t.remove(), 4500);
}

const fmtRuntime = formatRuntime;
const fmtLeft = (pos, dur) => (dur && dur > pos ? formatLeft(dur - pos) : '');
const resLabel = (w, hgt) => (!w ? null : w >= 3500 || hgt >= 2000 ? '4K' : w >= 1800 || hgt >= 1000 ? '1080p' : w >= 1200 || hgt >= 700 ? '720p' : 'SD');
const codecLabel = (c) => ({ hevc: 'HEVC', h264: 'H.264', vp9: 'VP9', av1: 'AV1', vc1: 'VC-1', mpeg4: 'MPEG-4' }[c] || (c ? c.toUpperCase() : null));

function store(key, value) { try { if (value === undefined) return localStorage.getItem(key); localStorage.setItem(key, value); } catch { /* storage unavailable */ } return null; }

// ---- cleanup registry: everything a page starts (timers, observers, listeners) is stopped when the route changes
let leaveFns = [];
const onLeave = (fn) => leaveFns.push(fn);
function runLeave() { const fns = leaveFns; leaveFns = []; for (const fn of fns.reverse()) { try { fn(); } catch { /* best effort */ } } }

// ================================================================ artwork: image over a generated gradient
function hashHue(s) { let n = 7; for (const ch of s) n = (n * 31 + ch.codePointAt(0)) % 360; return n; }

function art(src, title, { progress, badge, overlay, portrait, left, watched } = {}) {
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
  if (watched) el.append(h('span', { class: 'seen', title: 'Watched', 'aria-label': 'Watched' }, icon('check')));
  if (left) el.append(h('span', { class: 'leftlabel' }, left));            // "42m left" on anything you're partway through
  if (progress > 0) el.append(h('span', { class: 'progress' }, h('i', { style: `width:${Math.min(100, Math.round(progress * 100))}%` })));
  return el;
}

function tile({ href, title, hover, sub, img, progress, badge, portrait, left, watched }) {
  return h('a', { class: 'tile', href, title: hover || title }, art(img, title, { progress, badge, overlay: true, portrait, left, watched }),
    h('div', { class: 't' }, title), sub ? h('div', { class: 's' }, sub) : null);
}

const itemHref = (i) => (i.type === 'series' ? `#/series/${encodeURIComponent(i.key)}` : `#/movie/${i.mediaId}`);
const itemSub = (i) => [i.year, i.type === 'series' ? `${i.episodeCount} episode${i.episodeCount === 1 ? '' : 's'}` : null].filter(Boolean).join(' · ');
// Real posters (portrait) when the library has them; otherwise landscape frames. A title without a poster in poster mode
// shows its frame cropped to the same shape so a grid stays tidy.
const POSTER_MIN = 0.5;
const posterShare = (items) => (items.length ? items.filter((i) => i.posterUrl).length / items.length : 0);
const itemTile = (i, poster = false) => tile({ href: itemHref(i), title: i.title, hover: [i.title, i.rating ? `★ ${i.rating.toFixed(1)}` : null, i.genres].filter(Boolean).join(' · '), sub: itemSub(i), img: poster ? i.posterUrl || i.imageUrl : i.imageUrl, badge: i.isNew ? 'NEW' : null, portrait: poster,
  progress: i.progress, left: i.leftSeconds ? formatLeft(i.leftSeconds) : null, watched: i.watched });
const continueTile = (c) => tile({
  href: `#/watch/${c.mediaId}`, title: c.title, img: c.imageUrl,
  sub: c.nextUp ? `Up next · ${c.subtitle}` : [c.subtitle, fmtLeft(c.positionSeconds, c.durationSeconds)].filter(Boolean).join(' · '),
  progress: c.durationSeconds ? c.positionSeconds / c.durationSeconds : 0,
  left: c.durationSeconds && c.durationSeconds > c.positionSeconds && !c.nextUp ? formatLeft(c.durationSeconds - c.positionSeconds) : null,
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

// Menu icons: added here so the links in index.html stay plain text.
const NAV_ICONS = { home: 'home', movies: 'film', shows: 'tv', mylist: 'list', live: 'live', recordings: 'rec', downloads: 'download' };
document.querySelectorAll('#nav a').forEach((a) => a.prepend(h('span', { class: 'nav-ico' }, icon(NAV_ICONS[a.dataset.route] || 'play'))));

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
  const section = !page ? 'home' : page === 'movies' || page === 'movie' ? 'movies' : page === 'shows' || page === 'series' ? 'shows' : page === 'live' ? 'live' : page === 'mylist' ? 'mylist' : page === 'recordings' ? 'recordings' : page === 'downloads' ? 'downloads' : '';
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
    else if (page === 'mylist') await showMyList();
    else if (page === 'recordings') await showRecordings();
    else if (page === 'downloads') await showDownloads(params);
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
  const [movies, shows, cont, status, live, myList] = await Promise.all([
    api('/api/items?type=movie&sort=recent&limit=24'),
    api('/api/items?type=series&sort=recent&limit=24'),
    api('/api/continue?limit=12').catch(() => []),
    api('/api/library/status').catch(() => ({})),
    loadLiveChannelsForHome(),
    api('/api/watchlist').then((d) => d.items.slice(0, 24)).catch(() => []),
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
  if (myList.length && pref('homeMyList') === 'on') page.append(row('My List', myList.map((i) => itemTile(i, posterShare(myList) >= POSTER_MIN)), '#/mylist', posterShare(myList) >= POSTER_MIN));
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
const fmtBytes = formatBytes;
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
  const addHint = query && (arrInfo.radarr || arrInfo.sonarr) ? h('p', { class: 'hint' }, 'Can’t find it? ', h('a', { href: `#/downloads?q=${encodeURIComponent(query)}`, style: 'text-decoration:underline' }, 'Search Radarr and Sonarr to add it')) : null;
  setView(h('div', { class: 'page' }, h('div', { class: 'page-head' }, h('h1', {}, title), countEl, randomBtn, viewBox, genreSel, select), intro, addHint, grid, sentinel));

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
  if (!isAdmin()) return null;   // fixing a match changes the shared library
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
  const listed = await inMyList('movie', String(f.mediaId));
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
            resume ? h('a', { class: 'btn glass', href: `#/watch/${f.mediaId}?t=0` }, 'Start over') : null,
            myListButton('movie', String(f.mediaId), listed))))),
    info);
}

async function showSeries(key) {
  const s = await api(`/api/items/series/${encodeURIComponent(key)}`);
  const listed = await inMyList('series', key);
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
            } }, icon('dice'), 'Random episode'),
            myListButton('series', key, listed)) : null))),
    h('div', { class: 'section' }, h('div', { class: 'seasonbar' }, pills, watchedMenu(key, () => s.seasons[current], () => route())), list));
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

// ================================================================ player (see player.js)
let activePlayback = null;
let playerToken = 0; // bumped on every navigation so a slow start can tell it was abandoned
function teardownPlayer() {
  playerToken++;
  if (!activePlayback) return;
  const ap = activePlayback;
  activePlayback = null;
  ap.dispose();
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
// Installable app + offline page shell. (Browsers only allow this on https:// or localhost.)
if ('serviceWorker' in navigator && (location.protocol === 'https:' || location.hostname === 'localhost')) navigator.serviceWorker.register('/sw.js').catch(() => {});
let appStarted = false;
function startApp() {
  document.body.classList.remove('auth-screen');
  paintUserChip();
  refreshApp();
  if (!appStarted) { appStarted = true; window.addEventListener('hashchange', () => { if (!AUTH.locked) route(); }); }
  route();
  api('/api/library/status').then((s) => { if (s.scanning) watchScan(false); }).catch(() => {});
  loadLiveStatus(); // reveals the Live TV link when a source is configured
  loadArrStatus();  // ...and Downloads, when Radarr or Sonarr is
}

// Decide what to show first: the one-time setup, the sign-in form, or the app.
(async function boot() {
  let st;
  try { st = await (await fetch('/api/auth/state', { cache: 'no-store' })).json(); }
  catch { view.replaceChildren(h('div', { class: 'error' }, 'Can’t reach the server. Check that it’s running, then reload.')); return; }
  AUTH.enabled = st.authEnabled; AUTH.user = st.user; AUTH.name = st.name; AUTH.displayName = st.displayName; AUTH.role = st.role || 'admin'; applyServerName(st.serverName);
  if (st.setupRequired) { showSetup(st, () => { location.hash = '#/welcome'; startApp(); }); return; }
  if (!st.authenticated) { showLogin(startApp); return; }
  startApp();
})();
