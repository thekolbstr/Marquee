'use strict';

// Settings → Watch activity: who is watching right now, and what has been watched over the last week, month or quarter.
// Functions only: loaded before app.js, which provides h/api/onLeave/field/... at call time.

async function activityPanel(body) {
  let days = +(store('activityDays') || 30);
  const wrap = h('div');
  const range = h('div', { class: 'seg', role: 'group', 'aria-label': 'Period' });
  const paintRange = () => range.replaceChildren(...[[7, '7 days'], [30, '30 days'], [90, '90 days']].map(([d, l]) => h('button', { type: 'button', class: days === d ? 'on' : '', 'aria-pressed': String(days === d),
    onclick: () => { days = d; store('activityDays', String(d)); paintRange(); load(); } }, l)));
  paintRange();
  body.append(h('h2', {}, 'Watch activity'),
    h('p', { class: 'hint' }, 'Counted from what each person actually plays, so skipping ahead or leaving a video paused doesn’t add time. Dates follow the server’s clock.'),
    h('div', { class: 'actions-row flush' }, range), wrap);

  async function load() {
    let d;
    try { d = await api(`/api/system/activity?days=${days}`); } catch (e) { if (wrap.isConnected) wrap.replaceChildren(h('p', { class: 'error' }, e.message)); return; }
    if (wrap.isConnected) wrap.replaceChildren(...activityView(d));
  }
  await load();
  const t = setInterval(() => { if (wrap.isConnected) load(); }, 10000);
  onLeave(() => clearInterval(t));
}

function activityView(d) {
  const tile = (value, label) => h('div', { class: 'stat' }, h('b', {}, value), h('span', {}, label));
  const now = d.now.length
    ? d.now.map((n) => h('div', { class: 'stream' },
      h('div', {}, h('b', {}, n.title), n.detail ? h('span', { class: 'dur' }, ` · ${n.detail}`) : null,
        h('div', { class: 'dur' }, `${n.person} · ${formatClock(n.position)}${n.duration ? ` of ${formatClock(n.duration)}` : ''}`)),
      h('span', { class: 'badge-s ok' }, n.agoSeconds < 25 ? 'Watching' : 'Just now')))
    : [h('p', { class: 'dur' }, 'Nobody is watching right now.')];

  const max = Math.max(1, ...d.perDay.map((x) => x.seconds));
  const step = Math.max(1, Math.ceil(d.perDay.length / 6));
  const bars = h('div', { class: 'bars', role: 'img', 'aria-label': `Hours watched per day over ${d.days} days` },
    d.perDay.map((x, i) => h('div', { class: 'bar-col', title: `${formatDay(x.day) || x.day}: ${formatWatchTime(x.seconds)}` },
      h('i', { style: `height:${Math.max(x.seconds > 0 ? 3 : 0, Math.round((x.seconds / max) * 100))}%` }),
      h('small', {}, i % step === 0 || i === d.perDay.length - 1 ? x.day.slice(5) : ''))));

  const list = (items, empty) => (items.length ? items : [h('p', { class: 'dur' }, empty)]);
  return [
    h('div', { class: 'panel' }, h('h3', {}, 'Right now'), ...now),
    h('div', { class: 'stats' }, tile(formatWatchTime(d.totalSeconds), 'watched'), tile(String(d.totalPlays), d.totalPlays === 1 ? 'play' : 'plays'), tile(String(d.activePeople), d.activePeople === 1 ? 'person' : 'people')),
    h('div', { class: 'panel' }, h('h3', {}, `Per day · last ${d.days} days`), d.totalSeconds > 0 ? bars : h('p', { class: 'dur' }, 'Nothing has been watched in this period yet.')),
    h('div', { class: 'panel' }, h('h3', {}, 'By person'), ...list(d.people.map((p) => h('div', { class: 'stream' },
      h('div', {}, h('b', {}, p.name), h('div', { class: 'dur' }, `${p.plays} ${p.plays === 1 ? 'play' : 'plays'}${p.lastAt ? ` · last ${new Date(p.lastAt).toLocaleString()}` : ''}`)),
      h('b', {}, formatWatchTime(p.seconds)))), 'No viewing yet.')),
    h('div', { class: 'panel' }, h('h3', {}, 'Most watched'), ...list(d.top.map((t, i) => h('div', { class: 'stream' },
      h('div', {}, h('b', {}, `${i + 1}. ${t.title}`), h('div', { class: 'dur' }, `${t.kind === 'series' ? 'Show' : 'Movie'} · ${t.viewers} ${t.viewers === 1 ? 'viewer' : 'viewers'} · ${t.plays} ${t.plays === 1 ? 'play' : 'plays'}`)),
      h('b', {}, formatWatchTime(t.seconds)))), 'Nothing yet.')),
    h('div', { class: 'panel' }, h('h3', {}, 'Recent'), ...list(d.recent.map((r) => h('div', { class: 'stream' },
      h('div', {}, h('b', {}, r.title), r.detail ? h('span', { class: 'dur' }, ` · ${r.detail}`) : null, h('div', { class: 'dur' }, `${r.person} · ${formatDay(r.day)}`)),
      h('span', { class: 'dur' }, formatWatchTime(r.seconds)))), 'Nothing yet.')),
  ];
}
