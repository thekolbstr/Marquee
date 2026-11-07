'use strict';

// Radarr / Sonarr: the Downloads page (what's downloading, what's coming, add a title) and the extra tools on the settings page.
// Functions only: loaded before app.js, which provides h/api/onLeave/setView/toast/... at call time.

let arrInfo = { radarr: false, sonarr: false };

/** Shows the Downloads link when at least one of the two is set up. Only administrators can use it. */
async function loadArrStatus() {
  arrInfo = { radarr: false, sonarr: false };
  if (isAdmin()) { try { arrInfo = await api('/api/arr/status'); } catch { /* not available */ } }
  document.querySelector('#nav a[data-route=downloads]')?.classList.toggle('hidden', !(arrInfo.radarr || arrInfo.sonarr));
  return arrInfo;
}

const arrDate = (iso) => {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' });
};

async function showDownloads(params) {
  if (!isAdmin()) { setView(h('div', { class: 'page' }, h('p', { class: 'error' }, 'Only administrators can manage downloads.'))); return; }
  const info = await loadArrStatus();
  document.title = 'Downloads · ' + APP.name;
  if (!info.radarr && !info.sonarr) {
    setView(h('div', { class: 'page' }, h('div', { class: 'page-head' }, h('h1', {}, 'Downloads')),
      h('p', { class: 'hint' }, 'Connect Radarr and/or Sonarr to see what’s downloading, what’s coming soon, and to add movies and shows from here.'),
      h('a', { class: 'btn', href: '#/settings/downloads' }, 'Set up')));
    return;
  }
  const queue = h('div', { class: 'panel' }), upcoming = h('div', { class: 'panel' }), results = h('div', { class: 'dlg-list find' });
  const input = h('input', { type: 'search', class: 'dlg-input', placeholder: 'Search to add a movie or show', 'aria-label': 'Search to add', value: params.get('q') || '' });
  setView(h('div', { class: 'page' }, h('div', { class: 'page-head' }, h('h1', {}, 'Downloads')),
    h('div', { class: 'panel' }, h('h3', {}, 'Add to your library'), h('form', { class: 'inline', onsubmit: (e) => { e.preventDefault(); find(); } }, input, h('button', { class: 'btn', type: 'submit' }, 'Search')), results),
    queue, upcoming));

  async function find() {
    const term = input.value.trim();
    if (!term) { results.replaceChildren(); return; }
    results.replaceChildren(h('div', { class: 'dur' }, 'Searching…'));
    try {
      const { items } = await api(`/api/arr/lookup?term=${encodeURIComponent(term)}`);
      results.replaceChildren(...(items.length ? items.map(lookupRow) : [h('div', { class: 'dur' }, 'Nothing found. Try a different spelling.')]));
    } catch (e) { results.replaceChildren(h('div', { class: 'error' }, e.message)); }
  }
  function lookupRow(it) {
    const id = it.source === 'Radarr' ? it.tmdbId : it.tvdbId;
    const btn = h('button', { class: 'btn glass small', type: 'button', disabled: it.inLibrary || !id ? true : null, onclick: () => addDialog(it, id, () => { btn.replaceWith(h('span', { class: 'badge-s ok' }, 'Added')); loadQueue(); }) },
      it.inLibrary ? 'Already added' : 'Add');
    return h('div', { class: 'hit static' }, h('span', { class: 'hit-text' }, h('strong', {}, it.title, it.year ? ` (${it.year})` : '', ' ', h('span', { class: 'badge-s' }, it.source === 'Radarr' ? 'Movie' : 'Show')),
      h('small', {}, it.overview || 'No description')), btn);
  }

  async function loadQueue() {
    try {
      const { items } = await api('/api/arr/queue');
      if (!queue.isConnected) return;
      queue.replaceChildren(h('h3', {}, `Downloading (${items.length})`), ...(items.length ? items.map((q) => h('div', { class: 'stream col' },
        h('div', { class: 'row-split' }, h('div', {}, h('b', {}, q.title), q.detail ? h('span', { class: 'dur' }, ` · ${q.detail}`) : null),
          h('span', { class: q.problem ? 'badge-s bad' : 'badge-s' }, q.state)),
        h('div', { class: 'meter', role: 'progressbar', 'aria-valuenow': Math.round(q.percent), 'aria-valuemin': 0, 'aria-valuemax': 100 }, h('i', { style: `width:${q.percent}%` })),
        h('div', { class: 'dur' }, `${Math.round(q.percent)}%${q.timeLeft && q.timeLeft !== '00:00:00' ? ` · ${q.timeLeft} left` : ''} · ${q.source}${q.problem ? ` · ${q.problem}` : ''}`)))
        : [h('p', { class: 'dur' }, 'Nothing is downloading.')]));
    } catch (e) { if (queue.isConnected) queue.replaceChildren(h('h3', {}, 'Downloading'), h('p', { class: 'error' }, e.message)); }
  }
  async function loadUpcoming() {
    try {
      const { items } = await api('/api/arr/upcoming?days=21');
      upcoming.replaceChildren(h('h3', {}, 'Coming up · next 3 weeks'), ...(items.length ? items.map((u) => h('div', { class: 'stream' },
        h('div', {}, h('b', {}, u.title), u.detail ? h('span', { class: 'dur' }, ` · ${u.detail}`) : null, h('div', { class: 'dur' }, `${arrDate(u.date)} · ${u.source}`)),
        u.hasFile ? h('span', { class: 'badge-s ok' }, 'In library') : null)) : [h('p', { class: 'dur' }, 'Nothing is scheduled in the next three weeks.')]));
    } catch (e) { upcoming.replaceChildren(h('h3', {}, 'Coming up'), h('p', { class: 'error' }, e.message)); }
  }
  await Promise.all([loadQueue(), loadUpcoming()]);
  if (input.value) find();
  const t = setInterval(() => { if (queue.isConnected) loadQueue(); }, 8000);
  onLeave(() => clearInterval(t));
}

function addDialog(it, id, onDone) {
  const kind = it.source.toLowerCase();
  const profile = h('select', { class: 'sort', 'aria-label': 'Quality profile' }), folder = h('select', { class: 'sort', 'aria-label': 'Folder' });
  const status = h('p', { class: 'dur' }, 'Loading options…');
  const go = h('button', { class: 'btn', type: 'button', disabled: true }, 'Add and search');
  const close = () => { document.removeEventListener('keydown', onKey); dlg.remove(); };
  const onKey = (e) => { if (e.key === 'Escape') close(); };
  const dlg = h('div', { class: 'dlg-backdrop', onclick: (e) => { if (e.target === dlg) close(); } },
    h('div', { class: 'dlg', role: 'dialog', 'aria-modal': 'true', 'aria-label': `Add ${it.title}` },
      h('h2', {}, `Add ${it.title}${it.year ? ` (${it.year})` : ''}`),
      h('p', { class: 'dur' }, `${it.source} will look for it straight away, and it appears in your library once it’s downloaded and imported.`),
      field('Quality', null, profile), field('Save in', null, folder), status,
      h('div', { class: 'dlg-actions' }, go, h('button', { class: 'btn glass small', type: 'button', onclick: close }, 'Cancel'))));
  document.body.append(dlg);
  document.addEventListener('keydown', onKey);
  api(`/api/arr/options/${kind}`).then((o) => {
    profile.replaceChildren(...o.profiles.map((p) => h('option', { value: p.id }, p.name)));
    folder.replaceChildren(...o.folders.map((f) => h('option', { value: f.name }, f.name)));
    const ok = o.profiles.length && o.folders.length;
    status.textContent = ok ? '' : `${it.source} has no ${o.profiles.length ? 'root folder' : 'quality profile'} yet. Set one up there first.`;
    go.disabled = !ok;
  }).catch((e) => { status.textContent = e.message; status.className = 'error'; });
  go.addEventListener('click', async () => {
    go.disabled = true; status.className = 'dur'; status.textContent = 'Adding…';
    try {
      await api('/api/arr/add', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ source: it.source, id, qualityProfileId: +profile.value, rootFolder: folder.value }) });
      toast(`Added ${it.title}`); close(); onDone();
    } catch (e) { status.textContent = e.message; status.className = 'error'; go.disabled = false; }
  });
}

/** Extra controls under the Downloads settings: connection tests and the address that tells this server about new imports. */
function downloadsTools() {
  const testBtn = (name) => {
    const out = h('span', { class: 'save-status' });
    return [h('button', { class: 'btn glass', type: 'button', onclick: async () => {
      out.className = 'save-status'; out.textContent = 'Testing…';
      try { const r = await (await fetch(`/api/settings/test/${name.toLowerCase()}`, { method: 'POST' })).json(); out.textContent = r.message; out.className = 'save-status ' + (r.ok ? 'good' : 'bad'); } catch { out.textContent = 'Could not reach the server'; }
    } }, `Test ${name}`), out];
  };
  const hook = h('div', { class: 'panel' }, h('h3', {}, 'Add new files straight away'),
    h('p', { class: 'hint' }, 'In Radarr and Sonarr, open Settings → Connect → Add → Webhook, paste this address, and tick “On Import” and “On Upgrade” (and “On Rename” if you like). New files then appear here within seconds instead of waiting for the next scan.'));
  const show = (path) => {
    const url = location.origin + path;
    hook.querySelector('.hook')?.remove();
    hook.append(h('div', { class: 'hook inline' }, h('input', { class: 'dlg-input', readonly: true, value: url, 'aria-label': 'Webhook address', onfocus: (e) => e.target.select() }),
      h('button', { class: 'btn glass small', type: 'button', onclick: async () => { try { await navigator.clipboard.writeText(url); toast('Copied'); } catch { toast('Select the address and copy it', true); } } }, 'Copy'),
      h('button', { class: 'btn glass small', type: 'button', onclick: async () => {
        if (!confirm('Make a new address? The old one stops working, so update it in Radarr and Sonarr.')) return;
        try { show((await api('/api/arr/webhook/reset', { method: 'POST' })).webhookPath); toast('New address made'); } catch (e) { toast(e.message, true); }
      } }, 'Make a new one')));
  };
  api('/api/arr/status').then((s) => show(s.webhookPath)).catch(() => hook.append(h('p', { class: 'hint' }, 'The address could not be loaded.')));
  return [...testBtn('Radarr'), ...testBtn('Sonarr'), hook];
}
