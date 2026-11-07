'use strict';

// Settings: per-device preferences (kept in this browser) and server settings (kept on the server, editable by whoever can open this page).
// Functions only: loaded before app.js, which provides h/api/store/toast/icon/setView at call time.

// ================================================================ per-device preferences
const ACCENTS = {
  indigo: ['#7c8cff', '#b07cff', 'Indigo'], blue: ['#4aa3ff', '#7c8cff', 'Blue'], teal: ['#2dd4bf', '#38bdf8', 'Teal'],
  green: ['#3ddc97', '#a3e635', 'Green'], rose: ['#ff6b9a', '#ff9f6b', 'Rose'], amber: ['#ffb454', '#ff7a59', 'Amber'],
};
const PREF_DEFAULTS = {
  accent: 'indigo', theme: 'midnight', text: 'normal', density: 'comfortable', view: 'auto', motion: 'auto',
  homeMyList: 'on', homeGreeting: 'on', homeHero: 'on', heroRotate: 'on', homeQuick: 'on', homeContinue: 'on', homeLive: 'on', homeMovies: 'on', homeShows: 'on',
  audioLang: '', quality: 'original', autoplay: 'on', resume: 'resume', autonext: 'on', skip: '10', speed: '1', rememberVolume: 'on', volume: '1', muted: 'off',
  cc: 'auto', ccLang: 'en', ccSize: '100', ccBg: 'shadow',
};
const pref = (k) => { const v = store('pref:' + k); return v == null || v === '' ? PREF_DEFAULTS[k] : v; };
const setPref = (k, v) => { store('pref:' + k, v); applyPrefs(); };

const CC_SIZES = { '80': 'Small', '100': 'Medium', '130': 'Large', '170': 'Extra large' };
const CC_BGS = { none: 'None', shadow: 'Outline', box: 'Dark box' };

function applyPrefs() {
  const a = ACCENTS[pref('accent')] || ACCENTS.indigo;
  const root = document.documentElement;
  root.style.setProperty('--accent', a[0]); root.style.setProperty('--accent-2', a[1]);
  document.body.classList.toggle('dense', pref('density') === 'compact');
  const t = pref('theme');
  root.dataset.theme = t === 'black' ? 'black' : t === 'light' ? 'light' : t === 'auto' ? (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'midnight') : 'midnight';
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', root.dataset.theme === 'light' ? '#f4f5f8' : root.dataset.theme === 'black' ? '#000000' : '#0a0c10');
  root.classList.toggle('text-large', pref('text') === 'large');
  document.body.classList.toggle('reduce-motion', pref('motion') === 'reduce');
  let st = document.getElementById('cue-style');
  if (!st) { st = document.createElement('style'); st.id = 'cue-style'; document.head.append(st); }
  const bg = pref('ccBg');
  st.textContent = `video::cue { font-size: ${CC_SIZES[pref('ccSize')] ? pref('ccSize') : 100}%; color: #fff; font-family: system-ui, sans-serif; `
    + (bg === 'box' ? 'background: rgba(0,0,0,.78); ' : bg === 'shadow' ? 'background: transparent; text-shadow: 0 0 4px #000, 0 0 4px #000, 1px 1px 3px #000; ' : 'background: transparent; ')
    + '}';
}
const skipSeconds = () => Math.min(60, Math.max(5, parseInt(pref('skip'), 10) || 10));

// ================================================================ the page
const SECTIONS = [
  ['appearance', 'Appearance', 'device'], ['home', 'Home page', 'device'], ['playback', 'Playback', 'device'], ['captions', 'Captions', 'device'], ['shortcuts', 'Keyboard shortcuts', 'device'],
  ['general', 'General', 'server'], ['users', 'People', 'server'], ['activity', 'Watch activity', 'server'], ['library', 'Library', 'server'], ['metadata', 'Posters & details', 'server'], ['streaming', 'Streaming & playback', 'server'], ['livetv', 'Live TV & recording', 'server'], ['downloads', 'Downloads', 'server'],
  ['network', 'Network & security', 'server'], ['maintenance', 'Maintenance', 'server'], ['logs', 'Logs', 'server'], ['system', 'System & about', 'server'],
  ['account', 'Account & devices', 'account'],
];

async function showSettings(section) {
  // Ordinary people only see their own preferences and account; the server's configuration is for administrators.
  const visible = SECTIONS.filter(([, , kind]) => kind !== 'server' || isAdmin());
  const current = visible.some((s) => s[0] === section) ? section : 'appearance';
  const body = h('div', { class: 'set-body' });
  const nav = h('nav', { class: 'set-nav', 'aria-label': 'Settings sections' }, visible.map(([id, label, kind], i) => [
    i === 0 ? h('div', { class: 'set-sep' }, 'This device') : kind === 'server' && visible[i - 1][2] === 'device' ? h('div', { class: 'set-sep' }, 'Server')
      : kind === 'account' ? h('div', { class: 'set-sep' }, 'You') : null,
    h('a', { href: `#/settings/${id}`, class: id === current ? 'on' : '' }, label),
  ]));
  setView(h('div', { class: 'page' }, h('div', { class: 'page-head' }, h('h1', {}, 'Settings')), h('div', { class: 'settings' }, nav, body)));
  document.title = 'Settings · ' + APP.name;
  if (current === 'appearance') appearancePanel(body);
  else if (current === 'playback') playbackPanel(body);
  else if (current === 'home') homePanel(body);
  else if (current === 'captions') captionsPanel(body);
  else if (current === 'shortcuts') shortcutsPanel(body);
  else if (current === 'account') await accountPanel(body);
  else if (current === 'users') await usersPanel(body);
  else if (current === 'activity') await activityPanel(body);
  else if (current === 'maintenance') maintenancePanel(body);
  else if (current === 'logs') await logsPanel(body);
  else if (current === 'system') await systemPanel(body);
  else await serverPanel(body, current);
}

// ---- small form pieces
const field = (label, help, control) => h('div', { class: 'field' }, h('div', { class: 'field-text' }, h('label', {}, label), help ? h('p', {}, help) : null), h('div', { class: 'field-ctl' }, control));
function choice(key, options, after) {
  const el = h('div', { class: 'seg', role: 'group' });
  const paint = () => el.replaceChildren(...Object.entries(options).map(([v, l]) => h('button', { type: 'button', class: pref(key) === v ? 'on' : '', 'aria-pressed': String(pref(key) === v),
    onclick: () => { setPref(key, v); if (after) after(v); paint(); } }, l)));
  paint();
  return el;
}
const toggle = (checked, onchange) => h('label', { class: 'switch' }, h('input', { type: 'checkbox', checked: checked ? true : null, onchange: (e) => onchange(e.target.checked) }), h('i'));

function appearancePanel(body) {
  const sw = h('div', { class: 'swatches' });
  const paint = () => sw.replaceChildren(...Object.entries(ACCENTS).map(([id, [c1, c2, name]]) => h('button', {
    type: 'button', title: name, 'aria-label': name, 'aria-pressed': String(pref('accent') === id), class: pref('accent') === id ? 'on' : '',
    style: `background:linear-gradient(135deg,${c1},${c2})`, onclick: () => { setPref('accent', id); paint(); } })));
  paint();
  body.append(h('h2', {}, 'Appearance'),
    field('Accent colour', 'Buttons, progress bars and highlights.', sw),
    field('Theme', 'Black suits OLED screens and dark rooms. Auto follows your device.', choice('theme', { midnight: 'Midnight', black: 'Pure black', light: 'Light', auto: 'Auto' })),
    field('Text size', null, choice('text', { normal: 'Default', large: 'Large' })),
    field('Grid density', 'Compact fits more titles on screen.', choice('density', { comfortable: 'Comfortable', compact: 'Compact' })),
    field('Animations', 'Auto follows your device’s “reduce motion” setting.', choice('motion', { auto: 'Auto', reduce: 'Reduce' })),
    field('Artwork in lists', 'Posters when titles have them, or frames from the videos.', choice('view', { auto: 'Automatic', posters: 'Posters', thumbs: 'Frames' }, (v) => store('view', v))));
}

function homePanel(body) {
  const row = (key, label, help) => field(label, help, toggle(pref(key) === 'on', (on) => setPref(key, on ? 'on' : 'off')));
  body.append(h('h2', {}, 'Home page'), h('p', { class: 'hint' }, 'Choose what the home page shows on this device.'),
    row('homeGreeting', 'Greeting', 'A friendly “Good evening” with your name.'),
    row('homeHero', 'Featured banner', 'The big banner with recently added titles.'),
    row('heroRotate', 'Rotate the banner automatically', null),
    row('homeQuick', 'Quick buttons', 'Random movie, random episode, Live TV.'),
    row('homeContinue', 'Continue Watching', null),
    row('homeMyList', 'My List', 'Titles you saved to watch later.'),
    row('homeLive', 'Live TV row', 'Only appears when Live TV is set up.'),
    row('homeMovies', 'Recently added movies', null),
    row('homeShows', 'Recently added shows', null));
}

function playbackPanel(body) {
  const qOpts = { original: 'Original', '1080p': '1080p', '720p': '720p', '480p': '480p' };
  const qSeg = h('div', { class: 'seg', role: 'group' });
  const paintQ = () => qSeg.replaceChildren(...Object.entries(qOpts).map(([v, l]) => h('button', { type: 'button', class: (store('quality') || 'original') === v ? 'on' : '',
    onclick: () => { store('quality', v); paintQ(); } }, l)));
  paintQ();
  body.append(h('h2', {}, 'Playback'),
    APP.allowTranscoding
      ? field('Preferred quality', 'Original plays the file untouched whenever this device can. Lower options always re-encode, so use them on slow connections.', qSeg)
      : h('p', { class: 'hint' }, 'Transcoding is turned off on this server, so videos always play at their original quality.'),
    field('Preferred audio language', 'A code such as en, es, ja (or the language name). When a file has a track in that language, it is used instead of the file’s default. Leave empty to use the default.',
      h('input', { class: 'dlg-input short', type: 'text', value: pref('audioLang'), maxlength: '12', placeholder: 'default', 'aria-label': 'Preferred audio language', onchange: (e) => setPref('audioLang', e.target.value.trim().toLowerCase()) })),
    field('Start playing automatically', 'Off opens the player paused.', toggle(pref('autoplay') === 'on', (on) => setPref('autoplay', on ? 'on' : 'off'))),
    field('When a title was started before', null, choice('resume', { resume: 'Resume', start: 'Start over' })),
    field('Play the next episode automatically', 'When an episode ends.', toggle(pref('autonext') === 'on', (on) => setPref('autonext', on ? 'on' : 'off'))),
    field('Default speed', null, choice('speed', { '0.75': '0.75×', '1': '1×', '1.25': '1.25×', '1.5': '1.5×' })),
    field('Skip button', 'How far the ← → keys and skip buttons jump.', choice('skip', { 10: '10 s', 15: '15 s', 30: '30 s' })),
    field('Remember volume', 'Keep the volume and mute setting between videos.', toggle(pref('rememberVolume') === 'on', (on) => setPref('rememberVolume', on ? 'on' : 'off'))),
    h('p', { class: 'hint' }, 'The server decides per title whether to direct play, copy the video into a compatible stream, or transcode. The chip in the player shows which.'));
}

const SHORTCUTS = [
  ['Space  ·  K', 'Play / pause'], ['←  ·  J', 'Skip back'], ['→  ·  L', 'Skip forward'], ['↑  ↓', 'Volume'], ['M', 'Mute'], ['F  ·  double-click', 'Full screen'],
  ['C', 'Captions on / off'], ['I', 'Stats for nerds'], ['<  >', 'Slower / faster'], ['N', 'Next episode'], ['Esc', 'Close menu, leave full screen, go back'],
  ['[  ]  ·  type a number', 'Live TV: previous / next channel, or jump to a channel'],
];
function shortcutsPanel(body) {
  body.append(h('h2', {}, 'Keyboard shortcuts'), h('p', { class: 'hint' }, 'Available while a video or channel is playing.'),
    h('div', { class: 'shortcuts' }, SHORTCUTS.map(([k, d]) => h('div', { class: 'shortcut' }, h('span', { class: 'keys' }, k.split('  ·  ').map((g, i) => [i ? h('em', {}, 'or') : null, ...g.split(/\s{2}/).map((x) => h('kbd', {}, x.trim()))])), h('span', {}, d)))));
}

function captionsPanel(body) {
  const lang = h('input', { class: 'dlg-input short', type: 'text', value: pref('ccLang'), maxlength: '12', placeholder: 'en', 'aria-label': 'Preferred language',
    onchange: (e) => setPref('ccLang', e.target.value.trim().toLowerCase() || 'en') });
  const preview = h('div', { class: 'cc-preview' }, h('span', {}, 'This is how captions will look.'));
  const paintPreview = () => {
    const bg = pref('ccBg');
    preview.firstChild.style.cssText = `font-size:${1.1 * (parseInt(pref('ccSize'), 10) || 100) / 100}rem;color:#fff;padding:2px 8px;border-radius:4px;`
      + (bg === 'box' ? 'background:rgba(0,0,0,.78)' : bg === 'shadow' ? 'text-shadow:0 0 4px #000,0 0 4px #000,1px 1px 3px #000' : '');
  };
  paintPreview();
  body.append(h('h2', {}, 'Captions'),
    field('Default', 'Auto turns captions on only when a track in your language exists.', choice('cc', { off: 'Off', auto: 'Auto', always: 'Always' })),
    field('Preferred language', 'A code such as en, es, fr, de (or the language name).', lang),
    field('Size', null, choice('ccSize', CC_SIZES, paintPreview)),
    field('Background', null, choice('ccBg', CC_BGS, paintPreview)),
    preview,
    h('p', { class: 'hint' }, 'Text subtitles inside the file, and .srt / .vtt / .ass files placed next to it with the same name (for example Movie.en.srt), are supported. Picture-based tracks (PGS, DVD) cannot be shown in a browser.'));
}

// ---- server-side settings
async function serverPanel(body, groupId) {
  const data = await api('/api/settings');
  const group = data.groups.find((g) => g.id === groupId);
  if (!group) { body.append(h('p', {}, 'Unknown section.')); return; }
  const pending = new Map();          // key -> new value (null removes the override)
  const status = h('span', { class: 'save-status' });
  const save = h('button', { class: 'btn', type: 'button', disabled: true }, 'Save changes');
  const markDirty = () => { save.disabled = pending.size === 0; status.textContent = ''; };

  const rows = group.items.map((it) => {
    const badge = it.source === 'ui' ? h('span', { class: 'badge-s ui' }, 'Saved here') : it.source === 'deployment' ? h('span', { class: 'badge-s' }, 'From deployment') : null;
    let ctl;
    if (it.kind === 'bool') ctl = toggle(it.value === 'true', (on) => { pending.set(it.key, on ? 'true' : 'false'); markDirty(); });
    else if (it.kind === 'int') ctl = h('div', { class: 'inline unit' }, h('input', { class: 'dlg-input short', type: 'number', value: it.value, min: it.min, max: it.max, 'aria-label': it.label,
      oninput: (e) => { pending.set(it.key, e.target.value); markDirty(); } }), it.unit ? h('span', { class: 'unit-label' }, it.unit) : null);
    else if (it.kind === 'choice') ctl = h('select', { class: 'sort', 'aria-label': it.label, onchange: (e) => { pending.set(it.key, e.target.value); markDirty(); } },
      it.options.map((o) => h('option', { value: o.value, selected: o.value === it.value }, o.label)));
    else {
      const secret = it.kind === 'secret' || it.kind === 'secreturl';
      const input = h('input', { class: 'dlg-input', type: secret ? 'password' : 'text', autocomplete: 'off', spellcheck: 'false', 'aria-label': it.label, value: secret ? '' : it.value || '',
        placeholder: secret && it.isSet ? (it.preview ? `Saved: ${it.preview}` : '•••••••• saved') : it.placeholder || '',
        oninput: (e) => { if (e.target.value.trim() || !secret) pending.set(it.key, e.target.value); else pending.delete(it.key); markDirty(); } });
      const clear = secret && it.isSet && it.source === 'ui' ? h('button', { class: 'btn glass small', type: 'button', onclick: () => { pending.set(it.key, null); input.value = ''; input.placeholder = 'Will be removed'; markDirty(); } }, 'Remove') : null;
      ctl = h('div', { class: 'inline' }, input, clear);
    }
    const reset = it.source === 'ui' && it.kind !== 'secret' && it.kind !== 'secreturl'
      ? h('button', { class: 'link', type: 'button', onclick: () => { pending.set(it.key, null); doSave(); } }, it.default ? `Use the default (${it.kind === 'choice' ? (it.options.find((o) => o.value === it.default) || {}).label : it.default})` : 'Use the default') : null;
    return h('div', { class: 'field', 'data-key': it.key }, h('div', { class: 'field-text' }, h('label', {}, it.label, badge), h('p', {}, it.help), reset ? h('p', {}, reset) : null), h('div', { class: 'field-ctl' }, ctl, h('p', { class: 'field-err hidden' })));
  });

  async function doSave() {
    save.disabled = true; status.textContent = 'Saving…';
    body.querySelectorAll('.field-err').forEach((e) => e.classList.add('hidden'));
    const values = Object.fromEntries(pending);
    const res = await fetch('/api/settings', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ values }) });
    if (!res.ok) {
      let j = {}; try { j = await res.json(); } catch { /* ignore */ }
      status.textContent = '';
      const row = j.field && body.querySelector(`.field[data-key="${CSS.escape(j.field)}"] .field-err`);
      if (row) { row.textContent = j.error; row.classList.remove('hidden'); } else toast(j.error || 'Could not save', true);
      save.disabled = false;
      return;
    }
    pending.clear();
    toast('Saved');
    if (groupId === 'downloads') await loadArrStatus();
    await refreshApp();   // the server name and the transcoding switch change what the whole page shows
    await showSettings(groupId); // redraw with what the server now has
  }
  save.addEventListener('click', doSave);

  const tools = [];
  if (groupId === 'metadata') {
    const out = h('span', { class: 'save-status' });
    tools.push(h('button', { class: 'btn glass', type: 'button', onclick: async () => { out.textContent = 'Testing…'; try { const r = await (await fetch('/api/settings/test/tmdb', { method: 'POST' })).json(); out.textContent = r.message; out.className = 'save-status ' + (r.ok ? 'good' : 'bad'); } catch { out.textContent = 'Could not reach the server'; } } }, 'Test connection'), out);
  }
  if (groupId === 'downloads') tools.push(...downloadsTools());
  if (groupId === 'livetv') {
    const out = h('span', { class: 'save-status' });
    tools.push(h('button', { class: 'btn glass', type: 'button', onclick: async () => {
      out.className = 'save-status'; out.textContent = 'Fetching channels…';
      try {
        const res = await fetch('/api/live/refresh', { method: 'POST' });
        if (res.status === 503) { out.textContent = 'Save a playlist URL first.'; return; }
        const j = await res.json().catch(() => ({}));
        if (!res.ok) { out.textContent = j.detail || j.error || 'Could not load the playlist'; out.className = 'save-status bad'; return; }
        out.textContent = `Loaded ${j.channels ?? ''} channels${j.programs != null ? `, ${j.programs} programmes` : ''}.`; out.className = 'save-status good';
        const link = $('#nav a[data-route="live"]'); if (link) link.classList.remove('hidden');
      } catch { out.textContent = 'Could not reach the server'; }
    } }, 'Fetch channels now'), out);
  }
  if (groupId === 'metadata') {
    const out2 = h('span', { class: 'save-status' });
    tools.push(h('button', { class: 'btn glass', type: 'button', onclick: () => runTask('refresh-metadata', out2, 'Forget every match and fetch all titles again from TMDB? Manual matches are kept.') }, 'Refresh all metadata'), out2);
  }
  if (groupId === 'streaming' && !APP.allowTranscoding) tools.push(h('p', { class: 'hint' }, 'Transcoding is currently off: devices that can’t play a file directly (or with the video copied) will see an error.'));
  if (groupId === 'library') {
    const out = h('span', { class: 'save-status' });
    tools.push(h('button', { class: 'btn glass', type: 'button', onclick: () => { scanBtn.click(); out.textContent = 'Scan started'; } }, 'Scan now'), out);
  }
  const folders = groupId === 'library' ? h('div', { class: 'folders-block' }, h('h3', {}, 'Media folders')) : null;
  if (folders) { body.append(h('h2', {}, group.title), h('p', { class: 'hint' }, group.description), folders); await folderManager(folders, {}); }
  const extra = groupId === 'network' ? [await httpsCard()] : groupId === 'livetv' ? [recordingsNote()] : [];
  body.append(...(folders ? [h('h3', { class: 'sub' }, 'Scanning')] : [h('h2', {}, group.title), h('p', { class: 'hint' }, group.description)]), ...rows, h('div', { class: 'actions-row' }, save, status), ...(tools.length ? [h('div', { class: 'actions-row' }, ...tools)] : []), ...extra);
}

// ================================================================ media folders
// The folders themselves are chosen here, but only from what the server can see: it validates every path, and never changes your files.
async function folderManager(container, { wizard } = {}) {
  const list = h('div', { class: 'folder-list' });
  const addBtn = h('button', { class: 'btn' + (wizard ? '' : ' glass'), type: 'button' }, icon('plus'), 'Add a folder');
  const note = h('p', { class: 'hint hidden' });
  container.append(list, h('div', { class: 'actions-row flush' }, addBtn), note);

  async function load() {
    let d;
    try { d = await api('/api/library/folders'); } catch (e) { list.replaceChildren(h('p', { class: 'field-err' }, e.message)); return; }
    addBtn.classList.toggle('hidden', !d.editable);
    note.classList.toggle('hidden', d.editable);
    note.textContent = 'Turn sign-in on to add or change folders from the web (Auth__Enabled), or set them where the server is deployed.';
    list.replaceChildren(...(d.folders.length ? d.folders.map((f) => folderRow(f, d.editable)) : [h('p', { class: 'hint' }, 'No folders yet. Add the folder that holds your movies and shows.')]));
  }

  function folderRow(f, editable) {
    const ui = f.source === 'ui';
    const status = !f.exists ? h('span', { class: 'badge-s bad' }, 'Not found') : !f.enabled ? h('span', { class: 'badge-s' }, 'Hidden') : h('span', { class: 'badge-s ok' }, 'Available');
    return h('div', { class: 'folder' + (f.enabled ? '' : ' off') },
      h('div', { class: 'folder-info' }, h('strong', {}, f.name, ' ', status), h('code', {}, f.path),
        h('small', {}, `${f.files} file${f.files === 1 ? '' : 's'}${ui ? '' : ' · set where the server is deployed'}`)),
      ui && editable ? h('div', { class: 'folder-actions' },
        toggle(f.enabled, async (on) => { try { await call('PATCH', `/api/library/folders/${f.id}`, { enabled: on }); toast(on ? 'Folder shown' : 'Folder hidden'); } catch (e) { toast(e.message, true); } load(); }),
        h('button', { class: 'btn glass small', type: 'button', onclick: async () => {
          if (!confirm(`Remove “${f.name}” from the library?\n\nYour files are not touched. Only the server’s list of them is cleared, and watch history for them is kept if you add the folder again.`)) return;
          try { await call('DELETE', `/api/library/folders/${f.id}`); toast('Folder removed'); } catch (e) { toast(e.message, true); }
          load();
        } }, 'Remove')) : null);
  }

  async function call(method, url, body) {
    const res = await fetch(url, { method, headers: body ? { 'content-type': 'application/json' } : undefined, body: body ? JSON.stringify(body) : undefined });
    if (!res.ok) { let j = {}; try { j = await res.json(); } catch { /* none */ } throw new Error(j.error || `Server error (${res.status})`); }
    return res.status === 204 ? null : res.json();
  }

  addBtn.addEventListener('click', () => openFolderPicker({
    async onPick(path, name) { const added = await call('POST', '/api/library/folders', { path, name }); toast(`Added “${added.name}”. Scanning…`); watchScan(true); load(); },
  }));
  await load();
}

function openFolderPicker({ onPick }) {
  const crumbs = h('div', { class: 'crumbs' });
  const list = h('div', { class: 'dlg-list' });
  const pathInput = h('input', { class: 'dlg-input', type: 'text', spellcheck: 'false', autocomplete: 'off', 'aria-label': 'Folder path', placeholder: '/media/movies' });
  const nameInput = h('input', { class: 'dlg-input', type: 'text', autocomplete: 'off', 'aria-label': 'Display name (optional)', placeholder: 'Name (optional)', maxlength: '60' });
  const err = h('p', { class: 'auth-err hidden', role: 'alert' });
  const choose = h('button', { class: 'btn', type: 'button' }, 'Use this folder');
  let here = null;
  const close = () => { document.removeEventListener('keydown', onKey); dlg.remove(); };
  const onKey = (e) => { if (e.key === 'Escape') close(); };

  async function go(path) {
    err.classList.add('hidden');
    list.replaceChildren(h('div', { class: 'dur' }, 'Loading…'));
    try {
      const r = await api(`/api/fs/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`);
      here = r.path; pathInput.value = r.path;
      const parts = r.path.split('/').filter(Boolean);
      crumbs.replaceChildren(h('button', { type: 'button', class: 'crumb', onclick: () => go('/') }, '/'), ...parts.map((p, i) => h('button', { type: 'button', class: 'crumb', onclick: () => go('/' + parts.slice(0, i + 1).join('/')) }, p)));
      list.replaceChildren(...[
        r.parent ? h('button', { class: 'dir up', type: 'button', onclick: () => go(r.parent) }, icon('back'), 'Up one level') : null,
        ...(r.entries.length ? r.entries.map((d) => h('button', { class: 'dir', type: 'button', onclick: () => go(d.path) }, icon('folder'), d.name))
          : [h('div', { class: 'dur' }, 'No folders in here. You can still use this one.')]),
        r.truncated ? h('div', { class: 'dur' }, 'Only the first 500 folders are shown. Type a path above to go further.') : null].filter(Boolean));
    } catch (e) { list.replaceChildren(); err.textContent = e.message; err.classList.remove('hidden'); }
  }
  pathInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); go(pathInput.value.trim()); } });
  choose.addEventListener('click', async () => {
    if (!here) return;
    choose.disabled = true; err.classList.add('hidden');
    try { await onPick(here, nameInput.value.trim() || undefined); close(); }
    catch (e) { err.textContent = e.message; err.classList.remove('hidden'); choose.disabled = false; }
  });
  const dlg = h('div', { class: 'dlg-backdrop', onclick: (e) => { if (e.target === dlg) close(); } },
    h('div', { class: 'dlg', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Choose a folder' },
      h('h2', {}, 'Choose a folder'),
      h('p', { class: 'dur' }, 'These are the folders the server can see. In Docker, that means folders you’ve mounted into the container.'),
      pathInput, crumbs, list, err, nameInput,
      h('div', { class: 'dlg-actions' }, h('button', { class: 'btn glass small', type: 'button', onclick: close }, 'Cancel'), choose)));
  document.body.append(dlg);
  document.addEventListener('keydown', onKey);
  go(null);
}

// ================================================================ maintenance
async function runTask(task, out, confirmText) {
  if (confirmText && !confirm(confirmText)) return;
  out.className = 'save-status'; out.textContent = 'Working…';
  try {
    const res = await fetch(`/api/maintenance/${task}${task === 'clear-watch-history' ? '?confirm=true' : ''}`, { method: 'POST' });
    const j = await res.json().catch(() => ({}));
    if (res.status === 401) { authLost(); return; }
    out.textContent = j.message || j.error || (res.ok ? 'Done.' : 'That didn’t work.');
    out.className = 'save-status ' + (res.ok ? 'good' : 'bad');
    if (res.ok && task === 'refresh-metadata') watchScan(true);
  } catch { out.textContent = 'Could not reach the server.'; out.className = 'save-status bad'; }
}

// Intro and credits detection: start it, watch it work, forget what it found.
function skipTask() {
  const status = h('p', { class: 'save-status' });
  const bar = h('div', { class: 'meter hidden', role: 'progressbar', 'aria-label': 'Intro and credits search' }, h('i'));
  let timer = null;
  async function refresh() {
    let s;
    try { s = await api('/api/maintenance/skip-status'); } catch { return; }
    if (!status.isConnected) { clearInterval(timer); return; }
    bar.classList.toggle('hidden', !s.running || !s.total);
    if (s.running && s.total) bar.firstChild.style.width = `${Math.round((s.done / s.total) * 100)}%`;
    status.className = 'save-status' + (s.error ? ' bad' : '');
    status.textContent = s.error ? s.error
      : s.running ? `Listening… ${s.done} of ${s.total}${s.current ? ` · ${s.current}` : ''}`
      : s.titles ? `Skip points are known for ${s.titles} title${s.titles === 1 ? '' : 's'}${s.lastFinished ? ` · last search ${new Date(s.lastFinished).toLocaleString()}` : ''}.`
      : s.lastFinished ? 'Nothing found yet. Intros need at least two episodes of a season in the library.' : 'Not searched yet.';
  }
  timer = setInterval(refresh, 3000);
  onLeave(() => clearInterval(timer));
  refresh();
  const act = (task, label) => h('button', { class: 'btn glass', type: 'button', onclick: async () => {
    try { const r = await api(`/api/maintenance/${task}`, { method: 'POST' }); toast(r.message); refresh(); } catch (e) { toast(e.message, true); }
  } }, label);
  return h('div', { class: 'task' }, h('div', { class: 'task-text' }, h('strong', {}, 'Find intros and credits'),
    h('p', {}, 'Compares the openings and endings of episodes within a season (and the fade to black before a film’s credits) to offer Skip intro and Skip credits. It reads a few minutes of audio per episode, a title at a time. Chapters in the file are used first.'), bar, status),
    h('div', { class: 'task-btns' }, act('scan-skips', 'Search now'), act('clear-skips', 'Forget')));
}

function maintenancePanel(body) {
  const task = (title, text, button, id, confirmText, danger) => {
    const out = h('p', { class: 'save-status' });
    return h('div', { class: 'task' + (danger ? ' danger' : '') }, h('div', { class: 'task-text' }, h('strong', {}, title), h('p', {}, text), out),
      h('button', { class: 'btn glass' + (danger ? ' red' : ''), type: 'button', onclick: () => runTask(id, out, confirmText) }, button));
  };
  const scanOut = h('p', { class: 'save-status' });
  const liveOut = h('p', { class: 'save-status' });
  body.append(h('h2', {}, 'Maintenance'), h('p', { class: 'hint' }, 'Housekeeping for the person running the server. Nothing here touches your media files.'),
    h('div', { class: 'task' }, h('div', { class: 'task-text' }, h('strong', {}, 'Scan the library'), h('p', {}, 'Look for new, moved and removed files now.'), scanOut),
      h('button', { class: 'btn glass', type: 'button', onclick: () => { scanBtn.click(); scanOut.textContent = 'Scan started.'; scanOut.className = 'save-status good'; } }, 'Scan now')),
    skipTask(),
    h('div', { class: 'task' }, h('div', { class: 'task-text' }, h('strong', {}, 'Refresh Live TV'), h('p', {}, 'Re-import the channel list and programme guide.'), liveOut),
      h('button', { class: 'btn glass', type: 'button', onclick: async () => {
        liveOut.className = 'save-status'; liveOut.textContent = 'Fetching…';
        const res = await fetch('/api/live/refresh', { method: 'POST' }); const j = await res.json().catch(() => ({}));
        liveOut.textContent = res.ok ? `Loaded ${j.channels} channels, ${j.programs} programmes.` : res.status === 503 ? 'Live TV isn’t set up yet.' : (j.detail || 'Could not refresh.'); liveOut.className = 'save-status ' + (res.ok ? 'good' : 'bad');
      } }, 'Refresh')),
    task('Refresh all metadata', 'Forget every TMDB match and fetch the posters, descriptions and episode info again. Do this after changing the language. Manual matches are kept.', 'Refresh', 'refresh-metadata',
      'Forget every match and fetch everything again from TMDB? Manual matches are kept.'),
    task('Clear image cache', 'Delete downloaded posters, video-frame thumbnails, converted captions and channel logos. They are rebuilt as you browse.', 'Clear', 'clear-image-cache'),
    task('Clean up transcodes', 'Remove leftover temporary streaming folders that nothing is using (for example after a crash).', 'Clean up', 'clean-transcodes'),
    task('Compact the database', 'Reclaim unused space and refresh the database’s statistics. Safe at any time.', 'Compact', 'optimize-database'),
    h('div', { class: 'task' }, h('div', { class: 'task-text' }, h('strong', {}, 'Back up the database'), h('p', {}, 'Download a copy of everything the server remembers: watch history, matches, settings (including saved keys) and your password hash. Keep it private.')),
      h('a', { class: 'btn glass', href: '/api/maintenance/backup', download: '' }, 'Download backup')),
    h('h3', { class: 'sub' }, 'Danger zone'),
    task('Clear watch history', 'Mark everything as unwatched and forget where you stopped. This can’t be undone.', 'Clear history', 'clear-watch-history',
      'Clear ALL watch history and resume positions for every title? This can’t be undone.', true));
}

// ================================================================ logs
async function logsPanel(body) {
  let level = 'info', q = '', timer = null;
  const box = h('div', { class: 'logbox', role: 'log', tabindex: '0' });
  const sel = h('select', { class: 'sort', 'aria-label': 'Level', onchange: (e) => { level = e.target.value; load(); } },
    [['info', 'Everything'], ['warning', 'Warnings & errors'], ['error', 'Errors only']].map(([v, l]) => h('option', { value: v }, l)));
  const search = h('input', { class: 'dlg-input', type: 'search', placeholder: 'Filter…', 'aria-label': 'Filter log', oninput: (e) => { q = e.target.value.trim(); clearTimeout(search._t); search._t = setTimeout(load, 300); } });
  const auto = h('input', { type: 'checkbox', checked: true });
  async function load() {
    try {
      const j = await api(`/api/system/logs?limit=300&level=${level}&q=${encodeURIComponent(q)}`);
      const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
      box.replaceChildren(...(j.entries.length ? j.entries.map((e) => h('div', { class: `logline ${e.level}` },
        h('time', {}, new Date(e.time).toLocaleTimeString()), h('b', {}, e.level === 'info' ? 'INFO' : e.level === 'warning' ? 'WARN' : 'ERROR'), h('span', { class: 'cat' }, e.category), h('span', { class: 'msg' }, e.message)))
        : [h('div', { class: 'hint' }, 'Nothing logged yet.')]));
      if (atBottom || !box.dataset.shown) box.scrollTop = box.scrollHeight;
      box.dataset.shown = '1';
    } catch { /* the next tick tries again */ }
  }
  const copy = h('button', { class: 'btn glass small', type: 'button', onclick: async () => { try { await navigator.clipboard.writeText([...box.querySelectorAll('.logline')].map((l) => l.innerText.replace(/\n/g, ' ')).join('\n')); toast('Copied'); } catch { toast('Could not copy', true); } } }, 'Copy');
  body.append(h('h2', {}, 'Logs'), h('p', { class: 'hint' }, 'The most recent messages from the server, newest at the bottom. Useful when something doesn’t play. They are kept in memory only and start fresh when the server restarts.'),
    h('div', { class: 'log-tools' }, sel, search, h('label', { class: 'auth-check' }, auto, 'Live'), copy), box);
  await load();
  timer = setInterval(() => { if (auto.checked && document.contains(box)) load(); }, 3000);
  onLeave(() => clearInterval(timer));
}

// ================================================================ system & about
async function systemPanel(body) {
  const about = h('div', { class: 'panel' }, h('h3', {}, 'About'));
  const wrap = h('div');
  body.append(h('h2', {}, 'System & about'), wrap, about);
  await mountSystem(wrap);
  try {
    const a = await api('/api/system/about');
    const v = /^[0-9a-f]{40}$/.test(a.version) ? a.version.slice(0, 7) : a.version;
    about.append(h('dl', { class: 'kv' }, [
      ['Version', v], ['Runtime', a.runtime], ['System', a.os], ['Processors', a.cpus], ['Memory in use', fmtBytes(a.memoryBytes)], ['ffmpeg', a.ffmpeg || 'not found'], ['Database size', fmtBytes(a.databaseBytes)],
      ['This browser', `${BROWSER_NAME()} · ${CAPS.v || 'no'} video · ${CAPS.a || 'no'} audio`],
    ].map(([k, val]) => [h('dt', {}, k), h('dd', {}, String(val))])));
  } catch { /* about is optional */ }
}
const BROWSER_NAME = () => { const u = navigator.userAgent; return /Edg\//.test(u) ? 'Edge' : /Chrome\//.test(u) ? 'Chrome' : /Firefox\//.test(u) ? 'Firefox' : /Safari\//.test(u) ? 'Safari' : 'Browser'; };

// ================================================================ your account
function describeDevice(ua) {
  if (!ua) return 'Unknown device';
  const os = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? 'Android' : /Mac OS X/.test(ua) ? 'Mac' : /Windows/.test(ua) ? 'Windows' : /Linux/.test(ua) ? 'Linux' : '';
  const br = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) || /CriOS/.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
  return os ? `${br} on ${os}` : br;
}

async function accountPanel(body) {
  if (!AUTH.enabled) {
    body.append(h('h2', {}, 'Account & devices'), h('p', { class: 'hint' }, 'Sign-in is turned off for this server (Auth__Enabled=false), so there is no account to manage. Anyone who can reach this page can use the server and change its settings.'), resetBlock());
    return;
  }
  const cur = passwordField('Current password', { autocomplete: 'current-password' });
  const n1 = passwordField('New password (8+ characters)', { autocomplete: 'new-password' });
  const n2 = passwordField('Repeat the new password', { autocomplete: 'new-password' });
  const out = h('p', { class: 'save-status' });
  const btn = h('button', { class: 'btn', type: 'button' }, 'Change password');
  btn.addEventListener('click', async () => {
    out.className = 'save-status';
    if (n1.input.value !== n2.input.value) { out.textContent = 'The new passwords don’t match.'; out.className = 'save-status bad'; return; }
    btn.disabled = true;
    const r = await postJson('/api/auth/password', { current: cur.input.value, new: n1.input.value });
    btn.disabled = false;
    if (!r.ok) { out.textContent = r.json.error || 'Could not change the password.'; out.className = 'save-status bad'; return; }
    cur.input.value = n1.input.value = n2.input.value = '';
    out.textContent = 'Password changed. Other devices have been signed out.'; out.className = 'save-status good';
    loadDevices();
  });
  const nameIn = h('input', { class: 'dlg-input', type: 'text', maxlength: '40', autocomplete: 'nickname', 'aria-label': 'Display name', placeholder: AUTH.user || 'Your name', value: AUTH.displayName || '' });
  const nameOut = h('span', { class: 'save-status' });
  const nameBtn = h('button', { class: 'btn glass small', type: 'button', onclick: async () => {
    nameOut.className = 'save-status';
    const res = await fetch('/api/auth/profile', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ displayName: nameIn.value }) });
    const j = await res.json().catch(() => ({}));
    if (!res.ok) { nameOut.textContent = j.error || 'Could not save that.'; nameOut.className = 'save-status bad'; return; }
    AUTH.name = j.name; AUTH.displayName = j.displayName; paintUserChip();
    nameOut.textContent = 'Saved.'; nameOut.className = 'save-status good';
  } }, 'Save');
  const devices = h('div', { class: 'folder-list' });
  async function loadDevices() {
    try {
      const d = await api('/api/auth/sessions');
      devices.replaceChildren(...d.sessions.map((x) => h('div', { class: 'folder' },
        h('div', { class: 'folder-info' }, h('strong', {}, describeDevice(x.userAgent), ' ', x.current ? h('span', { class: 'badge-s ok' }, 'This device') : null),
          h('small', {}, `Signed in ${new Date(x.createdAt).toLocaleDateString()} · last active ${new Date(x.lastSeen).toLocaleString()}`)),
        x.current ? null : h('button', { class: 'btn glass small', type: 'button', onclick: async () => { await fetch(`/api/auth/sessions/${x.id}`, { method: 'DELETE' }); toast('Signed out'); loadDevices(); } }, 'Sign out'))));
    } catch { /* ignore */ }
  }
  body.append(h('h2', {}, 'Account & devices'),
    field('Signed in as', `Your sign-in name. It doesn’t change.`, h('div', { class: 'who' }, h('strong', {}, AUTH.user || ''), h('button', { class: 'btn glass small', type: 'button', onclick: signOut }, 'Sign out'))),
    field('Display name', 'What the interface calls you. Leave it empty to use your sign-in name.', h('div', { class: 'inline' }, nameIn, nameBtn, nameOut)),
    h('h3', { class: 'sub' }, 'Devices signed in'), devices,
    h('div', { class: 'actions-row flush' }, h('button', { class: 'btn glass small', type: 'button', onclick: async () => {
      const r = await (await fetch('/api/auth/sessions/revoke-others', { method: 'POST' })).json(); toast(r.revoked ? `Signed out ${r.revoked} other device${r.revoked === 1 ? '' : 's'}` : 'No other devices'); loadDevices();
    } }, 'Sign out everywhere else')),
    h('h3', { class: 'sub' }, 'Change password'),
    h('div', { class: 'pwform' }, cur.el, n1.el, n2.el, h('div', { class: 'actions-row flush' }, btn, out)),
    h('h3', { class: 'sub' }, 'Security'), await twoFactorBlock(),
    resetBlock());
  loadDevices();
}

function resetBlock() {
  return h('div', {}, h('h3', { class: 'sub' }, 'This device'),
    field('Reset preferences', 'Put appearance, home page, playback and caption choices on this device back to their defaults.', h('button', { class: 'btn glass small', type: 'button', onclick: () => {
      if (!confirm('Reset every preference on this device to its default?')) return;
      for (const k of Object.keys(PREF_DEFAULTS)) { try { localStorage.removeItem('pref:' + k); } catch { /* ignore */ } }
      ['quality', 'view', 'dismiss-tmdb'].forEach((k) => { try { localStorage.removeItem(k); } catch { /* ignore */ } });
      applyPrefs(); toast('Preferences reset'); showSettings('account');
    } }, 'Reset')));
}

// ================================================================ HTTPS, recordings note
async function httpsCard() {
  let s = null;
  try { s = await api('/api/system/https'); } catch { /* not available */ }
  if (!s) return h('div');
  return h('div', { class: 'panel', style: 'margin-top:22px' }, h('h3', {}, 'Encrypted connection (HTTPS)'),
    s.requestIsSecure ? h('p', { class: 'save-status good' }, 'You are connected over HTTPS.') : h('p', { class: 'save-status bad' }, 'You are connected over plain HTTP: passwords and sign-in cookies cross the network unencrypted.'),
    s.enabled
      ? h('div', {}, h('p', { class: 'hint' }, `The server also listens for HTTPS on port ${s.port}${s.selfSigned ? ' with a self-signed certificate. Browsers will ask you to trust it once; install the certificate on each device to make the warning go away.' : '.'}`),
        s.selfSigned ? h('a', { class: 'btn glass small', href: '/certificate.cer', download: '' }, 'Download certificate') : null)
      : h('p', { class: 'hint' }, 'Built-in HTTPS is off. Either terminate TLS in your reverse proxy (recommended), or set Https__Enabled=true on the server to add an HTTPS port with a self-signed certificate (Https__Hostnames adds names to it).'));
}

const recordingsNote = () => h('p', { class: 'hint', style: 'margin-top:18px' }, 'Recordings are saved in the recordings folder (Recordings__Directory, by default next to the database) and appear under Recordings. Make sure that folder has plenty of free space.');

// ================================================================ people
async function usersPanel(body) {
  const list = h('div', { class: 'folder-list' });
  const call = async (method, url, payload) => {
    const res = await fetch(url, { method, headers: payload ? { 'content-type': 'application/json' } : undefined, body: payload ? JSON.stringify(payload) : undefined });
    const j = res.status === 204 ? {} : await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(j.error || `Server error (${res.status})`);
    return j;
  };
  async function load() {
    const d = await api('/api/users');
    list.replaceChildren(...d.users.map((u) => {
      const me = u.username === AUTH.user;
      const role = h('select', { class: 'sort', 'aria-label': `Role of ${u.username}`, onchange: async (e) => { try { await call('PATCH', `/api/users/${u.id}`, { role: e.target.value }); toast('Role changed'); if (me) location.reload(); } catch (err) { toast(err.message, true); e.target.value = u.role; } } },
        h('option', { value: 'admin', selected: u.role === 'admin' }, 'Administrator'), h('option', { value: 'user', selected: u.role === 'user' }, 'Person'));
      return h('div', { class: 'folder' },
        h('div', { class: 'folder-info' }, h('strong', {}, u.displayName || u.username, ' ', me ? h('span', { class: 'badge-s ok' }, 'You') : null, u.twoFactor ? h('span', { class: 'badge-s' }, '2-step') : null),
          h('small', {}, `${u.username} · ${u.sessions} signed-in device${u.sessions === 1 ? '' : 's'} · joined ${new Date(u.createdAt).toLocaleDateString()}`)),
        h('div', { class: 'folder-actions' }, role,
          h('button', { class: 'btn glass small', type: 'button', onclick: () => {
            const pw = prompt(`New password for ${u.username} (8+ characters). They will be signed out everywhere.`);
            if (pw) call('POST', `/api/users/${u.id}/password`, { password: pw }).then(() => toast('Password changed')).catch((err) => toast(err.message, true));
          } }, 'Reset password'),
          u.twoFactor ? h('button', { class: 'btn glass small', type: 'button', onclick: () => { if (confirm(`Turn off two-step sign-in for ${u.username}?`)) call('POST', `/api/users/${u.id}/two-factor/reset`).then(() => { toast('Turned off'); load(); }).catch((err) => toast(err.message, true)); } }, 'Reset 2-step') : null,
          me ? null : h('button', { class: 'btn glass small red', type: 'button', onclick: () => {
            if (confirm(`Delete ${u.username}? Their watch history and My List are deleted too. This can’t be undone.`)) call('DELETE', `/api/users/${u.id}`).then(() => { toast('Removed'); load(); }).catch((err) => toast(err.message, true));
          } }, 'Delete')));
    }));
  }
  const nu = h('input', { class: 'dlg-input', type: 'text', autocomplete: 'off', 'aria-label': 'New username', placeholder: 'Username', maxlength: '32' });
  const nd = h('input', { class: 'dlg-input', type: 'text', autocomplete: 'off', 'aria-label': 'New display name', placeholder: 'Name shown (optional)', maxlength: '40' });
  const np = h('input', { class: 'dlg-input', type: 'password', autocomplete: 'new-password', 'aria-label': 'New password', placeholder: 'Password (8+ characters)' });
  const nr = h('select', { class: 'sort', 'aria-label': 'New role' }, h('option', { value: 'user' }, 'Person'), h('option', { value: 'admin' }, 'Administrator'));
  const out = h('span', { class: 'save-status' });
  const add = h('button', { class: 'btn', type: 'button', onclick: async () => {
    out.className = 'save-status';
    try { await call('POST', '/api/users', { username: nu.value.trim(), password: np.value, displayName: nd.value.trim() || null, role: nr.value }); nu.value = nd.value = np.value = ''; out.textContent = 'Added.'; out.className = 'save-status good'; load(); }
    catch (err) { out.textContent = err.message; out.className = 'save-status bad'; }
  } }, 'Add person');
  body.append(h('h2', {}, 'People'), h('p', { class: 'hint' }, 'Everyone signs in with their own account and has their own watch history, My List and recordings. Administrators can also change settings.'), list,
    h('h3', { class: 'sub' }, 'Add a person'), h('div', { class: 'pwform' }, nu, nd, np, nr, h('div', { class: 'actions-row flush' }, add, out)));
  await load();
}

// ================================================================ two-step sign-in (in Account)
const qrSvg = (text) => {
  const q = qrcode(0, 'M'); q.addData(text); q.make();
  return new DOMParser().parseFromString(q.createSvgTag({ cellSize: 5, margin: 2, scalable: true }), 'image/svg+xml').documentElement;   // parsed as XML, never as HTML
};

async function twoFactorBlock() {
  const box = h('div', { class: 'panel' });
  async function draw() {
    let st; try { st = await api('/api/auth/2fa'); } catch { return; }
    box.replaceChildren(h('h3', {}, 'Two-step sign-in'));
    if (st.enabled) {
      const out = h('p', { class: 'save-status' });
      box.append(h('p', { class: 'save-status good' }, `On · ${st.recoveryCodesLeft} recovery code${st.recoveryCodesLeft === 1 ? '' : 's'} left`),
        h('p', { class: 'hint' }, 'Signing in asks for a code from your authenticator app after your password.'),
        h('div', { class: 'actions-row flush' },
          h('button', { class: 'btn glass small', type: 'button', onclick: async () => {
            const pw = prompt('Your password, to make new recovery codes (the old ones stop working):'); if (!pw) return;
            const r = await postJson('/api/auth/2fa/recovery-codes', { password: pw });
            if (!r.ok) { out.textContent = r.json.error || 'Could not do that.'; out.className = 'save-status bad'; return; }
            showCodes(r.json.recoveryCodes); draw();
          } }, 'New recovery codes'),
          h('button', { class: 'btn glass small red', type: 'button', onclick: async () => {
            const pw = prompt('Your password:'); if (!pw) return; const code = prompt('A code from your authenticator app (or a recovery code):'); if (!code) return;
            const r = await postJson('/api/auth/2fa/disable', { password: pw, code });
            if (!r.ok && r.status !== 204) { out.textContent = r.json.error || 'Could not turn it off.'; out.className = 'save-status bad'; return; }
            toast('Two-step sign-in is off'); draw();
          } }, 'Turn off')), out);
      return;
    }
    box.append(h('p', { class: 'hint' }, 'Protect your account with a code from an authenticator app (Google Authenticator, 1Password, Authy…) in addition to your password.'),
      h('button', { class: 'btn glass small', type: 'button', onclick: async () => {
        const r = await postJson('/api/auth/2fa/begin', {});
        if (!r.ok) { toast(r.json.error || 'Could not start.', true); return; }
        const code = h('input', { class: 'dlg-input', type: 'text', inputmode: 'numeric', maxlength: '8', 'aria-label': 'Code from the app', placeholder: '6-digit code', autocomplete: 'one-time-code' });
        const out = h('span', { class: 'save-status' });
        const secret = r.json.secret.match(/.{1,4}/g).join(' ');
        box.replaceChildren(h('h3', {}, 'Set up two-step sign-in'),
          h('ol', { class: 'steps' }, h('li', {}, 'Scan this code with your authenticator app:', h('div', { class: 'qr' }, qrSvg(r.json.uri))),
            h('li', {}, 'or enter this key by hand: ', h('code', {}, secret)), h('li', {}, 'Type the 6-digit code the app shows to confirm:')),
          h('div', { class: 'inline' }, code, h('button', { class: 'btn', type: 'button', onclick: async () => {
            const v = await postJson('/api/auth/2fa/enable', { code: code.value.trim() });
            if (!v.ok) { out.textContent = v.json.error || 'That code isn’t right.'; out.className = 'save-status bad'; return; }
            showCodes(v.json.recoveryCodes); draw();
          } }, 'Turn on'), out));
        code.focus();
      } }, 'Set up'));
  }
  function showCodes(codes) {
    const text = codes.join('\n');
    const dlg = h('div', { class: 'dlg-backdrop' }, h('div', { class: 'dlg', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Recovery codes' },
      h('h2', {}, 'Save your recovery codes'),
      h('p', { class: 'dur' }, 'If you lose your phone, each of these signs you in once. They are shown only now: keep them somewhere safe.'),
      h('pre', { class: 'codes' }, text),
      h('div', { class: 'dlg-actions' },
        h('button', { class: 'btn glass small', type: 'button', onclick: async () => { try { await navigator.clipboard.writeText(text); toast('Copied'); } catch { toast('Could not copy', true); } } }, 'Copy'),
        h('button', { class: 'btn', type: 'button', onclick: () => dlg.remove() }, 'I’ve saved them'))));
    document.body.append(dlg);
  }
  await draw();
  return box;
}
