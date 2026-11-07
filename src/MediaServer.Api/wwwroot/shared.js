'use strict';

// Pure helpers shared by the pages and the player: formatting, language names, and the small calculations behind the player's menus and
// stats panel. Nothing here touches the DOM or the network, so it can be unit-tested in Node (see web-tests/).

const LANG_ALIASES = {
  eng: 'en', english: 'en', spa: 'es', spanish: 'es', fre: 'fr', fra: 'fr', french: 'fr', ger: 'de', deu: 'de', german: 'de', ita: 'it', italian: 'it',
  por: 'pt', portuguese: 'pt', jpn: 'ja', japanese: 'ja', kor: 'ko', korean: 'ko', chi: 'zh', zho: 'zh', chinese: 'zh', rus: 'ru', russian: 'ru',
  dut: 'nl', nld: 'nl', dutch: 'nl', swe: 'sv', swedish: 'sv', pol: 'pl', polish: 'pl', tur: 'tr', turkish: 'tr', ara: 'ar', arabic: 'ar', hin: 'hi', hindi: 'hi',
};
const LANG_NAMES = { en: 'English', es: 'Spanish', fr: 'French', de: 'German', it: 'Italian', pt: 'Portuguese', ja: 'Japanese', ko: 'Korean', zh: 'Chinese', ru: 'Russian',
  nl: 'Dutch', sv: 'Swedish', pl: 'Polish', tr: 'Turkish', ar: 'Arabic', hi: 'Hindi' };

/** "eng", "English", "en-US", "en_GB" → "en". Unknown names fall back to their first two letters. */
function langKey(l) {
  const k = String(l || '').toLowerCase().split(/[-_ .]/)[0];
  return LANG_ALIASES[k] || k.slice(0, 2);
}

/** 3725 → "1:02:05", 65 → "1:05", 7 → "0:07". Negative, NaN and Infinity read as 0. */
function formatClock(t) {
  t = Number.isFinite(t) ? Math.max(0, Math.floor(t)) : 0;
  const hh = Math.floor(t / 3600), mm = Math.floor((t % 3600) / 60), ss = t % 60;
  return (hh ? `${hh}:${String(mm).padStart(2, '0')}` : `${mm}`) + `:${String(ss).padStart(2, '0')}`;
}

/** 5400 → "1h 30m", 3000 → "50m", 20 → "<1m". */
function formatRuntime(s) {
  if (!Number.isFinite(s) || s <= 0) return '';
  const m = Math.round(s / 60);
  if (m < 1) return '<1m';
  return m >= 60 ? `${Math.floor(m / 60)}h${m % 60 ? ` ${m % 60}m` : ''}` : `${m}m`;
}

/** "42m left" for a title you're partway through; empty when there is nothing sensible to say. */
function formatLeft(seconds) {
  const r = formatRuntime(seconds);
  return r ? `${r} left` : '';
}

/** Time spent watching: 45 → "<1m", 600 → "10m", 5400 → "1h 30m", 90000 → "25h". */
function formatWatchTime(s) {
  if (!Number.isFinite(s) || s <= 0) return '0m';
  const m = Math.round(s / 60);
  if (m < 1) return '<1m';
  if (m < 60) return `${m}m`;
  const hh = Math.floor(m / 60), mm = m % 60;
  return hh >= 10 || !mm ? `${hh}h` : `${hh}h ${mm}m`;
}

/** An ISO date from the server → "today", "yesterday", or "Mon 3 Nov". `now` is injectable for tests. */
function formatDay(day, now = new Date()) {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(day || ''));
  if (!m) return '';
  const d = new Date(+m[1], +m[2] - 1, +m[3]);
  const t = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const diff = Math.round((t - d) / 86400000);
  if (diff === 0) return 'today';
  if (diff === 1) return 'yesterday';
  return d.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' });
}

function formatBytes(n) {
  if (!Number.isFinite(n) || n < 0) return '—';
  if (n >= 1e9) return `${(n / 1e9).toFixed(1)} GB`;
  if (n >= 1e6) return `${Math.round(n / 1e6)} MB`;
  return `${Math.round(n / 1e3)} KB`;
}

/** Bits per second → "8.2 Mbps" / "640 kbps". */
function formatBitrate(bps) {
  if (!Number.isFinite(bps) || bps <= 0) return '—';
  return bps >= 1e6 ? `${(bps / 1e6).toFixed(bps >= 1e7 ? 0 : 1)} Mbps` : `${Math.round(bps / 1e3)} kbps`;
}

/** Seconds of video buffered ahead of `t`, given `[[start, end], …]`. */
function bufferAhead(ranges, t) {
  let ahead = 0;
  for (const [s, e] of ranges) if (s <= t + 0.5 && e > t) ahead = Math.max(ahead, e - t);
  return ahead;
}

/** Which preview thumbnail covers time `t`: the sprite sheet, and the column/row inside it. */
function trickplayCell(info, t) {
  const per = info.columns * info.rows;
  const idx = Math.min(info.count - 1, Math.max(0, Math.floor(t / info.interval)));
  const n = idx % per;
  return { sheet: Math.floor(idx / per), col: n % info.columns, row: Math.floor(n / info.columns) };
}

const QUALITIES = [
  { name: '1080p', width: 1920, mbps: 8 },
  { name: '720p', width: 1280, mbps: 4 },
  { name: '480p', width: 854, mbps: 1.8 },
  { name: '360p', width: 640, mbps: 0.9 },
];
/** Only offer sizes the source can actually fill: nothing is ever scaled up. */
function qualitiesFor(sourceWidth) {
  return sourceWidth ? QUALITIES.filter((q) => q.width <= sourceWidth * 1.05) : [];
}

/** The caption track to switch on by default: your language first (not "forced" ones), then—only if allowed—any track. */
function pickCaption(usable, wantLang, fallbackToAny) {
  const want = langKey(wantLang);
  const inLang = usable.filter((t) => langKey(t.language || t.label) === want);
  return inLang.find((t) => !t.forced) || inLang[0] || (fallbackToAny ? usable.find((t) => !t.forced) || usable[0] : null) || null;
}

/** "English · Original · Stereo · AAC" for an audio track from the server's file info. */
function audioLabel(a, i) {
  const lang = a.language ? (LANG_NAMES[langKey(a.language)] || a.language) : null;
  const ch = a.channels ? ({ 1: 'Mono', 2: 'Stereo', 6: '5.1', 8: '7.1' }[a.channels] || `${a.channels}ch`) : null;
  const title = a.title && a.title.length < 40 && !(lang && a.title.toLowerCase().includes(lang.toLowerCase())) ? a.title : null;
  return [lang || `Track ${i + 1}`, title, ch, a.codec ? a.codec.toUpperCase() : null].filter(Boolean).join(' · ');
}

const CODEC_NAMES = { hevc: 'HEVC', h264: 'H.264', vp9: 'VP9', av1: 'AV1', vc1: 'VC-1', mpeg4: 'MPEG-4', mpeg2video: 'MPEG-2' };
const codecName = (c) => (c ? CODEC_NAMES[String(c).toLowerCase()] || String(c).toUpperCase() : '—');

/**
 * The rows of the "stats for nerds" panel. `d` is a plain object gathered by the player (file info, element state, hls.js, server);
 * every field is optional, and missing ones simply show a dash. Returns [[label, value], …].
 */
function statsRows(d) {
  const dash = (v) => (v === undefined || v === null || v === '' || (typeof v === 'number' && !Number.isFinite(v)) ? '—' : v);
  const px = (w, h) => (w && h ? `${w}×${h}` : '—');
  const rows = [];
  rows.push(['Mode', dash(d.modeLabel)]);
  if (d.reason && String(d.reason).toLowerCase() !== String(d.modeLabel || '').toLowerCase()) rows.push(['Why', d.reason]);
  rows.push(['Source', [d.container && String(d.container).split(',')[0].toUpperCase(), px(d.srcW, d.srcH), codecName(d.vcodec), d.vprofile, d.fps && `${Number(d.fps).toFixed(d.fps % 1 ? 3 : 0)} fps`, d.pixfmt].filter(Boolean).join(' · ') || '—']);
  rows.push(['Source bitrate', formatBitrate(d.bitrate)], ['File size', d.sizeBytes ? formatBytes(d.sizeBytes) : '—']);
  rows.push(['Audio', dash(d.audioLabel)]);
  rows.push(['Output', d.mode === 'direct' ? 'the original file, untouched' : [d.server && d.server.encoder, d.server && d.server.container, d.targetKbps && `${formatBitrate(d.targetKbps * 1000)} target`, d.server && d.server.maxWidth && `≤ ${d.server.maxWidth}px wide`].filter(Boolean).join(' · ') || '—']);
  rows.push(['Decoded', `${px(d.decW, d.decH)} → shown at ${px(d.viewW, d.viewH)}${d.dpr && d.dpr !== 1 ? ` (${d.dpr}× screen)` : ''}`]);
  rows.push(['Frames', d.droppedFrames === undefined ? '—' : `${d.droppedFrames} dropped of ${d.totalFrames}`]);
  rows.push(['Position', `${formatClock(d.time)} / ${formatClock(d.duration)}${d.rate && d.rate !== 1 ? `  (${d.rate}×)` : ''}`]);
  rows.push(['Buffer', d.bufferAhead === undefined ? '—' : `${d.bufferAhead.toFixed(1)} s ahead`]);
  rows.push(['Network', [d.bandwidth && `${formatBitrate(d.bandwidth)} measured`, d.downlink && `${d.downlink} Mbps reported`, d.netType].filter(Boolean).join(' · ') || '—']);
  if (d.session) rows.push(['Stream', `${d.session}${d.server ? ` · ${d.server.running ? 'encoder running' : 'encoder idle'} · ${d.server.segmentsReady}/${d.server.segmentsTotal || '∞'} segments ready${d.server.relaunches ? ` · restarted ${d.server.relaunches}×` : ''} · ${d.server.ageSeconds}s old` : ''}`]);
  rows.push(['Player', dash(d.engine)], ['Device', dash(d.device)]);
  return rows;
}

if (typeof module !== 'undefined') {
  module.exports = { langKey, formatClock, formatRuntime, formatLeft, formatWatchTime, formatDay, formatBytes, formatBitrate, bufferAhead, trickplayCell, qualitiesFor, pickCaption, audioLabel, codecName, statsRows, QUALITIES };
}
