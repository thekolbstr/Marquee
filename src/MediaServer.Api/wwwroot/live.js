'use strict';

// ================================================================ Live TV
// Loaded before app.js; only declares functions (they use app.js helpers such as h, api, icon, toast at call time).

const GUIDE_PX = 6;      // timeline scale: pixels per minute
const GUIDE_HOURS = 6;   // window shown in the guide
const CHAN_W = 214;      // width of the sticky channel column

const nowTs = () => Math.floor(Date.now() / 1000);
const fmtClock = (ts) => new Date(ts * 1000).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
const fmtMinsLeft = (stop) => { const m = Math.max(0, Math.round((stop - nowTs()) / 60)); return m >= 60 ? `${Math.floor(m / 60)}h ${m % 60}m left` : `${m}m left`; };
const progressOf = (p) => (p ? Math.min(1, Math.max(0, (nowTs() - p.start) / (p.stop - p.start))) : 0);

let liveInfo = null; // last /api/live/status

async function loadLiveStatus() {
  try { liveInfo = await api('/api/live/status'); } catch { liveInfo = { configured: false, channels: 0 }; }
  const link = document.querySelector('#nav a[data-route=live]');
  if (link) link.classList.toggle('hidden', !liveInfo.configured);
  document.querySelector('#nav a[data-route=recordings]')?.classList.toggle('hidden', !liveInfo.configured);
  return liveInfo;
}

// A channel's logo: our own proxied image over a lettered placeholder (logos are often transparent PNGs meant for dark backgrounds).
function channelLogo(ch, size) {
  const box = h('span', { class: `clogo ${size || ''}` }, h('span', { class: 'cinit' }, (ch.name || '?').trim().charAt(0).toUpperCase()));
  if (ch.logoUrl) {
    const img = h('img', { alt: '', loading: 'lazy', decoding: 'async', src: ch.logoUrl });
    img.addEventListener('load', () => { img.classList.add('loaded'); box.classList.add('has'); });
    img.addEventListener('error', () => img.remove());
    box.append(img);
  }
  return box;
}

function favButton(ch, onChange) {
  const b = h('button', { class: 'star' + (ch.favorite ? ' on' : ''), type: 'button', title: ch.favorite ? 'Remove from favorites' : 'Add to favorites', 'aria-label': 'Favorite', 'aria-pressed': String(!!ch.favorite) }, icon('star'));
  b.addEventListener('click', async (e) => {
    e.preventDefault(); e.stopPropagation();
    try {
      const res = await fetch(`/api/live/channels/${ch.id}/favorite`, { method: ch.favorite ? 'DELETE' : 'POST' });
      if (!res.ok) throw new Error();
      ch.favorite = !ch.favorite;
      b.classList.toggle('on', ch.favorite); b.setAttribute('aria-pressed', String(ch.favorite));
      b.title = ch.favorite ? 'Remove from favorites' : 'Add to favorites';
      if (onChange) onChange(ch);
    } catch { toast('Could not update favorites', true); }
  });
  return b;
}

// A tile for the home page row.
function liveTile(ch) {
  const pct = Math.round(progressOf(ch.now) * 100);
  return h('a', { class: 'tile', href: `#/live/${ch.id}`, title: ch.name },
    h('div', { class: 'art livetile' }, channelLogo(ch, 'xl'), h('span', { class: 'livebadge' }, 'LIVE'),
      h('span', { class: 'play-overlay' }, h('span', {}, icon('play'))),
      ch.now ? h('span', { class: 'progress' }, h('i', { style: `width:${pct}%` })) : null),
    h('div', { class: 't' }, ch.now ? ch.now.title : ch.name),
    h('div', { class: 's' }, `${ch.number != null ? ch.number + ' · ' : ''}${ch.name}${ch.now ? ` · ${fmtMinsLeft(ch.now.stop)}` : ''}`));
}

// ---------------------------------------------------------------- the Live TV page: guide + list
async function showLive() {
  const st = await loadLiveStatus();
  if (!st.configured) {
    setView(h('div', { class: 'empty' }, h('h2', {}, "Live TV isn't set up"), h('p', {}, 'Point the server at an M3U playlist (and optionally an XMLTV guide) with LiveTv__M3uUrl to get channels here.')));
    return;
  }
  if (st.channels === 0) {
    setView(h('div', { class: 'empty' }, h('h2', {}, st.refreshing ? 'Loading channels…' : 'No channels yet'), h('p', {}, st.lastError || (st.refreshing ? 'This takes a moment.' : 'The playlist has not been loaded yet.')),
      h('button', { class: 'btn', type: 'button', onclick: async () => { await fetch('/api/live/refresh?background=true', { method: 'POST' }); toast('Refreshing channels…'); setTimeout(route, 4000); } }, 'Refresh channels')));
    return;
  }

  const groups = await api('/api/live/groups').catch(() => []);
  let myLists = (await api('/api/live/lists').catch(() => ({ lists: [] }))).lists;
  let group = store('live-group') || '';
  if (group && group !== '★' && !group.startsWith('list:') && !groups.some((g) => g.name === group)) group = '';
  if (group.startsWith('list:') && !myLists.some((l) => `list:${l.id}` === group)) group = '';
  let query = '';
  let mode = store('live-view') || (matchMedia('(max-width: 720px)').matches ? 'list' : 'guide');

  const body = h('div', { class: 'livebody' });
  const groupSel = h('select', { class: 'sort', 'aria-label': 'Channel group', onchange: (e) => { group = e.target.value; store('live-group', group); draw(); } },
    h('option', { value: '' }, `All channels (${st.channels})`), h('option', { value: '★' }, '★ Favorites'));
  const fillGroups = () => {
    groupSel.replaceChildren(h('option', { value: '' }, `All channels (${st.channels})`), h('option', { value: '★' }, '★ Favorites'),
      myLists.length ? h('optgroup', { label: 'My lists' }, myLists.map((l) => h('option', { value: `list:${l.id}` }, `${l.name} (${l.channels})`))) : null,
      h('optgroup', { label: 'Groups' }, groups.map((g) => h('option', { value: g.name }, `${g.name} (${g.count})`))));
    groupSel.value = group;
  };
  fillGroups();
  const listsBtn = h('button', { class: 'btn glass small', type: 'button', title: 'Make your own channel lists', onclick: () => openListsDialog(myLists, async () => {
    myLists = (await api('/api/live/lists').catch(() => ({ lists: [] }))).lists;
    if (group.startsWith('list:') && !myLists.some((l) => `list:${l.id}` === group)) { group = ''; store('live-group', ''); }
    fillGroups(); draw();
  }) }, icon('list'), 'Lists');
  const search = h('input', { type: 'search', class: 'dlg-input livesearch', placeholder: 'Find a channel', 'aria-label': 'Find a channel' });
  let st1; search.addEventListener('input', () => { clearTimeout(st1); st1 = setTimeout(() => { query = search.value.trim(); draw(); }, 250); });
  const seg = h('div', { class: 'seg', role: 'group', 'aria-label': 'View' });
  const drawSeg = () => seg.replaceChildren(...[['guide', 'Guide'], ['list', 'Now']].map(([v, l]) => h('button', { type: 'button', class: mode === v ? 'on' : '', 'aria-pressed': String(mode === v), onclick: () => { mode = v; store('live-view', v); drawSeg(); draw(); } }, l)));
  drawSeg();

  setView(h('div', { class: 'page livepage' }, h('div', { class: 'page-head' }, h('h1', {}, 'Live TV'), h('span', { class: 'count' }, `${st.channels} channels`), seg, groupSel, listsBtn, search), body));

  let cleanup = () => {};
  onLeave(() => cleanup());
  function filters() { return { group: group && group !== '★' && !group.startsWith('list:') ? group : '', favorites: group === '★', list: group.startsWith('list:') ? group.slice(5) : '', search: query }; }
  function draw() { cleanup(); cleanup = (mode === 'guide' ? renderGuide : renderList)(body, filters()) || (() => {}); }
  draw();
}

function filterQuery(f) {
  const q = new URLSearchParams();
  if (f.group) q.set('group', f.group);
  if (f.list) q.set('list', f.list);
  if (f.favorites) q.set('favorites', 'true');
  if (f.search) q.set('search', f.search);
  return q;
}

function emptyChannels(f) {
  return h('div', { class: 'empty' }, h('h2', {}, f.favorites ? 'No favorites yet' : 'No channels'), h('p', {}, f.favorites ? 'Tap the ☆ next to a channel to pin it here.' : f.search ? `Nothing matched “${f.search}”.` : 'This group is empty.'));
}

// ---------------------------------------------------------------- guide (timeline)
function renderGuide(container, f) {
  const from = Math.floor(nowTs() / 1800) * 1800;
  const width = GUIDE_HOURS * 60 * GUIDE_PX;
  const px = (ts) => ((ts - from) / 60) * GUIDE_PX;

  const ticks = Array.from({ length: GUIDE_HOURS * 2 }, (_, i) => h('div', { class: 'gtick' + (i % 2 ? ' half' : ''), style: `left:${i * 30 * GUIDE_PX}px` }, i % 2 ? '' : fmtClock(from + i * 1800)));
  const head = h('div', { class: 'ghead' }, h('div', { class: 'gcorner' }, 'Channel'), h('div', { class: 'gruler', style: `width:${width}px` }, ticks));
  const rows = h('div', { class: 'grows' });
  const nowLine = h('div', { class: 'gnow', title: 'Now' });
  const sentinel = h('div', { class: 'sentinel' });
  const content = h('div', { class: 'gcontent', style: `width:${CHAN_W + width}px` }, head, rows, sentinel, nowLine);
  const scroller = h('div', { class: 'guide' }, content);
  container.replaceChildren(scroller);

  const placeNow = () => { nowLine.style.left = `${CHAN_W + px(nowTs())}px`; };
  placeNow();
  scroller.scrollLeft = Math.max(0, px(nowTs()) - 140);
  const timer = setInterval(placeNow, 30000);

  const planned = new Set();   // "channel:start" of what is already scheduled or recording
  fetch('/api/recordings').then((r) => (r.ok ? r.json() : { recordings: [] })).then((d) => d.recordings.filter((r) => r.status === 'scheduled' || r.status === 'recording').forEach((r) => planned.add(`${r.channelId}:${r.startTs}`))).catch(() => {});
  function block(ch, p) {
    const start = Math.max(p.start, from), stop = Math.min(p.stop, from + GUIDE_HOURS * 3600);
    const now = nowTs();
    const cls = 'gprog' + (p.start <= now && p.stop > now ? ' now' : p.stop <= now ? ' past' : '') + (p.start < from ? ' cont' : '');
    const el = h('a', { class: cls, href: `#/live/${ch.id}`, style: `left:${px(start)}px;width:${Math.max(3, px(stop) - px(start) - 3)}px`,
      title: `${p.title}${p.subtitle ? ' · ' + p.subtitle : ''}\n${fmtClock(p.start)} – ${fmtClock(p.stop)}${p.category ? '  ·  ' + p.category : ''}${p.description ? '\n\n' + p.description : ''}` },
      h('span', { class: 'gt' }, p.title), h('span', { class: 'gs' }, `${fmtClock(p.start)} – ${fmtClock(p.stop)}`));
    if (p.stop > now) {
      const mark = () => el.classList.toggle('planned', planned.has(`${ch.id}:${p.start}`));
      el.append(h('button', { class: 'gcrec', type: 'button', title: 'Record this programme', 'aria-label': `Record ${p.title}`, onclick: async (e) => {
        e.preventDefault(); e.stopPropagation();
        if (await startRecording(ch.id, { programmeStart: p.start })) { planned.add(`${ch.id}:${p.start}`); mark(); }
      } }, '●'));
      mark();
    }
    return el;
  }
  function row(ch) {
    const line = h('div', { class: 'gline', style: `width:${width}px` },
      ch.programs.length ? ch.programs.map((p) => block(ch, p)) : h('a', { class: 'gprog empty', href: `#/live/${ch.id}`, style: `left:0;width:${width - 3}px` }, h('span', { class: 'gt' }, 'No guide data'), h('span', { class: 'gs' }, 'Tap to watch live')));
    return h('div', { class: 'grow' },
      h('a', { class: 'gchan', href: `#/live/${ch.id}`, title: ch.name }, channelLogo(ch), h('span', { class: 'gname' }, h('b', {}, ch.number != null ? ch.number : ''), h('span', {}, ch.name)), favButton(ch)), line);
  }

  let offset = 0, total = Infinity, loading = false, gen = 0;
  async function more() {
    if (loading || offset >= total) return;
    loading = true; const my = gen;
    try {
      const q = filterQuery(f); q.set('from', from); q.set('hours', GUIDE_HOURS); q.set('limit', 40); q.set('offset', offset);
      const d = await api(`/api/live/guide?${q}`);
      if (my !== gen) return;
      total = d.total; offset += d.channels.length;
      rows.append(...d.channels.map(row));
      if (total === 0) container.replaceChildren(emptyChannels(f));
    } catch (e) { toast(e.message, true); total = offset; }
    finally { loading = false; }
  }
  const io = new IntersectionObserver((es) => { if (es.some((e) => e.isIntersecting)) more(); }, { root: scroller, rootMargin: '400px' });
  io.observe(sentinel);
  more();
  return () => { gen++; clearInterval(timer); io.disconnect(); };
}

// ---------------------------------------------------------------- list ("what's on now")
function renderList(container, f) {
  const grid = h('div', { class: 'lgrid' });
  const sentinel = h('div', { class: 'sentinel' });
  container.replaceChildren(grid, sentinel);

  function card(ch) {
    const pct = Math.round(progressOf(ch.now) * 100);
    return h('a', { class: 'lcard', href: `#/live/${ch.id}` }, channelLogo(ch, 'big'),
      h('div', { class: 'linfo' },
        h('div', { class: 'lname' }, ch.number != null ? h('b', {}, ch.number) : null, h('span', {}, ch.name)),
        ch.now
          ? [h('div', { class: 'lnow' }, ch.now.title), h('div', { class: 'lbar' }, h('i', { style: `width:${pct}%` })),
            h('div', { class: 'lmeta' }, `${fmtClock(ch.now.start)} – ${fmtClock(ch.now.stop)} · ${fmtMinsLeft(ch.now.stop)}`)]
          : h('div', { class: 'lnow dim' }, 'No guide data'),
        ch.next ? h('div', { class: 'lnext' }, `Next · ${ch.next.title}`) : null),
      favButton(ch));
  }

  let offset = 0, total = Infinity, loading = false, gen = 0;
  async function more() {
    if (loading || offset >= total) return;
    loading = true; const my = gen;
    try {
      const q = filterQuery(f); q.set('limit', 48); q.set('offset', offset);
      const d = await api(`/api/live/channels?${q}`);
      if (my !== gen) return;
      total = d.total; offset += d.items.length;
      grid.append(...d.items.map(card));
      if (total === 0) container.replaceChildren(emptyChannels(f));
    } catch (e) { toast(e.message, true); total = offset; }
    finally { loading = false; }
  }
  const io = new IntersectionObserver((es) => { if (es.some((e) => e.isIntersecting)) more(); }, { rootMargin: '600px' });
  io.observe(sentinel);
  more();
  return () => { gen++; io.disconnect(); };
}

// ---------------------------------------------------------------- the live player
async function showLivePlayer(id) {
  const token = playerToken;
  const group = store('live-group') || '';
  const orderQuery = () => { const q = new URLSearchParams({ limit: '500' }); if (group === '★') q.set('favorites', 'true'); else if (group.startsWith('list:')) q.set('list', group.slice(5)); else if (group) q.set('group', group); return q; };
  let list = (await api(`/api/live/channels?${orderQuery()}`).catch(() => ({ items: [] }))).items;
  let idx = list.findIndex((c) => c.id === id);
  if (idx < 0) { list = (await api('/api/live/channels?limit=500')).items; idx = list.findIndex((c) => c.id === id); }
  if (idx < 0) throw new Error('Channel not found');
  if (token !== playerToken) return;

  document.body.classList.add('watching');
  const video = h('video', { controls: true, autoplay: true, playsinline: true });
  const spinner = h('div', { class: 'buffering hidden' }, h('i'));
  const logoSlot = h('span', { class: 'live-logo' });
  const titleEl = h('div', { class: 'player-title' });
  const liveBadge = h('span', { class: 'badge-live' }, 'LIVE');
  const chip = h('button', { class: 'mode-chip', type: 'button', title: 'How this channel is being played' }, 'Tuning…');
  const reasonEl = h('div', { class: 'reason hidden' });
  const star = h('span', { class: 'live-star' });
  const chBtn = h('button', { class: 'round', type: 'button', title: 'Channels (C)', 'aria-label': 'Channels' }, icon('list'));
  const back = h('button', { class: 'round', type: 'button', 'aria-label': 'Back', onclick: () => leave() }, icon('back'));
  const banner = h('div', { class: 'banner hidden' });
  const numOverlay = h('div', { class: 'numoverlay hidden' });
  const goLive = h('button', { class: 'btn golive hidden', type: 'button' }, 'Go to live');
  const errorBox = h('div', { class: 'errorbox hidden' });
  const drawer = h('aside', { class: 'drawer hidden', 'aria-label': 'Channels' });
  const recBtn = h('button', { class: 'round recbtn', type: 'button', title: 'Record this programme', 'aria-label': 'Record', onclick: async () => {
    const r = await startRecording(cur().id, { mode: 'now' });
    if (r) recBtn.classList.add('on');
  } }, h('span', { class: 'recdot' }));
  const stage = h('div', { class: 'player' }, video, spinner,
    h('div', { class: 'player-top' }, back, logoSlot, titleEl, liveBadge, star, recBtn, chip, chBtn), reasonEl, banner, numOverlay, goLive, errorBox, drawer);
  setView(stage);

  const leave = () => { if (history.length > 1) history.back(); else location.hash = '#/live'; };
  const cur = () => list[idx];

  // ---- information about the channel on screen
  function renderInfo() {
    const ch = cur();
    document.title = `${ch.name} · ${APP.name}`;
    logoSlot.replaceChildren(channelLogo(ch));
    titleEl.replaceChildren(`${ch.number != null ? ch.number + ' · ' : ''}${ch.name}`, h('small', {}, ch.now ? `${ch.now.title} · ${fmtClock(ch.now.start)} – ${fmtClock(ch.now.stop)}` : 'No guide data'));
    star.replaceChildren(favButton(ch, () => renderDrawer()));
  }
  let bannerTimer;
  function showBanner() {
    const ch = cur();
    banner.replaceChildren(channelLogo(ch, 'big'), h('div', { class: 'bn' },
      h('div', { class: 'bn-name' }, `${ch.number != null ? ch.number + ' · ' : ''}${ch.name}`),
      ch.now ? [h('div', { class: 'bn-now' }, ch.now.title, ch.now.subtitle ? h('small', {}, ` · ${ch.now.subtitle}`) : null),
        h('div', { class: 'lbar' }, h('i', { style: `width:${Math.round(progressOf(ch.now) * 100)}%` })),
        h('div', { class: 'lmeta' }, `${fmtClock(ch.now.start)} – ${fmtClock(ch.now.stop)} · ${fmtMinsLeft(ch.now.stop)}`),
        ch.now.description ? h('div', { class: 'bn-desc' }, ch.now.description) : null]
        : h('div', { class: 'bn-now dim' }, 'No guide data'),
      ch.next ? h('div', { class: 'lnext' }, `Next · ${ch.next.title} · ${fmtClock(ch.next.start)}`) : null));
    banner.classList.remove('hidden');
    clearTimeout(bannerTimer); bannerTimer = setTimeout(() => banner.classList.add('hidden'), 6000);
  }
  function setMode(text, detail) { chip.textContent = text; reasonEl.textContent = detail || ''; }
  chip.addEventListener('click', () => reasonEl.classList.toggle('hidden'));

  // ---- the channel drawer (quick switch)
  function renderDrawer(filter) {
    const q = (filter || '').toLowerCase();
    const items = list.map((c, i) => ({ c, i })).filter(({ c }) => !q || c.name.toLowerCase().includes(q) || String(c.number) === q);
    drawer.replaceChildren(
      h('div', { class: 'dr-head' }, h('strong', {}, 'Channels'), h('button', { class: 'round small', type: 'button', 'aria-label': 'Close', onclick: () => drawer.classList.add('hidden') }, icon('close'))),
      h('input', { type: 'search', class: 'dlg-input', placeholder: 'Find a channel', 'aria-label': 'Find a channel', value: filter || '', oninput: (e) => renderDrawer(e.target.value) }),
      h('div', { class: 'dr-list' }, items.map(({ c, i }) => h('button', { type: 'button', class: 'dr-item' + (i === idx ? ' on' : ''), onclick: () => { drawer.classList.add('hidden'); switchTo(i); } },
        channelLogo(c), h('span', { class: 'dr-text' }, h('b', {}, `${c.number != null ? c.number + ' · ' : ''}${c.name}`), h('small', {}, c.now ? c.now.title : 'No guide data')),
        c.now ? h('span', { class: 'lbar sm' }, h('i', { style: `width:${Math.round(progressOf(c.now) * 100)}%` })) : null))));
    if (filter != null) drawer.querySelector('input').focus();
  }
  chBtn.addEventListener('click', () => { renderDrawer(); drawer.classList.toggle('hidden'); if (!drawer.classList.contains('hidden')) drawer.querySelector('.dr-item.on')?.scrollIntoView({ block: 'center' }); });

  // ---- tuning
  let ctl = newController();
  let seq = 0;
  let hls = null;
  function fail(msg) {
    spinner.classList.add('hidden');
    setMode('Unavailable', msg);
    errorBox.replaceChildren(h('strong', {}, msg), h('div', { class: 'row-actions' },
      h('button', { class: 'btn', type: 'button', onclick: () => tune(cur()) }, 'Try again'),
      h('button', { class: 'btn glass', type: 'button', onclick: () => { renderDrawer(); drawer.classList.remove('hidden'); } }, 'Other channels')));
    errorBox.classList.remove('hidden');
  }

  async function tune(ch, forceTranscode = false, attempt = 0) {
    const my = ++seq;
    ctl.stop(); ctl = newController(); hls = null;
    video.removeAttribute('src'); video.load();
    await sleep(60);
    errorBox.classList.add('hidden'); goLive.classList.add('hidden');
    spinner.classList.remove('hidden'); setMode('Tuning…', `Opening ${ch.name}`);
    let res;
    try {
      res = await fetch(`/api/live/channels/${ch.id}/start`, { method: 'POST', headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ video: CAPS.v, audio: CAPS.a, containers: CAPS.c, forceTranscode }) });
    } catch { if (my === seq) fail('Could not reach the server.'); return; }
    if (my !== seq) return;
    if (!res.ok) { reportIssue('live-start-failed', { mediaId: -ch.id, mode: null }, `http ${res.status}`); fail(res.status === 502 ? "This channel isn't available right now." : `Could not start the channel (${res.status}).`); return; }
    const s = await res.json();
    if (my !== seq) return;

    const base = `/api/hls/${s.sessionId}`;
    const stopSession = () => { try { navigator.sendBeacon(`${base}/stop`); } catch { /* ignore */ } };
    const ping = setInterval(() => fetch(`${base}/ping`, { method: 'POST' }).catch(() => {}), 15000);
    window.addEventListener('pagehide', stopSession);
    ctl.onStop(() => {
      clearInterval(ping); window.removeEventListener('pagehide', stopSession);
      if (hls) hls.destroy();
      fetch(`${base}/stop`, { method: 'POST', keepalive: true }).catch(() => {});
    });

    const retry = (why) => {
      if (my !== seq) return;
      reportIssue('live-playback-error', { mediaId: -ch.id, mode: s.mode }, why);
      if (!forceTranscode && s.mode === 'remux') { setMode('Tuning…', 'Switching to a compatible stream'); tune(ch, true, attempt); return; } // copied video didn't decode here
      if (attempt < 2) { setMode('Reconnecting…', 'The channel stalled'); tune(ch, forceTranscode, attempt + 1); return; }
      fail('This channel stopped working.');
    };
    const nativeHls = !!video.canPlayType('application/vnd.apple.mpegurl');
    if (nativeHls && (IS_SAFARI || !(window.Hls && Hls.isSupported()))) {
      const onErr = () => { if (video.currentSrc) retry('native HLS error'); };
      video.addEventListener('error', onErr); ctl.onStop(() => video.removeEventListener('error', onErr));
      video.src = s.playlistUrl;
    } else if (window.Hls && Hls.isSupported()) {
      let recoveredNet = false, recoveredMedia = false;
      hls = new Hls({ liveSyncDurationCount: 3, liveMaxLatencyDurationCount: 9, maxBufferLength: 20, backBufferLength: 60, manifestLoadingMaxRetry: 4, levelLoadingMaxRetry: 4, fragLoadingMaxRetry: 4 });
      hls.on(Hls.Events.ERROR, (_e, d) => {
        if (!d.fatal) return;
        if (d.type === Hls.ErrorTypes.NETWORK_ERROR && !recoveredNet) { recoveredNet = true; hls.startLoad(); return; }
        if (d.type === Hls.ErrorTypes.MEDIA_ERROR && !recoveredMedia) { recoveredMedia = true; hls.recoverMediaError(); return; }
        retry(`${d.type}/${d.details}`);
      });
      hls.loadSource(s.playlistUrl); hls.attachMedia(video);
    } else { fail('This browser cannot play live streams.'); return; }

    if (s.mode === 'remux') watchDecoding(video, ctl, () => retry("the video couldn't be decoded"));
    setMode(s.mode === 'remux' ? 'Live · Direct stream' : 'Live · Transcoding', s.reason);
    showBanner();
  }

  function switchTo(i) {
    if (i < 0 || i >= list.length) return;
    idx = i;
    history.replaceState(null, '', `#/live/${cur().id}`); // no hashchange: we're staying in the player
    renderInfo(); renderDrawer(); tune(cur());
  }
  const step = (d) => switchTo(((idx + d) % list.length + list.length) % list.length);

  // ---- live edge: offer a jump when playback has fallen behind (paused, buffering, or rewound)
  const liveEdge = () => (hls && hls.liveSyncPosition) || (video.seekable.length ? video.seekable.end(video.seekable.length - 1) : NaN);
  const edgeTimer = setInterval(() => {
    const e = liveEdge();
    const behind = Number.isFinite(e) ? e - video.currentTime : 0;
    goLive.classList.toggle('hidden', !(behind > 15));
    liveBadge.classList.toggle('behind', behind > 15);
  }, 2000);
  goLive.addEventListener('click', () => { const e = liveEdge(); if (Number.isFinite(e)) video.currentTime = e - 1; video.play().catch(() => {}); });

  // ---- idle UI, buffering spinner
  let idleTimer;
  const wake = () => { stage.classList.remove('idle'); clearTimeout(idleTimer); idleTimer = setTimeout(() => { if (!video.paused) stage.classList.add('idle'); }, 3500); };
  ['mousemove', 'touchstart', 'keydown'].forEach((ev) => stage.addEventListener(ev, wake, { passive: true }));
  video.addEventListener('play', () => { stage.classList.add('playing'); wake(); });
  video.addEventListener('pause', () => stage.classList.remove('playing', 'idle'));
  video.addEventListener('waiting', () => spinner.classList.remove('hidden'));
  ['playing', 'canplay'].forEach((ev) => video.addEventListener(ev, () => { spinner.classList.add('hidden'); errorBox.classList.add('hidden'); }));
  wake();

  // ---- keyboard: channel surfing like a TV remote
  let numBuf = '', numTimer;
  function commitNumber() {
    clearTimeout(numTimer);
    const n = parseInt(numBuf, 10); numBuf = ''; numOverlay.classList.add('hidden');
    const i = list.findIndex((c) => c.number === n);
    if (i >= 0) switchTo(i); else toast(`No channel ${n}`);
  }
  const onKey = (e) => {
    if (e.target.closest && e.target.closest('input, textarea, select')) { if (e.key === 'Escape') e.target.blur(); return; }
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if (/^[0-9]$/.test(e.key)) { numBuf = (numBuf + e.key).slice(0, 4); numOverlay.textContent = numBuf; numOverlay.classList.remove('hidden'); clearTimeout(numTimer); numTimer = setTimeout(commitNumber, 1400); wake(); return; }
    switch (e.key) {
      case 'Enter': if (numBuf) commitNumber(); else showBanner(); break;
      case 'PageUp': case ']': e.preventDefault(); step(1); break;
      case 'PageDown': case '[': e.preventDefault(); step(-1); break;
      case 'c': renderDrawer(); drawer.classList.toggle('hidden'); break;
      case 'i': showBanner(); break;
      case ' ': case 'k': e.preventDefault(); if (video.paused) video.play(); else video.pause(); break;
      case 'm': video.muted = !video.muted; break;
      case 'f': if (document.fullscreenElement) document.exitFullscreen(); else (stage.requestFullscreen || stage.webkitRequestFullscreen || (() => {})).call(stage); break;
      case 'ArrowUp': e.preventDefault(); video.volume = Math.min(1, video.volume + 0.1); break;
      case 'ArrowDown': e.preventDefault(); video.volume = Math.max(0, video.volume - 0.1); break;
      case 'Escape': if (!drawer.classList.contains('hidden')) drawer.classList.add('hidden'); else if (!document.fullscreenElement) leave(); break;
      default: return;
    }
    wake();
  };
  document.addEventListener('keydown', onKey);

  // keep "now/next" fresh as programmes change
  const refreshTimer = setInterval(async () => {
    try { const fresh = (await api(`/api/live/channels?${orderQuery()}`)).items; const keep = cur().id; list = fresh.length ? fresh : list; const i = list.findIndex((c) => c.id === keep); if (i >= 0) idx = i; renderInfo(); if (!drawer.classList.contains('hidden')) renderDrawer(); } catch { /* try again next time */ }
  }, 120000);

  activePlayback = {
    dispose() {
      clearInterval(edgeTimer); clearInterval(refreshTimer); clearTimeout(idleTimer); clearTimeout(bannerTimer); clearTimeout(numTimer);
      document.removeEventListener('keydown', onKey);
      seq++; try { video.pause(); } catch { /* ignore */ }
      ctl.stop(); // releases the provider connection (the server kills ffmpeg)
      video.removeAttribute('src'); video.load();
    },
  };

  renderInfo(); renderDrawer(); drawer.classList.add('hidden');
  await tune(cur());
  if (token !== playerToken) ctl.stop();
}

// ---------------------------------------------------------------- your channel lists ("Sports", "Kids"…)
function openListsDialog(initial, onChange) {
  let lists = initial.map((l) => ({ ...l }));
  let selected = lists[0] ? lists[0].id : null;
  const nameIn = h('input', { class: 'dlg-input', type: 'text', maxlength: '40', placeholder: 'New list name', 'aria-label': 'New list name' });
  const listBox = h('div', { class: 'dlg-list ll-lists' });
  const chanBox = h('div', { class: 'dlg-list ll-chans' });
  const search = h('input', { class: 'dlg-input', type: 'search', placeholder: 'Find a channel to add', 'aria-label': 'Find a channel' });
  const close = () => { document.removeEventListener('keydown', onKey); dlg.remove(); onChange(); };
  const onKey = (e) => { if (e.key === 'Escape') close(); };
  const call = async (method, url, body) => {
    const res = await fetch(url, { method, headers: body ? { 'content-type': 'application/json' } : undefined, body: body ? JSON.stringify(body) : undefined });
    const j = res.status === 204 ? {} : await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(j.error || 'That didn’t work');
    return j;
  };
  const reload = async () => { lists = (await api('/api/live/lists')).lists; if (!lists.some((l) => l.id === selected)) selected = lists[0] ? lists[0].id : null; drawLists(); drawChannels(); };

  function drawLists() {
    listBox.replaceChildren(...(lists.length ? lists.map((l) => h('div', { class: 'll-item' + (l.id === selected ? ' on' : '') },
      h('button', { type: 'button', class: 'll-name', onclick: () => { selected = l.id; drawLists(); drawChannels(); } }, l.name, h('small', {}, ` ${l.channels}`)),
      h('button', { type: 'button', class: 'll-x', title: 'Rename', onclick: async () => { const n = prompt('New name', l.name); if (n) { try { await call('PATCH', `/api/live/lists/${l.id}`, { name: n }); reload(); } catch (e) { toast(e.message, true); } } } }, '✎'),
      h('button', { type: 'button', class: 'll-x', title: 'Delete', onclick: async () => { if (confirm(`Delete the list “${l.name}”? The channels themselves stay.`)) { try { await call('DELETE', `/api/live/lists/${l.id}`); reload(); } catch (e) { toast(e.message, true); } } } }, '✕')))
      : [h('div', { class: 'dur' }, 'No lists yet. Name one above to start.')]));
  }
  let timer;
  async function drawChannels() {
    const l = lists.find((x) => x.id === selected);
    if (!l) { chanBox.replaceChildren(h('div', { class: 'dur' }, 'Pick or create a list.')); return; }
    const q = search.value.trim();
    const d = await api(`/api/live/channels?limit=200${q ? `&search=${encodeURIComponent(q)}` : ''}`).catch(() => ({ items: [] }));
    const inList = new Set(l.channelIds);
    // members first, then the rest
    const items = [...d.items].sort((a, b) => Number(inList.has(b.id)) - Number(inList.has(a.id)));
    chanBox.replaceChildren(...items.map((c) => h('label', { class: 'll-chan' },
      h('input', { type: 'checkbox', checked: inList.has(c.id) ? true : null, onchange: async (e) => {
        try { await call(e.target.checked ? 'PUT' : 'DELETE', `/api/live/lists/${l.id}/channels/${c.id}`); if (e.target.checked) l.channelIds.push(c.id); else l.channelIds = l.channelIds.filter((x) => x !== c.id); l.channels = l.channelIds.length; drawLists(); }
        catch (err) { toast(err.message, true); e.target.checked = !e.target.checked; }
      } }), h('span', {}, `${c.number != null ? c.number + ' · ' : ''}${c.name}`))));
  }
  search.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(drawChannels, 250); });
  const add = async () => { const n = nameIn.value.trim(); if (!n) return; try { const made = await call('POST', '/api/live/lists', { name: n }); nameIn.value = ''; selected = made.id; await reload(); } catch (e) { toast(e.message, true); } };
  nameIn.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); add(); } });

  const dlg = h('div', { class: 'dlg-backdrop', onclick: (e) => { if (e.target === dlg) close(); } },
    h('div', { class: 'dlg wide', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Channel lists' },
      h('h2', {}, 'Your channel lists'), h('p', { class: 'dur' }, 'Group channels your own way and filter the guide by a list.'),
      h('div', { class: 'll' }, h('div', { class: 'll-col' }, h('div', { class: 'inline' }, nameIn, h('button', { class: 'btn small', type: 'button', onclick: add }, 'Add')), listBox),
        h('div', { class: 'll-col' }, search, chanBox)),
      h('div', { class: 'dlg-actions' }, h('span'), h('button', { class: 'btn', type: 'button', onclick: close }, 'Done'))));
  document.body.append(dlg);
  document.addEventListener('keydown', onKey);
  drawLists(); drawChannels();
}
