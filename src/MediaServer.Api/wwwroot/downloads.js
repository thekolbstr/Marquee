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
/** "Today", "Tomorrow" or "Fri 14 Nov" for the date badge on a coming-soon card. */
const arrWhen = (iso) => {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const days = Math.round((new Date(d.getFullYear(), d.getMonth(), d.getDate()) - new Date(new Date().getFullYear(), new Date().getMonth(), new Date().getDate())) / 86400000);
  return days <= 0 ? 'Today' : days === 1 ? 'Tomorrow' : arrDate(iso);
};

/** Cover art, or a coloured placeholder with the title's initial while there is none (or it fails to load). */
function arrPoster(title, src, cls = '') {
  const hue = hashHue(title || '?');
  const box = h('div', { class: `arr-art ${cls}`, style: `background:linear-gradient(145deg,hsl(${hue} 42% 30%),hsl(${(hue + 45) % 360} 48% 14%))` },
    h('span', { class: 'arr-initial', 'aria-hidden': 'true' }, (title || '?').trim().charAt(0).toUpperCase()));
  if (src) {
    const img = h('img', { alt: '', loading: 'lazy', decoding: 'async', src });
    img.addEventListener('error', () => img.remove());
    box.append(img);
  }
  return box;
}

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
  const summary = h('div', { class: 'arr-pills' });
  const results = h('div', { class: 'arr-results' });
  const queue = h('section', { class: 'arr-section' }), upcoming = h('section', { class: 'arr-section' });
  const input = h('input', { type: 'search', class: 'arr-search', placeholder: 'Find a movie or show to add…', 'aria-label': 'Search to add', enterkeyhint: 'search', value: params.get('q') || '' });
  const sources = [info.radarr ? 'Radarr' : null, info.sonarr ? 'Sonarr' : null].filter(Boolean).join(' & ');
  setView(h('div', { class: 'page arr-page' },
    h('div', { class: 'page-head' }, h('h1', {}, 'Downloads'), summary),
    h('form', { class: 'arr-find', onsubmit: (e) => { e.preventDefault(); find(); } }, icon('search'), input, h('button', { class: 'btn', type: 'submit' }, 'Search')),
    h('p', { class: 'dur arr-via' }, `Adds go to ${sources}, which searches for a good copy straight away.`),
    results, queue, upcoming));

  const counts = { queue: 0, upcoming: 0 };
  const paintSummary = () => summary.replaceChildren(
    h('span', { class: 'arr-pill' }, h('b', {}, counts.queue), ' downloading'), h('span', { class: 'arr-pill' }, h('b', {}, counts.upcoming), ' coming up'));

  async function find() {
    const term = input.value.trim();
    if (!term) { results.replaceChildren(); return; }
    results.replaceChildren(h('p', { class: 'dur' }, 'Searching…'));
    try {
      const { items } = await api(`/api/arr/lookup?term=${encodeURIComponent(term)}`);
      results.replaceChildren(h('h2', { class: 'arr-h' }, `Results for “${term}”`),
        items.length ? h('div', { class: 'arr-grid' }, items.map(lookupCard)) : h('p', { class: 'dur' }, 'Nothing found. Try a different spelling.'));
    } catch (e) { results.replaceChildren(h('p', { class: 'error' }, e.message)); }
  }
  function lookupCard(it) {
    const id = it.source === 'Radarr' ? it.tmdbId : it.tvdbId;
    const action = h('div', { class: 'arr-act' });
    const showAdded = (text) => action.replaceChildren(h('span', { class: 'badge-s ok' }, icon('check'), text));
    if (it.inLibrary) showAdded('Already added');
    else if (!id) action.append(h('span', { class: 'dur' }, 'Can’t be added'));
    else action.append(h('button', { class: 'btn small', type: 'button', onclick: () => addDialog(it, id, () => { showAdded('Added'); loadQueue(); }) }, icon('plus'), 'Add'));
    return h('article', { class: 'arr-card' },
      h('div', { class: 'arr-frame' }, arrPoster(it.title, it.poster),
        h('span', { class: 'arr-kind' }, it.source === 'Radarr' ? 'Movie' : 'Show'),
        it.rating ? h('span', { class: 'arr-rating' }, '★ ', it.rating) : null),
      h('div', { class: 'arr-meta' }, h('div', { class: 't', title: it.title }, it.title), h('div', { class: 's' }, [it.year, it.status && it.source === 'Sonarr' ? it.status : null].filter(Boolean).join(' · ') || ' '), action));
  }

  async function loadQueue() {
    try {
      const { items } = await api('/api/arr/queue');
      if (!queue.isConnected) return;
      counts.queue = items.length; paintSummary();
      queue.replaceChildren(h('h2', { class: 'arr-h' }, 'Downloading'), items.length
        ? h('div', { class: 'arr-queue' }, items.map((q) => h('article', { class: 'arr-dl' },
          arrPoster(q.title, q.poster, 'sm'),
          h('div', { class: 'arr-dl-body' },
            h('div', { class: 'row-split' }, h('div', { class: 'arr-dl-title' }, h('b', {}, q.title), q.detail ? h('span', { class: 'dur' }, ` · ${q.detail}`) : null),
              h('span', { class: q.problem ? 'badge-s bad' : 'badge-s' }, q.state)),
            h('div', { class: 'meter', role: 'progressbar', 'aria-label': q.title, 'aria-valuenow': Math.round(q.percent), 'aria-valuemin': 0, 'aria-valuemax': 100 }, h('i', { style: `width:${q.percent}%` })),
            h('div', { class: 'dur' }, `${Math.round(q.percent)}%${q.timeLeft && q.timeLeft !== '00:00:00' ? ` · ${q.timeLeft} left` : ''} · ${q.source}`),
            q.problem ? h('div', { class: 'arr-problem' }, q.problem) : null))))
        : h('p', { class: 'dur arr-empty' }, 'Nothing is downloading right now.'));
    } catch (e) { if (queue.isConnected) queue.replaceChildren(h('h2', { class: 'arr-h' }, 'Downloading'), h('p', { class: 'error' }, e.message)); }
  }
  async function loadUpcoming() {
    try {
      const { items } = await api('/api/arr/upcoming?days=21');
      counts.upcoming = items.length; paintSummary();
      upcoming.replaceChildren(h('h2', { class: 'arr-h' }, 'Coming up', h('small', {}, ' next 3 weeks')), items.length
        ? h('div', { class: 'arr-rail' }, items.map((u) => h('article', { class: 'arr-card rail' },
          h('div', { class: 'arr-frame' }, arrPoster(u.title, u.poster), h('span', { class: 'arr-date' }, arrWhen(u.date)), u.hasFile ? h('span', { class: 'arr-have' }, icon('check'), 'In library') : null),
          h('div', { class: 'arr-meta' }, h('div', { class: 't', title: u.title }, u.title), h('div', { class: 's' }, u.detail || ' '), h('div', { class: 's' }, u.source)))))
        : h('p', { class: 'dur arr-empty' }, 'Nothing is scheduled in the next three weeks.'));
    } catch (e) { upcoming.replaceChildren(h('h2', { class: 'arr-h' }, 'Coming up'), h('p', { class: 'error' }, e.message)); }
  }
  paintSummary();
  await Promise.all([loadQueue(), loadUpcoming()]);
  if (input.value) find();
  const t = setInterval(() => { if (queue.isConnected) loadQueue(); }, 8000);
  onLeave(() => clearInterval(t));
}

function addDialog(it, id, onDone) {
  const kind = it.source.toLowerCase();
  const profile = h('select', { class: 'sort', 'aria-label': 'Quality profile' }), folder = h('select', { class: 'sort', 'aria-label': 'Folder' });
  const status = h('p', { class: 'dur' }, 'Loading options…');
  const go = h('button', { class: 'btn', type: 'button', disabled: true }, icon('plus'), 'Add and search');
  const close = () => { document.removeEventListener('keydown', onKey); dlg.remove(); };
  const onKey = (e) => { if (e.key === 'Escape') close(); };
  const dlg = h('div', { class: 'dlg-backdrop', onclick: (e) => { if (e.target === dlg) close(); } },
    h('div', { class: 'dlg arr-add', role: 'dialog', 'aria-modal': 'true', 'aria-label': `Add ${it.title}` },
      h('div', { class: 'arr-add-top' }, arrPoster(it.title, it.poster, 'md'),
        h('div', {}, h('h2', {}, it.title), h('p', { class: 'dur' }, [it.year, it.source === 'Radarr' ? 'Movie' : 'Show', it.rating ? `★ ${it.rating}` : null].filter(Boolean).join(' · ')),
          h('p', { class: 'arr-over' }, it.overview || 'No description.'))),
      field('Quality', null, profile), field('Save in', null, folder), status,
      h('div', { class: 'dlg-actions' }, go, h('button', { class: 'btn glass small', type: 'button', onclick: close }, 'Cancel'))));
  document.body.append(dlg);
  document.addEventListener('keydown', onKey);
  api(`/api/arr/options/${kind}`).then((o) => {
    profile.replaceChildren(...o.profiles.map((p) => h('option', { value: p.id }, p.name)));
    folder.replaceChildren(...o.folders.map((f) => h('option', { value: f.name }, f.name)));
    const ok = o.profiles.length && o.folders.length;
    status.textContent = ok ? `${it.source} will look for it straight away. It appears in your library once it’s downloaded and imported.` : `${it.source} has no ${o.profiles.length ? 'root folder' : 'quality profile'} yet. Set one up there first.`;
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
