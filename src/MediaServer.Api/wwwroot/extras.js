'use strict';

// My List, marking things watched, Recordings, and the extra player features: skip intro/credits, seek previews, audio tracks, casting.
// Functions only: loaded before app.js, which provides h/api/icon/toast/pref/setView/... at call time.

// ================================================================ My List
async function inMyList(type, key) {
  try { return (await api(`/api/watchlist/status/${type}/${encodeURIComponent(key)}`)).inList; } catch { return false; }
}

// A toggle button for the details pages. `type` is "movie" (key = a file id) or "series" (key = the series key).
function myListButton(type, key, initial) {
  let on = !!initial;
  const b = h('button', { class: 'btn glass', type: 'button', 'aria-pressed': String(on) });
  const draw = () => { b.replaceChildren(icon(on ? 'check' : 'plus'), on ? 'In My List' : 'My List'); b.setAttribute('aria-pressed', String(on)); b.title = on ? 'Remove from My List' : 'Add to My List'; };
  draw();
  b.addEventListener('click', async () => {
    b.disabled = true;
    try {
      const res = await fetch(`/api/watchlist/${type}/${encodeURIComponent(key)}`, { method: on ? 'DELETE' : 'PUT' });
      if (!res.ok) throw new Error();
      on = !on; draw(); toast(on ? 'Added to My List' : 'Removed from My List');
    } catch { toast('Could not update My List', true); }
    finally { b.disabled = false; }
  });
  return b;
}

async function showMyList() {
  const d = await api('/api/watchlist');
  document.title = `My List · ${APP.name}`;
  const poster = posterShare(d.items) >= POSTER_MIN;
  setView(h('div', { class: 'page' }, h('div', { class: 'page-head' }, h('h1', {}, 'My List'), h('span', { class: 'count' }, d.items.length ? `${d.items.length} ${d.items.length === 1 ? 'title' : 'titles'}` : '')),
    d.items.length ? h('div', { class: 'grid' + (poster ? ' posters' : '') }, d.items.map((i) => itemTile(i, poster)))
      : h('div', { class: 'empty' }, h('h2', {}, 'Nothing here yet'), h('p', {}, 'Open a movie or show and choose “My List” to save it for later.'))));
}

// Mark a whole season (or the whole show) watched or unwatched.
function watchedMenu(seriesKey, getSeason, onDone) {
  const run = async (season, watched) => {
    const qs = new URLSearchParams({ watched: String(watched) }); if (season != null) qs.set('season', season);
    try {
      const res = await fetch(`/api/items/series/${encodeURIComponent(seriesKey)}/watched?${qs}`, { method: 'POST' });
      if (!res.ok) throw new Error();
      const j = await res.json(); toast(`${watched ? 'Marked watched' : 'Marked unwatched'}: ${j.changed} episode${j.changed === 1 ? '' : 's'}`); onDone();
    } catch { toast('Could not update', true); }
  };
  const menu = h('div', { class: 'qmenu inlinemenu hidden', role: 'menu' });
  const btn = h('button', { class: 'btn glass small', type: 'button', 'aria-haspopup': 'true', onclick: () => {
    const s = getSeason(); const label = s.season === 0 ? 'Specials' : s.season == null ? 'these episodes' : `Season ${s.season}`;
    menu.replaceChildren(...[[`Mark ${label} watched`, s.season, true], [`Mark ${label} unwatched`, s.season, false], ['Mark the whole show watched', null, true], ['Mark the whole show unwatched', null, false]]
      .map(([t, sn, w]) => h('button', { type: 'button', role: 'menuitem', onclick: () => { menu.classList.add('hidden'); run(sn, w); } }, h('span', {}, t))));
    menu.classList.toggle('hidden');
  } }, icon('check'), 'Watched…');
  document.addEventListener('click', (e) => { if (!menu.contains(e.target) && !btn.contains(e.target)) menu.classList.add('hidden'); });
  return h('div', { class: 'watchedmenu' }, btn, menu);
}

// ================================================================ Recordings
const fmtRecWhen = (ts) => new Date(ts * 1000).toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
const REC_STATUS = { scheduled: 'Scheduled', recording: 'Recording', done: 'Ready', failed: 'Failed', cancelled: 'Cancelled' };

async function showRecordings() {
  document.title = `Recordings · ${APP.name}`;
  const list = h('div', { class: 'rec-list' });
  setView(h('div', { class: 'page' }, h('div', { class: 'page-head' }, h('h1', {}, 'Recordings')), list));
  let timer;
  async function load() {
    let d; try { d = await api('/api/recordings'); } catch { return; }
    if (!list.isConnected) { clearInterval(timer); return; }
    const call = async (method, url, ok) => { const res = await fetch(url, { method }); if (!res.ok && res.status !== 204) { toast((await res.json().catch(() => ({}))).error || 'That didn’t work', true); return; } toast(ok); load(); };
    list.replaceChildren(...(d.recordings.length ? d.recordings.map((r) => h('div', { class: `rec ${r.status}` },
      h('div', { class: 'rec-main' }, h('strong', {}, r.title), h('small', {}, `${r.channelName} · ${fmtRecWhen(r.startTs)}${r.ownerName && d.recordings.some((x) => x.ownerName !== r.ownerName) ? ` · ${r.ownerName}` : ''}`),
        r.description ? h('p', { class: 'dur' }, r.description) : null, r.error ? h('p', { class: 'field-err' }, r.error) : null),
      h('span', { class: `badge-s rec-badge ${r.status}` }, r.status === 'recording' ? '● Recording' : REC_STATUS[r.status] || r.status),
      h('div', { class: 'folder-actions' },
        r.status === 'done' && r.mediaId ? h('a', { class: 'btn small', href: `#/watch/${r.mediaId}` }, icon('play'), 'Play') : r.status === 'done' ? h('span', { class: 'dur' }, 'Adding to library…') : null,
        r.status === 'recording' ? h('button', { class: 'btn glass small', type: 'button', onclick: () => call('POST', `/api/recordings/${r.id}/stop`, 'Stopped. What was recorded is kept.') }, 'Stop') : null,
        r.status === 'scheduled' ? h('button', { class: 'btn glass small', type: 'button', onclick: () => call('POST', `/api/recordings/${r.id}/stop`, 'Cancelled') }, 'Cancel') : null,
        r.status !== 'scheduled' && r.status !== 'recording' || r.status === 'done' ? h('button', { class: 'btn glass small red', type: 'button', onclick: () => { if (confirm(`Delete “${r.title}”${r.status === 'done' ? ' and its file' : ''}?`)) call('DELETE', `/api/recordings/${r.id}`, 'Deleted'); } }, 'Delete') : null)))
      : [h('div', { class: 'empty' }, h('h2', {}, 'No recordings yet'), h('p', {}, 'In Live TV, hover a programme in the guide and press ● to record it, or press Record while watching a channel.'), h('a', { class: 'btn', href: '#/live' }, 'Open Live TV'))]));
    if (!d.recordings.some((r) => r.status === 'recording' || r.status === 'scheduled' || (r.status === 'done' && !r.mediaId))) { /* nothing in flight: no need to keep polling fast */ }
  }
  await load();
  timer = setInterval(load, 8000);
  onLeave(() => clearInterval(timer));
}

// Schedule a recording; `body` is one of {mode:'now'}, {programmeStart}, {start, stop, title}.
async function startRecording(channelId, body) {
  const res = await fetch('/api/recordings', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ channelId, ...body }) });
  const j = await res.json().catch(() => ({}));
  if (!res.ok) { toast(j.error || 'Could not schedule that', true); return null; }
  toast(body.mode === 'now' ? `Recording “${j.title}”` : `Scheduled: “${j.title}” at ${fmtRecWhen(j.startTs)}`);
  return j;
}

// ================================================================ skip intro / credits
async function setupSkip(video, stage, f, wake) {
  let info; try { info = await api(`/api/media/${f.mediaId}/chapters`); } catch { return; }
  if (!info.markers || !info.markers.length) return;
  const btn = h('button', { class: 'btn skip-btn hidden', type: 'button' });
  stage.append(btn);
  let active = null;
  btn.addEventListener('click', () => {
    if (!active) return;
    const end = active.end;
    if (active.kind === 'credits' && f.nextMediaId && end >= (f.durationSeconds || Infinity) - 10) location.hash = `#/watch/${f.nextMediaId}`;
    else video.currentTime = Math.min(end, (f.durationSeconds || end) - 1);
    btn.classList.add('hidden'); active = null; wake();
  });
  video.addEventListener('timeupdate', () => {
    const t = video.currentTime;
    const m = info.markers.find((x) => t >= x.start - 1 && t < x.end - 2);
    if (m === active) return;
    active = m || null;
    if (!m) { btn.classList.add('hidden'); return; }
    btn.replaceChildren(icon('next'), m.kind === 'intro' ? 'Skip intro' : 'Skip credits');
    btn.classList.remove('hidden');
  });
}

// ================================================================ seek-bar previews
function trickplayPreview(f) {
  const tp = h('div', { class: 'tp hidden' });
  const label = h('span', { class: 'tt' });
  const root = h('div', { class: 'tpwrap' }, tp, label);
  let info = null;
  (async () => {
    for (let i = 0; i < 40; i++) {
      let res; try { res = await fetch(`/api/media/${f.mediaId}/trickplay`); } catch { return; }
      if (res.status === 200) { info = await res.json(); return; }
      if (res.status !== 202) return;               // 404: this title won't get previews
      await sleep(15000);
      if (!root.isConnected && i > 0) return;
    }
  })();
  return {
    el: root,
    show(t, text) {
      label.textContent = text;
      if (!info) { tp.classList.add('hidden'); return; }
      const idx = Math.min(info.count - 1, Math.max(0, Math.floor(t / info.interval)));
      const per = info.columns * info.rows, sheet = Math.floor(idx / per), n = idx % per;
      tp.style.cssText = `width:${info.width}px;height:${info.height}px;background-image:url(/api/media/${f.mediaId}/trickplay/${sheet}.jpg);`
        + `background-size:${info.columns * info.width}px ${info.rows * info.height}px;background-position:-${(n % info.columns) * info.width}px -${Math.floor(n / info.columns) * info.height}px`;
      tp.classList.remove('hidden');
    },
  };
}

// ================================================================ audio tracks
const audioLabel = (a, i) => {
  const lang = a.language ? (LANG_NAMES[langKey(a.language)] || a.language) : null;
  const ch = a.channels ? (a.channels === 6 ? '5.1' : a.channels === 8 ? '7.1' : a.channels === 2 ? 'Stereo' : a.channels === 1 ? 'Mono' : `${a.channels}ch`) : null;
  return [lang || `Track ${i + 1}`, a.title && a.title.length < 40 && !(lang && a.title.toLowerCase().includes(lang.toLowerCase())) ? a.title : null, ch, a.codec ? a.codec.toUpperCase() : null].filter(Boolean).join(' · ');
};
const LANG_NAMES = { en: 'English', es: 'Spanish', fr: 'French', de: 'German', it: 'Italian', pt: 'Portuguese', ja: 'Japanese', ko: 'Korean', zh: 'Chinese', ru: 'Russian', nl: 'Dutch', sv: 'Swedish', pl: 'Polish', tr: 'Turkish', ar: 'Arabic', hi: 'Hindi' };

// A button + menu to pick the audio track. `choose(index)` restarts playback with that track.
function setupAudio(stage, f, ui, wake, current, choose) {
  const tracks = f.audio || [];
  if (tracks.length < 2) return;
  const btn = h('button', { class: 'cbtn', type: 'button', title: 'Audio', 'aria-label': 'Audio track', 'aria-haspopup': 'true' }, icon('audio'));
  const menu = h('div', { class: 'qmenu cc-menu audio-menu hidden', role: 'menu' });
  btn.addEventListener('click', () => {
    const cur = current();
    menu.replaceChildren(...tracks.map((a, i) => h('button', { type: 'button', role: 'menuitemradio', class: a.index === cur ? 'on' : '', 'aria-checked': String(a.index === cur),
      onclick: () => { menu.classList.add('hidden'); if (a.index !== cur) choose(a.index); } }, h('span', {}, audioLabel(a, i)), h('small', {}, a.index === cur ? '✓' : ''))));
    menu.classList.toggle('hidden'); wake();
  });
  stage.addEventListener('click', (e) => { if (!menu.contains(e.target) && !btn.contains(e.target)) menu.classList.add('hidden'); });
  ui.slot.after(btn);
  stage.append(menu);
}

// ================================================================ AirPlay and Chromecast
function setupAirPlay(video, ui) {
  if (!window.WebKitPlaybackTargetAvailabilityEvent || !video.webkitShowPlaybackTargetPicker) return;
  const btn = h('button', { class: 'cbtn hidden', type: 'button', title: 'AirPlay', 'aria-label': 'AirPlay', onclick: () => video.webkitShowPlaybackTargetPicker() }, icon('cast'));
  video.addEventListener('webkitplaybacktargetavailabilitychanged', (e) => btn.classList.toggle('hidden', e.availability !== 'available'));
  ui.slot.after(btn);
}

let castLoading = null;
function loadCastSdk() {
  if (window.cast && window.cast.framework) return Promise.resolve();
  if (castLoading) return castLoading;
  castLoading = new Promise((resolve, reject) => {
    window.__onGCastApiAvailable = (ok) => (ok ? resolve() : reject(new Error('Cast is not available in this browser')));
    const s = document.createElement('script'); s.src = 'https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1'; s.onerror = () => reject(new Error('Could not load Google Cast'));
    document.head.append(s);
  });
  return castLoading;
}

// Chromecast: the TV fetches the stream itself, so we hand it an HLS stream it can play (H.264 + AAC) at an address it can reach.
// The Google Cast library is only downloaded when you press the button.
function setupCast(video, f, ui, getStart) {
  const secure = window.isSecureContext;
  if (!secure || !/Chrome|Edg|CriOS/.test(navigator.userAgent)) return;
  const btn = h('button', { class: 'cbtn', type: 'button', title: 'Cast to a TV', 'aria-label': 'Cast', onclick: async () => {
    if (/^(localhost|127\.|\[::1\])/.test(location.hostname)) { toast('Casting needs the server’s network address, not localhost', true); return; }
    try {
      await loadCastSdk();
      const ctx = cast.framework.CastContext.getInstance();
      ctx.setOptions({ receiverApplicationId: chrome.cast.media.DEFAULT_MEDIA_RECEIVER_APP_ID, autoJoinPolicy: chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED });
      await ctx.requestSession();
      const res = await fetch('/api/hls/start', { method: 'POST', headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ mediaId: f.mediaId, video: 'h264', audio: 'aac,mp3', containers: 'mp4', startSeconds: Math.floor(getStart()) }) });
      if (!res.ok) throw new Error((await res.json().catch(() => ({}))).detail || 'The server could not prepare a stream for the TV');
      const s = await res.json();
      const media = new chrome.cast.media.MediaInfo(`${location.origin}${s.playlistUrl}`, 'application/x-mpegURL');
      media.streamType = chrome.cast.media.StreamType.BUFFERED;
      media.metadata = new chrome.cast.media.GenericMediaMetadata(); media.metadata.title = f.title;
      const req = new chrome.cast.media.LoadRequest(media); req.currentTime = getStart();
      video.pause();
      await ctx.getCurrentSession().loadMedia(req);
      toast(`Playing on ${ctx.getCurrentSession().getCastDevice().friendlyName}`);
      ctx.addEventListener(cast.framework.CastContextEventType.SESSION_STATE_CHANGED, (e) => {
        if (e.sessionState === cast.framework.SessionState.SESSION_ENDED) { try { navigator.sendBeacon(`/api/hls/${s.sessionId}/stop`); } catch { /* ignore */ } }
      });
    } catch (e) { if (e && e.message && !/cancel/i.test(e.message)) toast(e.message, true); }
  } }, icon('cast'));
  ui.slot.after(btn);
}
