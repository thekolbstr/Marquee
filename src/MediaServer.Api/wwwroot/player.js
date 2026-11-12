'use strict';

// The video player. One Player object owns everything for the title on screen: the stream (direct play or an HLS session on the server), the
// controls, the settings menu (quality, audio, subtitles, speed, stats), captions, skip buttons, seek previews, casting, and saving progress.
// Pure helpers (formatting, language matching, the stats table) live in shared.js and are unit-tested there.

const SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 2];
const MODE_LABEL = { direct: 'Direct play', remux: 'Direct stream', transcode: 'Transcode' };
const IDLE_MS = 3200, SAVE_EVERY_MS = 10000, STATS_EVERY_MS = 1000, SERVER_STATS_EVERY_MS = 3000;

// A stream's cleanups, run in reverse when it stops (the HLS session, timers, listeners).
function newController() {
  const cleanups = [];
  return { onStop: (fn) => cleanups.push(fn), stop() { while (cleanups.length) { try { cleanups.pop()(); } catch { /* best effort */ } } } };
}


// ---- helpers shared with the live-TV player (live.js)

// Tell the server why playback fell back, so "why did this transcode?" never needs guessing. `f` needs only { mediaId, mode }.
function reportIssue(kind, f, detail, video) {
  try {
    const probe = document.createElement('video');
    const err = video && video.error;
    fetch('/api/client-events', {
      method: 'POST', headers: { 'content-type': 'application/json' }, keepalive: true,
      body: JSON.stringify({ kind, mediaId: f ? f.mediaId : null, mode: f ? f.mode : null, detail: String(detail || '').slice(0, 300),
        mediaError: err ? `${err.code}:${String(err.message || '').slice(0, 120)}` : null,
        nativeHls: !!probe.canPlayType('application/vnd.apple.mpegurl'), mse: !!window.MediaSource, caps: CAPS }),
    }).catch(() => {});
  } catch { /* diagnostics must never break playback */ }
}

// "The video can't be decoded" is only knowable once frames should be flowing: some browsers (notably Safari's native HLS)
// report videoWidth 0 until the first frame renders, so checking at loadedmetadata wrongly condemned working streams.
function watchDecoding(video, ctl, onFail) {
  let timer;
  const onPlaying = () => { clearTimeout(timer); timer = setTimeout(() => { if (!video.paused && video.currentTime > 0.5 && video.videoWidth === 0) onFail(); }, 3000); };
  video.addEventListener('playing', onPlaying);
  ctl.onStop(() => { clearTimeout(timer); video.removeEventListener('playing', onPlaying); });
}

class Player {
  constructor(f, params) {
    this.f = f;
    this.stream = newController();
    this.disposed = false;
    this.hls = null;                         // the hls.js instance, when that engine is in use
    this.session = null;                     // the server's HLS session: { sessionId, base, mode, reason, vod }
    this.serverInfo = null;
    this.mode = f.mode; this.reason = f.reason;
    this.castSession = null;
    this.castTime = 0;
    this.captionTracks = []; this.captionActive = null; this.captionOffset = 0; this.subsSearch = null;
    this.audioTracks = f.audio || [];
    this.options = APP.allowTranscoding ? qualitiesFor(f.width) : [];
    this.quality = store('quality') || 'original';
    if (this.quality !== 'original' && !this.options.some((q) => q.name === this.quality)) this.quality = 'original';   // a saved choice that doesn't fit this file
    this.extra = { audioIndex: this.preferredAudio(), burnSubtitle: null };      // choices that need the server to prepare the stream
    this.startAt = params.has('t') ? Math.max(0, parseFloat(params.get('t')) || 0) : pref('resume') === 'start' ? 0 : f.resumeSeconds;
    this.menuPage = null;
    this.timers = {};
    this.disposers = [];
    this.build();
    this.bind();
  }

  // ------------------------------------------------------------------ small accessors
  total() { return this.f.durationSeconds || (Number.isFinite(this.video.duration) ? this.video.duration : 0); }
  position() { return this.castSession ? this.castTime : this.video.currentTime || 0; }
  defaultAudioIndex() { return (this.audioTracks.find((a) => a.isDefault) || this.audioTracks[0] || {}).index; }
  currentAudioIndex() { return this.extra.audioIndex ?? this.defaultAudioIndex(); }
  // A track in your preferred language (Settings → Playback) wins over the file's default, when there is one.
  preferredAudio() {
    const want = langKey(pref('audioLang'));
    if (!want || this.audioTracks.length < 2) return null;
    const hit = this.audioTracks.find((a) => langKey(a.language) === want);
    return hit && hit.index !== this.defaultAudioIndex() ? hit.index : null;
  }
  leave() { if (history.length > 1) history.back(); else location.hash = '#/'; }

  // ------------------------------------------------------------------ the screen
  build() {
    const f = this.f;
    this.video = h('video', { autoplay: pref('autoplay') === 'on', playsinline: true, preload: 'auto' });
    this.spinner = h('div', { class: 'buffering hidden' }, h('i'));
    this.errorBox = h('div', { class: 'perr hidden', role: 'alert' });
    this.castBanner = h('div', { class: 'castbanner hidden' });
    this.nextBtn = h('a', { class: 'btn next-btn hidden', href: f.nextMediaId ? `#/watch/${f.nextMediaId}` : '#' }, icon('next'), h('span', {}, 'Next episode', f.nextTitle ? h('small', {}, f.nextTitle) : null));
    this.skipBtn = h('button', { class: 'btn skip-btn hidden', type: 'button' });
    const title = f.title.includes(' – ') ? [f.title.split(' – ')[0], h('small', {}, f.title.split(' – ').slice(1).join(' – '))] : f.title;
    const top = h('div', { class: 'player-top' }, h('button', { class: 'round', type: 'button', 'aria-label': 'Back', onclick: () => this.leave() }, icon('back')), h('div', { class: 'player-title' }, title));
    this.menu = h('div', { class: 'smenu hidden', role: 'menu' });
    this.statsBox = h('div', { class: 'statsbox hidden', role: 'status' });
    this.stage = h('div', { class: 'player' }, this.video, this.spinner, top, this.castBanner, this.skipBtn, this.nextBtn, this.statsBox, this.errorBox, this.menu);
    this.buildControls();
    this.stage.append(this.controls);
    setView(this.stage);
    window.scrollTo(0, 0);
    document.body.classList.add('watching');
    document.title = `${f.title} · ${APP.name}`;
  }

  buildControls() {
    const v = this.video;
    const btn = (name, label, onclick) => h('button', { class: 'cbtn', type: 'button', title: label, 'aria-label': label, onclick }, icon(name));
    this.playBtn = btn('play', 'Play / pause (Space)', () => this.togglePlay());
    this.muteBtn = btn('volume', 'Mute (M)', () => this.toggleMute());
    this.castBtn = btn('cast', 'Cast to a TV', () => this.cast());
    this.gearBtn = btn('gear', 'Settings', () => this.toggleMenu());
    this.gearBtn.setAttribute('aria-haspopup', 'true');
    this.fsBtn = btn('expand', 'Full screen (F)', () => this.toggleFullscreen());
    this.vol = h('input', { class: 'vol', type: 'range', min: '0', max: '1', step: '0.05', value: String(v.volume), 'aria-label': 'Volume' });
    this.vol.addEventListener('input', () => { if (this.castSession) { this.castCmd('volume', parseFloat(this.vol.value)); return; } v.volume = parseFloat(this.vol.value); v.muted = false; });
    this.cur = h('span', { class: 'time' }, '0:00');
    this.dur = h('span', { class: 'time' }, formatClock(this.total()));
    this.buf = h('i', { class: 'buf' }); this.fill = h('i', { class: 'fill' }); this.knob = h('b', { class: 'knob' });
    this.tpImage = h('div', { class: 'tp hidden' });
    this.tpLabel = h('span', { class: 'tt' });
    this.hint = h('div', { class: 'scrub-hint hidden' }, h('div', { class: 'tpwrap' }, this.tpImage, this.tpLabel));
    this.bar = h('div', { class: 'seekbar', role: 'slider', tabindex: '0', 'aria-label': 'Seek', 'aria-valuemin': '0' }, this.buf, this.fill, this.knob, this.hint);
    this.controls = h('div', { class: 'controls' }, this.bar,
      h('div', { class: 'crow' }, this.playBtn, btn('rew', 'Skip back', () => this.skip(-skipSeconds())), btn('fwd', 'Skip forward', () => this.skip(skipSeconds())),
        this.cur, h('span', { class: 'sep' }, '/'), this.dur, h('span', { class: 'grow' }), this.castBtn, this.muteBtn, this.vol, this.gearBtn, this.fsBtn));
    if (pref('rememberVolume') === 'on') { v.volume = Math.min(1, Math.max(0, parseFloat(pref('volume')) || 1)); v.muted = pref('muted') === 'on'; this.vol.value = v.muted ? '0' : String(v.volume); }
    this.setSpeed(parseFloat(pref('speed')), false);
    this.bindSeekBar();
  }

  bindSeekBar() {
    const bar = this.bar;
    let scrubbing = false, scrubT = 0;
    const frac = (e) => { const b = bar.getBoundingClientRect(); return Math.min(1, Math.max(0, (e.clientX - b.left) / b.width)); };
    this.scrubbing = () => (scrubbing ? scrubT : null);
    bar.addEventListener('pointerdown', (e) => { scrubbing = true; bar.setPointerCapture(e.pointerId); scrubT = frac(e) * this.total(); this.paint(); });
    bar.addEventListener('pointermove', (e) => {
      const x = frac(e), t = x * this.total();
      this.showPreview(t); this.hint.style.left = `${x * 100}%`; this.hint.classList.remove('hidden');
      if (scrubbing) { scrubT = t; this.paint(); }
    });
    bar.addEventListener('pointerleave', () => this.hint.classList.add('hidden'));
    bar.addEventListener('pointerup', () => { if (scrubbing) { scrubbing = false; this.seekTo(scrubT); this.wake(); } });
    bar.addEventListener('keydown', (e) => {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      e.preventDefault(); e.stopPropagation(); this.skip(e.key === 'ArrowLeft' ? -skipSeconds() : skipSeconds());
    });
  }

  // ------------------------------------------------------------------ events
  bind() {
    const v = this.video, stage = this.stage;
    const on = (target, ev, fn, opts) => { target.addEventListener(ev, fn, opts); this.disposers.push(() => target.removeEventListener(ev, fn, opts)); };
    this.on = on;
    ['mousemove', 'touchstart', 'keydown'].forEach((ev) => on(stage, ev, () => this.wake(), { passive: true }));
    on(v, 'play', () => { stage.classList.add('playing'); this.swapIcon(this.playBtn, 'pause'); this.wake(); });
    on(v, 'pause', () => { stage.classList.remove('playing', 'idle'); this.swapIcon(this.playBtn, 'play'); this.saveNow(); });
    on(v, 'waiting', () => this.spinner.classList.remove('hidden'));
    ['playing', 'canplay', 'seeked'].forEach((ev) => on(v, ev, () => this.spinner.classList.add('hidden')));
    ['timeupdate', 'progress', 'durationchange'].forEach((ev) => on(v, ev, () => this.paint()));
    on(v, 'timeupdate', () => this.onTime());
    on(v, 'volumechange', () => {
      if (pref('rememberVolume') === 'on') { store('pref:volume', String(v.volume)); store('pref:muted', v.muted ? 'on' : 'off'); }
      this.swapIcon(this.muteBtn, v.muted || v.volume === 0 ? 'mute' : 'volume'); this.vol.value = v.muted ? '0' : String(v.volume);
    });
    on(v, 'ended', () => this.onEnded());
    on(v, 'click', () => { if (matchMedia('(hover: hover)').matches) this.togglePlay(); });   // a tap on a touch screen only reveals the controls
    on(v, 'dblclick', () => this.toggleFullscreen());
    // composedPath() is captured at dispatch time, so it still includes the menu after a row's click handler has re-rendered it (which detaches the clicked button).
    on(stage, 'click', (e) => { const path = e.composedPath(); if (this.menuPage && !path.includes(this.menu) && !path.includes(this.gearBtn)) this.closeMenu(); });
    on(document, 'keydown', (e) => this.onKey(e));
    on(window, 'pagehide', () => this.saveBeacon());
    this.skipBtn.addEventListener('click', () => this.doSkip());
    this.timers.save = setInterval(() => { if (!v.paused || this.castSession) this.saveNow(); }, SAVE_EVERY_MS);
    this.wake();
    this.setupAirPlayState();
  }

  toggleMute() { if (this.castSession) this.castCmd('mute', this.castSession.muted ? 0 : 1); else this.video.muted = !this.video.muted; }
  swapIcon(button, name) { button.replaceChildren(icon(name)); }
  togglePlay() {
    if (this.castSession) { this.castCmd(this.castSession.state === 'PLAYING' || this.castSession.state === 'BUFFERING' ? 'pause' : 'play'); return; }
    const v = this.video; if (v.paused) v.play().catch(() => {}); else v.pause();
  }

  wake() {
    this.stage.classList.remove('idle');
    clearTimeout(this.timers.idle);
    this.timers.idle = setTimeout(() => { if (!this.video.paused && !this.menuPage) this.stage.classList.add('idle'); }, IDLE_MS);
  }

  paint() {
    const d = this.total(), scrub = this.scrubbing && this.scrubbing(), t = scrub ?? this.position();
    const p = d ? Math.min(100, (t / d) * 100) : 0;
    this.fill.style.width = `${p}%`; this.knob.style.left = `${p}%`;
    this.cur.textContent = formatClock(t); this.dur.textContent = formatClock(d);
    this.bar.setAttribute('aria-valuemax', String(Math.round(d))); this.bar.setAttribute('aria-valuenow', String(Math.round(t)));
    const ranges = this.bufferedRanges();
    const end = ranges.reduce((m, [s, e]) => (s <= t + 1 && e > m ? e : m), 0);
    this.buf.style.width = `${d ? Math.min(100, (Math.max(end, t) / d) * 100) : 0}%`;
  }

  bufferedRanges() { const r = this.video.buffered; return Array.from({ length: r.length }, (_, i) => [r.start(i), r.end(i)]); }

  seekTo(t) {
    const d = this.total();
    if (this.castSession) { const to = Math.max(0, d ? Math.min(d - 1, t) : t); this.castTime = to; this.paint(); this.castCmd('seek', to); return; }
    let to = Math.max(0, d ? Math.min(d - 0.5, t) : t);
    const r = this.video.seekable;                  // a copied stream can only be entered where it has been produced so far
    if (r && r.length && to > r.end(r.length - 1)) to = r.end(r.length - 1);
    this.video.currentTime = to;
  }
  skip(dt) { this.seekTo(this.position() + dt); this.wake(); }

  toggleFullscreen() {
    const s = this.stage, v = this.video;
    if (document.fullscreenElement || document.webkitFullscreenElement) (document.exitFullscreen || document.webkitExitFullscreen).call(document);
    else if (s.requestFullscreen || s.webkitRequestFullscreen) (s.requestFullscreen || s.webkitRequestFullscreen).call(s);
    else if (v.webkitEnterFullscreen) v.webkitEnterFullscreen();   // iPhone only fullscreens the video element itself
  }

  // ------------------------------------------------------------------ next episode, skip intro/credits
  onTime() {
    const d = this.total();
    this.nextBtn.classList.toggle('hidden', !(this.f.nextMediaId && d && d - this.video.currentTime < 30));
    if (!this.markers || !this.markers.length) return;
    const t = this.video.currentTime;
    const m = this.markers.find((x) => t >= x.start - 1 && t < x.end - 2) || null;
    if (m === this.activeMarker) return;
    this.activeMarker = m;
    if (!m) { this.skipBtn.classList.add('hidden'); return; }
    this.skipBtn.replaceChildren(icon('next'), m.kind === 'intro' ? 'Skip intro' : 'Skip credits');
    this.skipBtn.classList.remove('hidden');
  }

  doSkip() {
    const m = this.activeMarker;
    if (!m) return;
    if (m.kind === 'credits' && this.f.nextMediaId && m.end >= (this.f.durationSeconds || Infinity) - 10) location.hash = `#/watch/${this.f.nextMediaId}`;
    else this.video.currentTime = Math.min(m.end, (this.f.durationSeconds || m.end) - 1);
    this.skipBtn.classList.add('hidden'); this.activeMarker = null; this.wake();
  }

  onEnded() {
    const d = this.f.durationSeconds || this.video.duration;
    this.postProgress({ position: d, duration: d, watched: true });
    if (this.f.nextMediaId && pref('autonext') === 'on') location.hash = `#/watch/${this.f.nextMediaId}`; else this.leave();
  }

  // ------------------------------------------------------------------ progress (saved to the server, so it follows you between devices)
  progressUrl() { return `/api/media/${this.f.mediaId}/progress`; }
  progressBody() { return JSON.stringify({ position: this.position(), duration: this.total() || undefined }); }
  canSave() { return Number.isFinite(this.position()) && this.position() > 5; }
  postProgress(body) { fetch(this.progressUrl(), { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body), keepalive: true }).catch(() => {}); }
  saveNow() { if (this.canSave()) fetch(this.progressUrl(), { method: 'POST', headers: { 'content-type': 'application/json' }, body: this.progressBody(), keepalive: true }).catch(() => {}); }
  saveBeacon() { if (this.canSave()) { try { navigator.sendBeacon(this.progressUrl(), new Blob([this.progressBody()], { type: 'application/json' })); } catch { /* ignore */ } } }

  // ------------------------------------------------------------------ keyboard
  onKey(e) {
    if (e.target.closest && e.target.closest('input, textarea, select')) return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if (document.querySelector('.dlg-backdrop')) return;   // a dialog is open: its own keys apply   // browser shortcuts (copy, find, address bar…) are not player keys
    const v = this.video;
    switch (e.key) {
      case ' ': case 'k': e.preventDefault(); this.togglePlay(); break;
      case 'ArrowLeft': case 'j': e.preventDefault(); this.skip(-skipSeconds()); break;
      case 'ArrowRight': case 'l': e.preventDefault(); this.skip(skipSeconds()); break;
      case 'ArrowUp': e.preventDefault(); v.volume = Math.min(1, v.volume + 0.1); break;
      case 'ArrowDown': e.preventDefault(); v.volume = Math.max(0, v.volume - 0.1); break;
      case 'm': this.toggleMute(); break;
      case 'f': this.toggleFullscreen(); break;
      case 'c': this.toggleCaptions(); break;
      case 'i': this.toggleStats(); break;
      case '<': this.stepSpeed(-1); break;
      case '>': this.stepSpeed(1); break;
      case 'n': if (this.f.nextMediaId) location.hash = `#/watch/${this.f.nextMediaId}`; break;
      case 'Escape': if (this.menuPage) this.closeMenu(); else if (!document.fullscreenElement) this.leave(); break;
      default: return;
    }
    this.wake();
  }

  // ------------------------------------------------------------------ the settings menu (the cog)
  toggleMenu() { if (this.menuPage) this.closeMenu(); else this.openMenu('main'); }
  openMenu(page) { this.menuPage = page; this.renderMenu(); this.menu.classList.remove('hidden'); this.gearBtn.classList.add('on'); this.wake(); }
  closeMenu() { this.menuPage = null; this.menu.classList.add('hidden'); this.gearBtn.classList.remove('on'); this.wake(); }

  qualityLabel() { return this.quality === 'original' ? `Original${resLabel(this.f.width, this.f.height) ? ` (${resLabel(this.f.width, this.f.height)})` : ''}` : this.quality; }
  audioLabelNow() { const i = this.audioTracks.findIndex((a) => a.index === this.currentAudioIndex()); return i < 0 ? '' : audioLabel(this.audioTracks[i], i); }
  captionLabelNow() {
    if (this.extra.burnSubtitle != null) { const t = this.captionTracks.find((x) => x.id === `e${this.extra.burnSubtitle}`); return t ? `${t.label} (burned in)` : 'Burned in'; }
    const t = this.captionTracks.find((x) => x.id === this.captionActive);
    return t ? t.label : 'Off';
  }
  speedLabel(r = this.video.playbackRate) { return r === 1 ? 'Normal' : `${r}×`; }

  renderMenu() {
    const page = this.menuPage;
    const row = (label, value, go) => h('button', { type: 'button', role: 'menuitem', class: 'mrow', onclick: go }, h('span', {}, label), h('span', { class: 'mval' }, value, h('i', { class: 'chev' }, '›')));
    const opt = (label, on, note, pick) => h('button', { type: 'button', role: 'menuitemradio', 'aria-checked': String(on), class: 'mopt' + (on ? ' on' : ''), onclick: pick }, h('span', {}, label), h('small', {}, note || (on ? '✓' : '')));
    const head = (title) => h('button', { type: 'button', class: 'mhead', onclick: () => this.openMenu('main') }, h('i', { class: 'chev back' }, '‹'), title);
    let kids;
    if (page === 'main') {
      kids = [
        this.options.length ? row('Quality', this.qualityLabel(), () => this.openMenu('quality')) : null,
        this.audioTracks.length > 1 ? row('Audio', this.audioLabelNow(), () => this.openMenu('audio')) : null,
        this.captionTracks.length || this.subsSearch ? row('Subtitles', this.captionLabelNow(), () => this.openMenu('subs')) : null,
        row('Playback speed', this.speedLabel(), () => this.openMenu('speed')),
        h('div', { class: 'mrow toggle' }, h('span', {}, 'Stats for nerds'), toggle(!this.statsBox.classList.contains('hidden'), (on) => this.setStats(on))),
      ];
    } else if (page === 'quality') {
      kids = [head('Quality'), opt(`Original${resLabel(this.f.width, this.f.height) ? ` (${resLabel(this.f.width, this.f.height)})` : ''}`, this.quality === 'original', 'best available', () => this.chooseQuality('original')),
        ...this.options.map((q) => opt(q.name, this.quality === q.name, `up to ${q.mbps} Mbps`, () => this.chooseQuality(q.name)))];
    } else if (page === 'audio') {
      kids = [head('Audio'), ...this.audioTracks.map((a, i) => opt(audioLabel(a, i), a.index === this.currentAudioIndex(), '', () => this.chooseAudio(a.index)))];
    } else if (page === 'subs') {
      kids = [head('Subtitles'), opt('Off', this.captionActive == null && this.extra.burnSubtitle == null, '', () => this.selectCaption(null)),
        ...this.captionTracks.map((t) => {
          const picture = t.supported === false, burnable = picture && APP.allowTranscoding && /^e\d+$/.test(t.id);
          const on = picture ? this.extra.burnSubtitle === Number(t.id.slice(1)) : this.captionActive === t.id && this.extra.burnSubtitle == null;
          return h('button', { type: 'button', role: 'menuitemradio', 'aria-checked': String(on), class: 'mopt' + (on ? ' on' : ''), disabled: picture && !burnable ? true : null, onclick: () => this.selectCaption(t) },
            h('span', {}, t.label, t.forced ? ' (forced)' : ''), h('small', {}, on ? '✓' : picture ? (burnable ? 'burn in' : 'picture-based') : t.source === 'file' ? 'file' : ''));
        })];
      const shown = this.captionTracks.find((t) => t.id === this.captionActive);
      if (shown && this.extra.burnSubtitle == null) {
        const off = this.captionOffset, label = off === 0 ? 'In sync' : `${off > 0 ? '+' : ''}${off.toFixed(1)} s`;
        kids.push(h('div', { class: 'mrow toggle' }, h('span', {}, 'Delay'), h('span', { class: 'mval nudge' },
          h('button', { type: 'button', class: 'cbtn', title: 'Show subtitles earlier', 'aria-label': 'Show subtitles earlier', onclick: () => this.nudgeCaptions(-0.5) }, '−'), label,
          h('button', { type: 'button', class: 'cbtn', title: 'Show subtitles later', 'aria-label': 'Show subtitles later', onclick: () => this.nudgeCaptions(0.5) }, '+'))));
        if (shown.source === 'download') kids.push(h('button', { type: 'button', role: 'menuitem', class: 'mrow', onclick: () => this.removeDownloadedCaption(shown) }, h('span', {}, 'Remove this downloaded subtitle')));
      }
      if (this.subsSearch) kids.push(h('button', { type: 'button', role: 'menuitem', class: 'mrow', onclick: () => this.openSubtitleSearch() }, h('span', {}, 'Find subtitles…'), h('span', { class: 'mval' }, h('i', { class: 'chev' }, '›'))));
    } else {
      kids = [head('Playback speed'), ...SPEEDS.map((r) => opt(this.speedLabel(r), this.video.playbackRate === r, '', () => { this.setSpeed(r); this.openMenu('speed'); }))];
    }
    this.menu.replaceChildren(...kids.filter(Boolean));
  }

  // ------------------------------------------------------------------ speed, quality, audio, subtitles
  setSpeed(rate, remember = true) {
    const r = SPEEDS.includes(rate) ? rate : 1;
    this.video.defaultPlaybackRate = r; this.video.playbackRate = r;
    if (remember) setPref('speed', String(r));
  }
  stepSpeed(d) {
    const i = SPEEDS.indexOf(this.video.playbackRate);
    this.setSpeed(SPEEDS[Math.min(SPEEDS.length - 1, Math.max(0, (i < 0 ? 2 : i) + d))]);
    toast(`Speed ${this.speedLabel()}`);
  }

  async chooseQuality(name) {
    this.closeMenu();
    if (name === this.quality) return;
    this.quality = name; store('quality', name);
    await this.reload({});
  }
  async chooseAudio(index) {
    this.closeMenu();
    if (index !== this.currentAudioIndex()) await this.reload({ audioIndex: index });
  }

  async loadCaptions() {
    api('/api/subtitles/status').then((s) => { this.subsSearch = s.configured ? s : null; }).catch(() => {});
    const tracks = (await api(`/api/media/${this.f.mediaId}/captions`)).tracks || [];
    this.captionTracks = tracks;
    const usable = tracks.filter((t) => t.supported !== false);
    const pick = pref('cc') === 'off' ? null : pickCaption(usable, pref('ccLang'), pref('cc') === 'always');
    if (pick) this.showCaption(pick);
  }

  // Show a text track (WebVTT from the server) in the player.
  showCaption(t) {
    const v = this.video;
    v.querySelectorAll('track').forEach((x) => x.remove());
    for (const x of v.textTracks) x.mode = 'disabled';
    this.captionActive = t ? t.id : null; this.captionOffset = 0;
    if (!t) return;
    const lang = /^[a-z]{2,3}$/i.test(t.language || '') ? t.language : 'und';
    const el = h('track', { kind: 'subtitles', label: t.label, srclang: lang, src: `/api/media/${this.f.mediaId}/captions/${t.id}.vtt`, default: true });
    el.addEventListener('error', () => { toast('Could not load those captions', true); this.showCaption(null); });
    el.addEventListener('load', () => this.applyCaptionOffset());
    v.append(el);
    el.track.mode = 'showing';
  }

  // Shift every cue of the shown track by captionOffset seconds (positive = later). Original times are remembered on the cue itself.
  applyCaptionOffset() {
    for (const tr of this.video.textTracks) {
      if (tr.mode === 'disabled' || !tr.cues) continue;
      for (const cue of tr.cues) {
        if (!cue._orig) cue._orig = [cue.startTime, cue.endTime];
        cue.startTime = Math.max(0, cue._orig[0] + this.captionOffset); cue.endTime = Math.max(0, cue._orig[1] + this.captionOffset);
      }
    }
  }
  nudgeCaptions(d) {
    this.captionOffset = Math.round(Math.max(-30, Math.min(30, this.captionOffset + d)) * 10) / 10;
    this.applyCaptionOffset(); this.openMenu('subs');
  }

  // The "Find subtitles" dialog: search OpenSubtitles for this title, pick one, and it is kept on the server for next time.
  openSubtitleSearch() {
    this.closeMenu();
    const LANGS = [['en', 'English'], ['es', 'Español'], ['fr', 'Français'], ['de', 'Deutsch'], ['it', 'Italiano'], ['pt-br', 'Português (Brasil)'], ['pt-pt', 'Português'], ['nl', 'Nederlands'], ['sv', 'Svenska'], ['pl', 'Polski'], ['ru', 'Русский'], ['tr', 'Türkçe'], ['ja', '日本語'], ['ko', '한국어'], ['zh-cn', '中文']];
    const lang = h('select', { class: 'dlg-input', 'aria-label': 'Language' }, LANGS.map(([v, l]) => h('option', { value: v, selected: v === (this.subsSearch && this.subsSearch.language) }, l)));
    const list = h('div', { class: 'dlg-list' });
    const status = h('p', { class: 'dur' });
    const close = () => { dlg.remove(); document.removeEventListener('keydown', onKey); };
    const onKey = (e) => { if (e.key === 'Escape') close(); };
    const search = async () => {
      status.className = 'dur'; status.textContent = 'Searching…'; list.replaceChildren();
      try {
        const r = (await api(`/api/media/${this.f.mediaId}/subtitles/search?lang=${encodeURIComponent(lang.value)}`)).results;
        status.textContent = r.length ? `${r.length} found. Best matches first.` : 'Nothing found for this title in that language.';
        list.replaceChildren(...r.slice(0, 40).map((x) => h('button', { type: 'button', class: 'mopt', onclick: () => pick(x) },
          h('span', {}, x.name, x.hearingImpaired ? ' (hearing impaired)' : ''), h('small', {}, `${x.downloads.toLocaleString()} downloads${x.trusted ? ' · trusted' : ''}`))));
      } catch (e) { status.className = 'dur bad'; status.textContent = e.message; }
    };
    const pick = async (x) => {
      status.className = 'dur'; status.textContent = 'Downloading…'; list.replaceChildren();
      try {
        const res = await fetch(`/api/media/${this.f.mediaId}/subtitles/download`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ fileId: x.fileId, language: lang.value }) });
        const j = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(j.error || 'The download did not work.');
        this.captionTracks = j.tracks; close();
        const t = j.tracks.find((c) => c.id === j.track); if (t) { this.selectCaption(t); toast(`Subtitles added: ${t.label}`); }
      } catch (e) { status.className = 'dur bad'; status.textContent = e.message; search(); }
    };
    const dlg = h('div', { class: 'dlg-backdrop', onclick: (e) => { if (e.target === dlg) close(); } },
      h('div', { class: 'dlg', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Find subtitles' },
        h('h2', {}, 'Find subtitles'), h('p', { class: 'dur' }, `For “${this.f.title}”, from OpenSubtitles.`),
        h('div', { class: 'inline' }, lang, h('button', { class: 'btn small', type: 'button', onclick: search }, 'Search')),
        status, list, h('div', { class: 'dlg-actions' }, h('button', { class: 'btn glass small', type: 'button', onclick: close }, 'Close'))));
    this.stage.append(dlg);
    document.addEventListener('keydown', onKey);
    search();
  }

  async removeDownloadedCaption(t) {
    try {
      const res = await fetch(`/api/media/${this.f.mediaId}/subtitles/${t.id}`, { method: 'DELETE' });
      if (!res.ok) throw new Error();
      this.showCaption(null); this.captionTracks = (await res.json()).tracks; toast('Removed'); this.openMenu('subs');
    } catch { toast('Could not remove that subtitle', true); }
  }

  async selectCaption(t) {
    this.closeMenu();
    const picture = t && t.supported === false;
    if (picture) {                                   // picture-based: drawn onto the video, which needs the server to re-encode
      if (!APP.allowTranscoding || !/^e\d+$/.test(t.id)) return;
      this.showCaption(null);
      await this.reload({ burnSubtitle: Number(t.id.slice(1)) }, 'Adding subtitles…');
      return;
    }
    this.showCaption(t);
    if (this.extra.burnSubtitle != null) await this.reload({ burnSubtitle: null });   // leaving a burned-in track: back to normal playback
  }

  toggleCaptions() {
    if (!this.captionTracks.length) { toast('No captions available'); return; }
    if (this.captionActive || this.extra.burnSubtitle != null) { this.selectCaption(null); toast('Captions off'); return; }
    const usable = this.captionTracks.filter((t) => t.supported !== false);
    const t = pickCaption(usable, pref('ccLang'), true) || usable[0];
    if (t) { this.showCaption(t); toast(`Captions: ${t.label}`); } else toast('No captions available');
  }

  // ------------------------------------------------------------------ skip markers and seek previews
  async loadMarkers() {
    try { this.markers = (await api(`/api/media/${this.f.mediaId}/chapters`)).markers || []; } catch { this.markers = []; }
  }

  // Preview pictures are made in the background the first time a title is opened; keep asking until they exist (or won't).
  async loadPreviews() {
    for (let i = 0; i < 40 && !this.disposed; i++) {
      let res; try { res = await fetch(`/api/media/${this.f.mediaId}/trickplay`); } catch { return; }
      if (res.status === 200) { this.previews = await res.json(); return; }
      if (res.status !== 202) return;                // 404: this title won't get previews
      await sleep(15000);
    }
  }
  showPreview(t) {
    this.tpLabel.textContent = formatClock(t);
    const info = this.previews;
    if (!info) { this.tpImage.classList.add('hidden'); return; }
    const c = trickplayCell(info, t);
    this.tpImage.style.cssText = `width:${info.width}px;height:${info.height}px;background-image:url(/api/media/${this.f.mediaId}/trickplay/${c.sheet}.jpg);`
      + `background-size:${info.columns * info.width}px ${info.rows * info.height}px;background-position:-${c.col * info.width}px -${c.row * info.height}px`;
    this.tpImage.classList.remove('hidden');
  }

  // ------------------------------------------------------------------ stats for nerds
  toggleStats() { this.setStats(this.statsBox.classList.contains('hidden')); }
  setStats(on) {
    this.statsBox.classList.toggle('hidden', !on);
    setPref('stats', on ? 'on' : 'off');
    clearInterval(this.timers.stats); clearInterval(this.timers.serverStats);
    if (!on) return;
    this.refreshStats();
    this.timers.stats = setInterval(() => this.refreshStats(), STATS_EVERY_MS);
    this.pullServerStats();
    this.timers.serverStats = setInterval(() => this.pullServerStats(), SERVER_STATS_EVERY_MS);
  }

  async pullServerStats() {
    if (!this.session) { this.serverInfo = null; return; }
    try { const res = await fetch(`${this.session.base}/info`); this.serverInfo = res.ok ? await res.json() : null; } catch { /* keep the last one */ }
  }

  statsData() {
    const v = this.video, f = this.f, q = v.getVideoPlaybackQuality ? v.getVideoPlaybackQuality() : null, conn = navigator.connection || {};
    return {
      mode: this.mode, modeLabel: `${MODE_LABEL[this.mode] || this.mode}${this.quality !== 'original' ? ` · ${this.quality}` : ''}`, reason: this.reason,
      container: f.container, srcW: f.width, srcH: f.height, vcodec: f.videoCodec, vprofile: f.videoProfile, fps: f.frameRate, pixfmt: f.pixelFormat, bitrate: f.bitRate, sizeBytes: f.sizeBytes,
      audioLabel: this.audioLabelNow(), decW: v.videoWidth, decH: v.videoHeight, viewW: v.clientWidth, viewH: v.clientHeight, dpr: window.devicePixelRatio,
      droppedFrames: q ? q.droppedVideoFrames : undefined, totalFrames: q ? q.totalVideoFrames : undefined,
      time: this.position(), duration: this.total(), rate: v.playbackRate, bufferAhead: v.buffered.length ? bufferAhead(this.bufferedRanges(), v.currentTime) : undefined,
      bandwidth: this.hls ? this.hls.bandwidthEstimate : undefined, downlink: conn.downlink, netType: conn.effectiveType,
      session: this.session ? this.session.sessionId.slice(0, 8) : null, server: this.serverInfo, targetKbps: this.serverInfo ? this.serverInfo.videoKbps : undefined,
      engine: this.castSession ? 'Cast (the server drives the TV)' : this.hls ? `hls.js ${Hls.version}` : this.session ? 'native HLS' : 'native <video>',
      device: `${browserName()} · ${CAPS.v || 'no video'} · ${CAPS.a || 'no audio'}`,
    };
  }

  refreshStats() {
    const rows = statsRows(this.statsData());
    const copy = h('button', { class: 'statsbtn', type: 'button', title: 'Copy', onclick: async () => { try { await navigator.clipboard.writeText(rows.map(([k, val]) => `${k}: ${val}`).join('\n')); toast('Copied'); } catch { toast('Could not copy', true); } } }, 'Copy');
    const close = h('button', { class: 'statsbtn', type: 'button', title: 'Close', 'aria-label': 'Close', onclick: () => this.setStats(false) }, '✕');
    this.statsBox.replaceChildren(h('div', { class: 'statshead' }, h('strong', {}, 'Stats for nerds'), h('span', {}, copy, close)),
      ...rows.map(([k, val]) => h('div', { class: 'statsrow' }, h('span', { class: 'k' }, k), h('span', { class: 'v' }, val))));
  }

  // ------------------------------------------------------------------ errors and diagnostics
  setBusy(on) { this.spinner.classList.toggle('hidden', !on); }

  // Something the viewer must know about: what happened, and a way to try again.
  fail(message, { retry = true } = {}) {
    this.setBusy(false);
    this.errorBox.replaceChildren(h('strong', {}, 'Can’t play this right now'), h('p', {}, message),
      h('div', { class: 'perr-actions' }, retry ? h('button', { class: 'btn', type: 'button', onclick: () => { this.errorBox.classList.add('hidden'); this.reload({}); } }, 'Try again') : null,
        h('button', { class: 'btn glass', type: 'button', onclick: () => this.leave() }, 'Go back')));
    this.errorBox.classList.remove('hidden');
  }

  report(kind, detail) { reportIssue(kind, { mediaId: this.f.mediaId, mode: this.f.mode }, detail, this.video); }

  // ------------------------------------------------------------------ starting a stream
  async start() {
    this.setBusy(true);
    // Optional extras: none of them may ever hold up playback.
    this.loadCaptions().catch(() => {});
    this.loadMarkers();
    this.loadPreviews();
    if (pref('stats') === 'on') this.setStats(true);
    if (await this.resumeCasting()) return;          // already playing on a TV: this page just becomes its remote
    try { await (this.quality !== 'original' ? this.startHls(this.startAt, false) : this.startPlayback(this.startAt)); }
    catch (e) { if (!this.disposed) { this.report('start-failed', e.message); this.fail(e.message); } return; }
    if (this.disposed) this.stream.stop();            // navigated away while the stream was starting
  }

  // Restart from the same moment, optionally changing the audio track / burned-in subtitles / quality.
  async reload(change, notice, atOverride) {
    Object.assign(this.extra, change);
    const at = atOverride ?? this.position(), wasPlaying = atOverride == null && !this.video.paused;
    this.saveNow();
    this.stream.stop();                                // ends the current stream (direct listeners or the server-side session)
    this.session = null; this.hls = null; this.serverInfo = null;
    this.video.removeAttribute('src'); this.video.load();
    await sleep(80);                                   // let the element flush events queued by clearing the source
    this.setBusy(true);
    if (notice) toast(notice);
    try {
      await (this.quality === 'original' ? this.startPlayback(at) : this.startHls(at, false));
      if (wasPlaying) this.video.play().catch(() => {});
    } catch (e) { this.report('switch-failed', e.message); this.fail(e.message); }
  }

  // A copied stream grows as the encoder works, so a resume point may not exist yet: seek once it does.
  seekWhenReady(t) {
    let n = 0;
    const timer = setInterval(() => {
      n++;
      const r = this.video.seekable;
      if (r && r.length && r.end(r.length - 1) >= t + 2) { clearInterval(timer); this.video.currentTime = t; }
      else if (n > 240) clearInterval(timer);          // about two minutes: give up and play from the start
    }, 500);
    this.stream.onStop(() => clearInterval(timer));
  }

  setMode(mode, reason) { this.mode = mode; this.reason = reason || ''; }

  // Cheapest path first: direct play, then direct stream (video copied), then a transcode. Each step is also the fallback for the one before it,
  // in case a browser claims support it doesn't have.
  async startPlayback(start) {
    const f = this.f, v = this.video;
    // Another audio track, or subtitles drawn onto the picture, can't be done by the browser alone: the server prepares a stream.
    if (f.mode !== 'direct' || this.extra.audioIndex != null || this.extra.burnSubtitle != null) { await this.startHls(start, false); return; }
    this.setMode('direct', f.reason);
    let fellBack = false;
    const fallBack = (why) => {
      if (fellBack) return;
      fellBack = true;
      this.setBusy(true);
      this.startHls(v.currentTime || start, false).catch((e) => this.fail(`${why}. ${e.message}`));
    };
    // Clearing src (when switching) queues an "empty src" error event: only a failure if a source is actually loaded.
    const onError = () => { if (v.currentSrc) { this.report('direct-error', 'media error during direct play'); fallBack('Direct play failed'); } };
    const onMeta = () => { if (start > 1 && start < (v.duration || Infinity) - 5) v.currentTime = start; };
    watchDecoding(this.video, this.stream, () => { this.report('video-not-decoded', 'direct play: audio plays but no video frames'); fallBack('This browser couldn’t decode the video'); });
    v.addEventListener('error', onError);
    v.addEventListener('loadedmetadata', onMeta, { once: true });
    this.stream.onStop(() => { v.removeEventListener('error', onError); v.removeEventListener('loadedmetadata', onMeta); });
    v.src = f.streamUrl;
  }

  async startHls(resumeAt, forceTranscode) {
    const f = this.f, v = this.video;
    const res = await fetch('/api/hls/start', {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ mediaId: f.mediaId, video: CAPS.v, audio: CAPS.a, containers: CAPS.c, forceTranscode, startSeconds: resumeAt > 1 ? resumeAt : 0,
        quality: this.quality !== 'original' ? this.quality : null, audioIndex: this.extra.audioIndex ?? null, burnSubtitle: this.extra.burnSubtitle ?? null }),
    });
    if (!res.ok) {
      let detail = ''; try { detail = (await res.json()).detail || ''; } catch { /* not json */ }
      throw new Error(`The server couldn’t start the stream (${res.status}). ${detail}`.trim());
    }
    const s = await res.json();
    const base = `/api/hls/${s.sessionId}`;
    this.session = { sessionId: s.sessionId, base, mode: s.mode, reason: s.reason, vod: s.vod };
    this.setMode(s.mode, s.reason || (s.mode === 'remux' ? 'Direct stream (video untouched)' : 'Transcode'));

    // Everything below is registered for cleanup immediately, so no path can leak the server-side ffmpeg.
    const stopSession = () => { try { navigator.sendBeacon(`${base}/stop`); } catch { /* ignore */ } };
    const ping = setInterval(() => fetch(`${base}/ping`, { method: 'POST' }).catch(() => {}), 15000);
    window.addEventListener('pagehide', stopSession);
    let hls = null, retried = false;
    // A copied stream that the browser then can't decode is retried once as a real transcode.
    const retryAsTranscode = (why) => {
      const canRetry = !forceTranscode && !retried && s.mode === 'remux';
      this.report(canRetry ? 'retry-as-transcode' : 'playback-error', why);
      if (!canRetry) { this.fail(`Playback error: ${why}`); return; }
      retried = true;
      const at = v.currentTime || resumeAt;
      this.stream.stop();
      this.startHls(at, true).catch((e) => this.fail(e.message));
    };
    const onMeta = () => { if (resumeAt > 1 && s.vod && !hls) v.currentTime = resumeAt; };   // native HLS (hls.js uses startPosition)
    if (s.mode === 'remux') watchDecoding(v, this.stream, () => retryAsTranscode('the video couldn’t be decoded'));
    const onNativeError = () => { if (v.currentSrc) retryAsTranscode('the stream could not be played (native HLS)'); };
    v.addEventListener('loadedmetadata', onMeta, { once: true });
    this.stream.onStop(() => {
      clearInterval(ping);
      window.removeEventListener('pagehide', stopSession);
      v.removeEventListener('loadedmetadata', onMeta); v.removeEventListener('error', onNativeError);
      if (hls) hls.destroy();
      if (this.hls === hls) this.hls = null;
      fetch(`${base}/stop`, { method: 'POST', keepalive: true }).catch(() => {});
    });

    // Safari plays HLS (including HEVC in fragmented MP4) natively; its MSE path doesn't handle that reliably, so prefer native there.
    const nativeHls = !!v.canPlayType('application/vnd.apple.mpegurl');
    if (nativeHls && (IS_SAFARI || !(window.Hls && Hls.isSupported()))) {
      v.addEventListener('error', onNativeError);
      v.src = s.playlistUrl;
    } else if (window.Hls && Hls.isSupported()) {
      // A far seek makes the server restart its encoder there, so a segment can legitimately take a while: don't time out early.
      const slow = { default: { maxTimeToFirstByteMs: 60000, maxLoadTimeMs: 120000, timeoutRetry: { maxNumRetry: 2, retryDelayMs: 0, maxRetryDelayMs: 0 }, errorRetry: { maxNumRetry: 3, retryDelayMs: 500, maxRetryDelayMs: 4000 } } };
      hls = new Hls({ startPosition: resumeAt > 1 && s.vod ? resumeAt : -1, fragLoadPolicy: slow });
      hls.on(Hls.Events.ERROR, (_e, d) => { if (d.fatal) retryAsTranscode(`${d.type}/${d.details}${d.response ? ' http ' + d.response.code : ''}`); });
      hls.loadSource(s.playlistUrl);
      hls.attachMedia(v);
      this.hls = hls;
    } else {
      throw new Error('This browser can’t play this kind of stream.');
    }
    if (resumeAt > 1 && !s.vod) this.seekWhenReady(resumeAt);
  }

  // ------------------------------------------------------------------ casting
  // The server does the casting: it finds Chromecasts on the network, starts a stream the TV can play and tells the TV to fetch it. This page is
  // then only a remote control, so it works in any browser, needs no secure page, and the TV keeps playing if the tab is closed.
  // Safari's own AirPlay picker is offered alongside.
  cast() { if (this.castSession) this.castPanel(); else this.castPicker(); }

  castDialog(title, ...kids) {
    this.closeCastDialog();
    const dlg = h('div', { class: 'dlg-backdrop', onclick: (e) => { if (e.target === dlg) dlg.remove(); } },
      h('div', { class: 'dlg', role: 'dialog', 'aria-modal': 'true', 'aria-label': title }, h('h2', {}, title), ...kids));
    this.stage.append(dlg);                                        // inside the player, so it is visible in full screen too
    this.castDlg = dlg;
    return dlg;
  }
  closeCastDialog() { if (this.castDlg) { this.castDlg.remove(); this.castDlg = null; } }

  castPicker() {
    const v = this.video;
    const list = h('div', { class: 'dlg-list' });
    const row = (title, sub, onclick) => h('button', { class: 'hit', type: 'button', onclick }, h('div', { class: 'hit-text' }, h('strong', {}, title), sub ? h('small', {}, sub) : null));
    const look = async (refresh) => {
      list.replaceChildren(h('p', { class: 'muted' }, 'Looking for TVs…'));
      let devices;
      try {
        const r = await fetch(`/api/cast/devices${refresh ? '?refresh=1' : ''}`);
        if (!r.ok) throw new Error('failed');
        devices = (await r.json()).devices || [];
      } catch { list.replaceChildren(h('p', {}, 'The server couldn’t look for TVs.')); return; }
      const rows = [];
      if (v.webkitShowPlaybackTargetPicker) rows.push(row('AirPlay', 'Apple TV, AirPlay speakers and TVs', () => { this.closeCastDialog(); v.webkitShowPlaybackTargetPicker(); }));
      for (const d of devices) rows.push(row(d.name, d.busy ? 'Playing something now' : d.model !== d.name ? d.model : '', () => this.castTo(d, list)));
      if (!devices.length) rows.push(h('p', { class: 'muted' }, 'No TVs found. They must be on the same network as the server, and the server has to be able to see them: in Docker, use host networking, or list the TV’s address in Settings → Network & security.'));
      list.replaceChildren(...rows);
    };
    this.castDialog('Play on…', list, h('div', { class: 'dlg-actions' },
      h('button', { class: 'btn glass', type: 'button', onclick: () => look(true) }, 'Look again'),
      h('button', { class: 'btn glass', type: 'button', onclick: () => this.closeCastDialog() }, 'Close')));
    look(false);
  }

  async castTo(device, list) {
    list.replaceChildren(h('p', {}, `Starting on ${device.name}…`));
    try {
      const at = Math.floor(this.position());
      const res = await fetch('/api/cast/play', { method: 'POST', headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ deviceId: device.id, mediaId: this.f.mediaId, title: this.f.title, startSeconds: at, audioIndex: this.extra.audioIndex ?? null, caption: this.captionActive || null }) });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(data.detail || data.error || 'The TV wouldn’t start.');
      this.closeCastDialog();
      this.stream.stop();                                          // the TV has its own stream; this page stops playing locally
      this.session = null; this.hls = null;
      this.video.pause(); this.video.removeAttribute('src'); this.video.load();
      this.setBusy(false);
      this.attachCast(data);
    } catch (e) {
      list.replaceChildren(h('p', {}, e.message), h('button', { class: 'btn glass', type: 'button', onclick: () => { this.closeCastDialog(); this.castPicker(); } }, 'Choose another TV'));
    }
  }

  // Take over as the remote for a cast the server is running (just started here, or already running when this page opened).
  attachCast(info) {
    this.castSession = { deviceId: info.deviceId, name: info.deviceName };
    this.castTime = info.position || 0;
    this.applyCastInfo(info);
    this.castBtn.classList.add('on');
    this.castBanner.classList.remove('hidden');
    clearInterval(this.timers.cast);
    this.timers.cast = setInterval(() => this.pollCast(), 1000);
  }

  applyCastInfo(info) {
    const c = this.castSession;
    Object.assign(c, { state: info.state, captions: info.captions || [], activeCaption: info.activeCaption || '', volume: info.volume, muted: info.muted });
    this.castTime = info.position || 0;
    this.swapIcon(this.playBtn, info.state === 'PLAYING' || info.state === 'BUFFERING' ? 'pause' : 'play');
    this.swapIcon(this.muteBtn, info.muted || info.volume === 0 ? 'mute' : 'volume');
    this.vol.value = info.muted ? '0' : String(info.volume ?? 1);
    this.castBanner.replaceChildren(h('strong', {}, info.state === 'LOADING' || info.state === 'BUFFERING' ? `Starting on ${c.name}…` : `Playing on ${c.name}`),
      h('button', { class: 'btn glass small', type: 'button', onclick: () => this.stopCasting() }, 'Stop casting'));
    this.paint();
  }

  async pollCast() {
    const c = this.castSession;
    if (!c || this.polling) return;
    this.polling = true;
    try {
      const r = await fetch('/api/cast/sessions');
      if (!r.ok) return;
      const mine = ((await r.json()).sessions || []).find((x) => x.deviceId === c.deviceId && x.mediaId === this.f.mediaId);
      if (!this.castSession) return;
      if (mine) this.applyCastInfo(mine); else this.castEnded();
    } catch { /* a missed poll is fine */ } finally { this.polling = false; }
  }

  async castCmd(action, value, text) {
    const c = this.castSession;
    if (!c) return;
    try {
      const r = await fetch(`/api/cast/${c.deviceId}/control`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ action, value, text }) });
      if (!r.ok) { if (r.status === 404) this.castEnded(); else toast((await r.json().catch(() => ({}))).detail || 'The TV didn’t respond', true); }
    } catch { toast('The TV didn’t respond', true); }
  }

  castPanel() {
    const c = this.castSession;
    const sel = h('select', { class: 'dlg-input', 'aria-label': 'Subtitles', onchange: () => this.castCmd('captions', null, sel.value) },
      h('option', { value: '' }, 'Subtitles off'), ...c.captions.map((t) => h('option', { value: t.id, selected: t.id === c.activeCaption }, t.label)));
    this.castDialog(`Playing on ${c.name}`, ...(c.captions.length ? [sel] : []),
      h('div', { class: 'dlg-actions' },
        h('button', { class: 'btn glass', type: 'button', onclick: () => { this.closeCastDialog(); this.castPicker(); } }, 'Switch TV'),
        h('button', { class: 'btn', type: 'button', onclick: () => { this.closeCastDialog(); this.stopCasting(); } }, 'Stop casting'),
        h('button', { class: 'btn glass', type: 'button', onclick: () => this.closeCastDialog() }, 'Close')));
  }

  // The TV stopped (finished, or someone pressed stop): come back to playing here, from where it got to.
  castEnded() { this.leaveCasting(false); }

  stopCasting() { this.leaveCasting(true); }

  leaveCasting(tellServer) {
    const c = this.castSession;
    if (!c) return;
    this.castSession = null;
    clearInterval(this.timers.cast);
    const at = this.castTime;
    if (tellServer) fetch(`/api/cast/${c.deviceId}/stop`, { method: 'POST', keepalive: true }).catch(() => {});
    this.castBtn.classList.remove('on'); this.castBanner.classList.add('hidden');
    this.swapIcon(this.playBtn, 'play');
    if (this.disposed) return;
    const finished = this.total() > 0 && at >= this.total() - 5;
    if (finished) { this.onEnded(); return; }
    this.reload({}, null, at);
  }

  setupAirPlayState() {
    const v = this.video;
    if (!('onwebkitcurrentplaybacktargetiswirelesschanged' in v)) return;
    this.on(v, 'webkitcurrentplaybacktargetiswirelesschanged', () => this.castBtn.classList.toggle('on', !!v.webkitCurrentPlaybackTargetIsWireless));
  }

  // If the server is already casting this title (started earlier, from this or another device), pick up the remote.
  async resumeCasting() {
    try {
      const r = await fetch('/api/cast/sessions');
      if (!r.ok || this.disposed || this.castSession) return !!this.castSession;
      const mine = ((await r.json()).sessions || []).find((x) => x.mediaId === this.f.mediaId);
      if (!mine) return false;
      this.stream.stop(); this.session = null; this.hls = null;
      this.video.pause(); this.video.removeAttribute('src'); this.video.load();
      this.setBusy(false);
      this.attachCast(mine);
      return true;
    } catch { return false; /* not casting, or the server is old: play here */ }
  }

  // ------------------------------------------------------------------ teardown
  dispose() {
    this.disposed = true;
    Object.values(this.timers).forEach((t) => { clearTimeout(t); clearInterval(t); });
    this.disposers.forEach((fn) => fn());
    this.saveNow();                                    // the final position
    // a cast carries on without this page: the server keeps it going and saves the progress
    try { this.video.pause(); } catch { /* ignore */ }
    this.stream.stop();                                // ends the HLS session, so the server kills ffmpeg
    this.video.removeAttribute('src');
    this.video.load();                                 // aborts the in-flight media request so the server stops streaming
  }
}

const browserName = () => { const u = navigator.userAgent; return /Edg\//.test(u) ? 'Edge' : /Chrome\//.test(u) ? 'Chrome' : /Firefox\//.test(u) ? 'Firefox' : /Safari\//.test(u) ? 'Safari' : 'Browser'; };

// ================================================================ entry point (the #/watch/<id> page)
async function showPlayer(id, params) {
  const token = playerToken;
  const f = await api(`/api/media/${encodeURIComponent(id)}/play?${capsQuery()}`);
  if (token !== playerToken) return;                   // navigated away while loading
  const player = new Player(f, params);
  activePlayback = { dispose: () => player.dispose() };
  await player.start();
}
