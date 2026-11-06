'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const S = require('../src/MediaServer.Api/wwwroot/shared.js');

test('langKey understands codes, names and regions', () => {
  for (const [input, want] of [['eng', 'en'], ['English', 'en'], ['en-US', 'en'], ['en_GB', 'en'], ['fre', 'fr'], ['fra', 'fr'], ['Japanese', 'ja'], ['zho', 'zh'], ['', ''], [null, ''], [undefined, ''], ['xx-YY', 'xx'], ['klingon', 'kl']])
    assert.equal(S.langKey(input), want, String(input));
});

test('formatClock', () => {
  assert.equal(S.formatClock(0), '0:00'); assert.equal(S.formatClock(7), '0:07'); assert.equal(S.formatClock(65), '1:05'); assert.equal(S.formatClock(3725), '1:02:05');
  assert.equal(S.formatClock(36000), '10:00:00'); assert.equal(S.formatClock(59.9), '0:59');
  for (const bad of [-5, NaN, Infinity, -Infinity, undefined, null]) assert.equal(S.formatClock(bad), '0:00', String(bad));
});

test('formatRuntime and formatLeft', () => {
  assert.equal(S.formatRuntime(20), '<1m'); assert.equal(S.formatRuntime(3000), '50m'); assert.equal(S.formatRuntime(5400), '1h 30m'); assert.equal(S.formatRuntime(7200), '2h');
  assert.equal(S.formatRuntime(0), ''); assert.equal(S.formatRuntime(-3), ''); assert.equal(S.formatRuntime(NaN), '');
  assert.equal(S.formatLeft(2520), '42m left'); assert.equal(S.formatLeft(0), ''); assert.equal(S.formatLeft(undefined), '');
});

test('formatBytes and formatBitrate', () => {
  assert.equal(S.formatBytes(1_500_000_000), '1.5 GB'); assert.equal(S.formatBytes(25_000_000), '25 MB'); assert.equal(S.formatBytes(4096), '4 KB'); assert.equal(S.formatBytes(-1), '—'); assert.equal(S.formatBytes(NaN), '—');
  assert.equal(S.formatBitrate(8_200_000), '8.2 Mbps'); assert.equal(S.formatBitrate(24_000_000), '24 Mbps'); assert.equal(S.formatBitrate(640_000), '640 kbps'); assert.equal(S.formatBitrate(0), '—'); assert.equal(S.formatBitrate(undefined), '—');
});

test('bufferAhead only counts the range that contains the playhead', () => {
  assert.equal(S.bufferAhead([[0, 30]], 10), 20);
  assert.equal(S.bufferAhead([[0, 5], [20, 50]], 10), 0);        // a gap: nothing buffered right here
  assert.equal(S.bufferAhead([[0, 5], [10, 50]], 10), 40);
  assert.equal(S.bufferAhead([[0, 30]], 31), 0); assert.equal(S.bufferAhead([], 3), 0);
});

test('trickplayCell maps a time to a sprite sheet cell and clamps at the ends', () => {
  const info = { interval: 10, columns: 10, rows: 10, count: 250 };
  assert.deepEqual(S.trickplayCell(info, 0), { sheet: 0, col: 0, row: 0 });
  assert.deepEqual(S.trickplayCell(info, 95), { sheet: 0, col: 9, row: 0 });
  assert.deepEqual(S.trickplayCell(info, 100), { sheet: 0, col: 0, row: 1 });
  assert.deepEqual(S.trickplayCell(info, 1000), { sheet: 1, col: 0, row: 0 });
  assert.deepEqual(S.trickplayCell(info, 999999), { sheet: 2, col: 9, row: 4 });   // last thumbnail (index 249)
  assert.deepEqual(S.trickplayCell(info, -50), { sheet: 0, col: 0, row: 0 });
});

test('qualitiesFor never offers to scale up', () => {
  assert.deepEqual(S.qualitiesFor(3840).map((q) => q.name), ['1080p', '720p', '480p', '360p']);
  assert.deepEqual(S.qualitiesFor(1920).map((q) => q.name), ['1080p', '720p', '480p', '360p']);
  assert.deepEqual(S.qualitiesFor(1280).map((q) => q.name), ['720p', '480p', '360p']);
  assert.deepEqual(S.qualitiesFor(640).map((q) => q.name), ['360p']);
  assert.deepEqual(S.qualitiesFor(0), []); assert.deepEqual(S.qualitiesFor(undefined), []);
});

test('pickCaption prefers your language, skips forced tracks, and only falls back when allowed', () => {
  const tracks = [{ id: 'e1', language: 'fre', label: 'French' }, { id: 'e2', language: 'eng', label: 'English', forced: true }, { id: 'e3', language: 'eng', label: 'English SDH' }];
  assert.equal(S.pickCaption(tracks, 'en', false).id, 'e3');
  assert.equal(S.pickCaption(tracks, 'fr', false).id, 'e1');
  assert.equal(S.pickCaption(tracks, 'ja', false), null);
  assert.equal(S.pickCaption(tracks, 'ja', true).id, 'e1');                       // "always": any non-forced track
  assert.equal(S.pickCaption([{ id: 'e2', language: 'eng', forced: true }], 'en', false).id, 'e2');
  assert.equal(S.pickCaption([], 'en', true), null);
});

test('audioLabel', () => {
  assert.equal(S.audioLabel({ language: 'eng', title: 'Original', channels: 2, codec: 'aac' }, 0), 'English · Original · Stereo · AAC');
  assert.equal(S.audioLabel({ language: 'fre', channels: 6, codec: 'eac3' }, 1), 'French · 5.1 · EAC3');
  assert.equal(S.audioLabel({ language: 'eng', title: 'English Commentary' }, 0), 'English');   // a title that repeats the language adds nothing
  assert.equal(S.audioLabel({}, 2), 'Track 3');
  assert.equal(S.audioLabel({ language: 'xx', channels: 3 }, 0), 'xx · 3ch');
});

test('statsRows copes with an empty object and with a full one', () => {
  const empty = S.statsRows({});
  assert.ok(empty.length >= 10); for (const [label, value] of empty) { assert.equal(typeof label, 'string'); assert.equal(typeof value, 'string'); }
  const rows = Object.fromEntries(S.statsRows({
    mode: 'remux', modeLabel: 'Direct stream', reason: 'the container isn’t playable', container: 'matroska,webm', srcW: 1920, srcH: 1080, vcodec: 'h264', vprofile: 'High', fps: 23.976, bitrate: 8_200_000, sizeBytes: 4_000_000_000,
    audioLabel: 'English · 5.1', decW: 1920, decH: 1080, viewW: 1280, viewH: 720, dpr: 2, droppedFrames: 3, totalFrames: 1200, time: 65, duration: 7200, rate: 1.5, bufferAhead: 12.34,
    bandwidth: 25_000_000, session: 'a1b2c3d4', server: { encoder: 'none (video copied)', container: 'MPEG-TS', running: true, segmentsReady: 8, segmentsTotal: 0, relaunches: 1, ageSeconds: 42 }, engine: 'hls.js', device: 'Chrome',
  }));
  assert.equal(rows.Mode, 'Direct stream'); assert.equal(rows.Source, 'MATROSKA · 1920×1080 · H.264 · High · 23.976 fps');
  assert.equal(rows['Source bitrate'], '8.2 Mbps'); assert.equal(rows.Frames, '3 dropped of 1200'); assert.equal(rows.Position, '1:05 / 2:00:00  (1.5×)');
  assert.equal(rows.Buffer, '12.3 s ahead'); assert.equal(rows.Network, '25 Mbps measured'); assert.match(rows.Stream, /8\/∞ segments ready · restarted 1× · 42s old/);
  assert.match(rows.Decoded, /1920×1080 → shown at 1280×720 \(2× screen\)/);
});

test('statsRows says "the original file" for direct play', () => {
  assert.equal(Object.fromEntries(S.statsRows({ mode: 'direct' })).Output, 'the original file, untouched');
});

test('statsRows drops a "Why" that only repeats the mode', () => {
  assert.ok(!S.statsRows({ modeLabel: 'Direct play', reason: 'Direct play' }).some(([k]) => k === 'Why'));
  assert.ok(S.statsRows({ modeLabel: 'Direct stream', reason: 'the container isn’t playable' }).some(([k]) => k === 'Why'));
});
