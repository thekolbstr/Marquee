'use strict';

// My List, marking things watched, and the Recordings page. (The player and its extras are in player.js.)
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
