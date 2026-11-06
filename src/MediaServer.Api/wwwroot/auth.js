'use strict';

// Sign-in, first-run setup and the welcome wizard. Functions only: loaded before app.js, which supplies h/api/store/toast/icon at call time.

const AUTH = { enabled: true, user: null, name: null, displayName: null, role: 'admin', locked: false };
const isAdmin = () => !AUTH.enabled || AUTH.role === 'admin';
const greetingName = () => AUTH.name || AUTH.user || '';
// The little round badge in the header: your initial, linking to your account.
function paintUserChip() {
  const old = document.getElementById('userchip');
  if (old) old.remove();
  if (!AUTH.enabled || !AUTH.user) return;
  const chip = h('a', { id: 'userchip', class: 'icon-btn userchip', href: '#/settings/account', title: `${greetingName()} · account`, 'aria-label': `Account: ${greetingName()}` }, greetingName().trim().charAt(0).toUpperCase() || '?');
  const gear = document.querySelector('#top a[href="#/settings"]');
  if (gear) gear.after(chip);
}
// What the server says about itself (name, whether transcoding is on). Filled in at start-up.
const APP = { name: 'MediaServer', allowTranscoding: true, httpsPort: null, castBaseUrl: null };
async function refreshApp() {
  try { const a = await (await fetch('/api/app')).json(); applyServerName(a.serverName); APP.allowTranscoding = a.allowTranscoding; APP.httpsPort = a.httpsPort; APP.castBaseUrl = a.castBaseUrl; } catch { /* keep what we had */ }
}
function applyServerName(name) {
  APP.name = name || 'MediaServer';
  document.querySelectorAll('.brand-name').forEach((e) => { e.textContent = APP.name; });
  document.title = APP.name;
}

// ================================================================ the full-screen forms
function authShell(...kids) {
  document.body.classList.add('auth-screen', 'auth-on');
  document.title = APP.name;
  view.replaceChildren(h('div', { class: 'auth-wrap' },
    h('div', { class: 'auth-brand' }, h('span', { class: 'logo big', 'aria-hidden': 'true' }), h('span', {}, APP.name)), ...kids));
  window.scrollTo(0, 0);
}

function passwordField(label, { id, autocomplete, onEnter } = {}) {
  const input = h('input', { id, class: 'auth-input', type: 'password', autocomplete: autocomplete || 'current-password', 'aria-label': label, placeholder: label, required: true, maxlength: '200' });
  const eye = h('button', { class: 'eye', type: 'button', title: 'Show password', 'aria-label': 'Show password', onclick: () => {
    const show = input.type === 'password'; input.type = show ? 'text' : 'password'; eye.setAttribute('aria-pressed', String(show)); eye.classList.toggle('on', show);
  } }, icon('eye'));
  if (onEnter) input.addEventListener('keydown', (e) => { if (e.key === 'Enter') onEnter(); });
  return { input, el: h('div', { class: 'pw' }, input, eye) };
}

async function postJson(url, body) {
  const res = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  let json = {};
  try { json = await res.json(); } catch { /* no body */ }
  if (res.status === 429) { const wait = parseInt(res.headers.get('retry-after') || '0', 10); json.error = `${json.error || 'Too many attempts.'}${wait ? ` Try again in ${wait > 90 ? `${Math.ceil(wait / 60)} minutes` : `${wait} seconds`}.` : ''}`; }
  return { ok: res.ok, status: res.status, json };
}

function showLogin(onDone, notice) {
  const user = h('input', { class: 'auth-input', type: 'text', autocomplete: 'username', autocapitalize: 'none', spellcheck: 'false', 'aria-label': 'Username', placeholder: 'Username', required: true, maxlength: '64' });
  const pw = passwordField('Password', { onEnter: () => submit() });
  const remember = h('input', { type: 'checkbox', checked: true });
  const err = h('p', { class: 'auth-err hidden', role: 'alert' });
  const btn = h('button', { class: 'btn auth-btn', type: 'submit' }, 'Sign in');
  const form = h('form', { class: 'auth-card', novalidate: true },
    h('h1', {}, 'Welcome back'), h('p', { class: 'auth-sub' }, notice || 'Sign in to continue.'),
    user, pw.el, h('label', { class: 'auth-check' }, remember, 'Keep me signed in'), err, btn);
  async function submit() {
    err.classList.add('hidden');
    if (!user.value.trim() || !pw.input.value) { err.textContent = 'Enter your username and password.'; err.classList.remove('hidden'); return; }
    btn.disabled = true; btn.textContent = 'Signing in…';
    try {
      const r = await postJson('/api/auth/login', { username: user.value.trim(), password: pw.input.value, remember: remember.checked });
      if (!r.ok) { err.textContent = r.json.error || 'Could not sign in.'; err.classList.remove('hidden'); pw.input.value = ''; pw.input.focus(); return; }
      if (r.json.twoFactorRequired) { showSecondStep(r.json.ticket, remember.checked, onDone); return; }   // right password: now the code
      await finishSignIn(r.json, onDone);
    } catch { err.textContent = 'Could not reach the server.'; err.classList.remove('hidden'); }
    finally { btn.disabled = false; btn.textContent = 'Sign in'; }
  }
  form.addEventListener('submit', (e) => { e.preventDefault(); submit(); });
  authShell(form);
  user.focus();
}

// After a successful sign-in: learn who we are (name, role) and carry on.
async function finishSignIn(json, onDone) {
  AUTH.user = json.user; AUTH.name = json.name; AUTH.locked = false;
  try { const st = await (await fetch('/api/auth/state', { cache: 'no-store' })).json(); AUTH.role = st.role || 'user'; AUTH.displayName = st.displayName; AUTH.name = st.name; } catch { /* keep what we have */ }
  paintUserChip();
  onDone();
}

// The second sign-in step: a code from the authenticator app, or one of the recovery codes.
function showSecondStep(ticket, remember, onDone) {
  const code = h('input', { class: 'auth-input code', type: 'text', inputmode: 'text', autocomplete: 'one-time-code', autocapitalize: 'none', spellcheck: 'false', 'aria-label': 'Code', placeholder: '123 456', maxlength: '24' });
  const err = h('p', { class: 'auth-err hidden', role: 'alert' });
  const btn = h('button', { class: 'btn auth-btn', type: 'submit' }, 'Verify');
  const form = h('form', { class: 'auth-card', novalidate: true },
    h('h1', {}, 'Two-step sign-in'), h('p', { class: 'auth-sub' }, 'Enter the 6-digit code from your authenticator app. Lost your phone? Use one of your recovery codes instead.'),
    code, err, btn, h('a', { class: 'auth-fine', href: '#', onclick: (e) => { e.preventDefault(); location.reload(); } }, '← Start over'));
  form.addEventListener('submit', async (e) => {
    e.preventDefault(); err.classList.add('hidden');
    btn.disabled = true;
    try {
      const r = await postJson('/api/auth/login/2fa', { ticket, code: code.value.trim(), remember });
      if (!r.ok) { err.textContent = r.json.error || 'That code isn’t right.'; err.classList.remove('hidden'); code.select(); if (r.status === 401 && /expired/i.test(r.json.error || '')) setTimeout(() => location.reload(), 1800); return; }
      await finishSignIn(r.json, onDone);
    } catch { err.textContent = 'Could not reach the server.'; err.classList.remove('hidden'); }
    finally { btn.disabled = false; }
  });
  authShell(form);
  code.focus();
}

function showSetup(state, onDone) {
  const user = h('input', { class: 'auth-input', type: 'text', autocomplete: 'username', autocapitalize: 'none', spellcheck: 'false', 'aria-label': 'Username', placeholder: 'Choose a username', required: true, maxlength: '32' });
  const display = h('input', { class: 'auth-input', type: 'text', autocomplete: 'nickname', 'aria-label': 'Your name', placeholder: 'What should we call you? (optional)', maxlength: '40' });
  const pw = passwordField('Choose a password (8+ characters)', { autocomplete: 'new-password' });
  const pw2 = passwordField('Repeat the password', { autocomplete: 'new-password' });
  const code = state.setupCodeRequired ? h('input', { class: 'auth-input', type: 'text', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Setup code', placeholder: 'Setup code from the server settings' }) : null;
  const meter = h('div', { class: 'meter', 'aria-hidden': 'true' }, h('i'));
  const err = h('p', { class: 'auth-err hidden', role: 'alert' });
  const btn = h('button', { class: 'btn auth-btn', type: 'submit' }, 'Create account');
  pw.input.addEventListener('input', () => {
    const v = pw.input.value; let score = 0;
    if (v.length >= 8) score++; if (v.length >= 12) score++; if (/[A-Z]/.test(v) && /[a-z]/.test(v)) score++; if (/\d/.test(v) || /[^A-Za-z0-9]/.test(v)) score++;
    meter.dataset.score = String(v ? Math.max(1, score) : 0);
  });
  const form = h('form', { class: 'auth-card', novalidate: true },
    h('h1', {}, 'Welcome'), h('p', { class: 'auth-sub' }, 'Let’s set up your server. Create the account you’ll use to sign in. You can add your media folders next.'),
    user, display, pw.el, meter, pw2.el, code, err, btn,
    h('p', { class: 'auth-fine' }, 'This account has full control of the server, including its settings and folders. Keep the password somewhere safe.'));
  form.addEventListener('submit', async (e) => {
    e.preventDefault(); err.classList.add('hidden');
    const fail = (m) => { err.textContent = m; err.classList.remove('hidden'); };
    if (pw.input.value !== pw2.input.value) { fail('The two passwords don’t match.'); return; }
    btn.disabled = true; btn.textContent = 'Creating…';
    try {
      const r = await postJson('/api/auth/setup', { username: user.value.trim(), password: pw.input.value, setupCode: code ? code.value.trim() : undefined, displayName: display.value.trim() || undefined });
      if (!r.ok) { fail(r.json.error || 'Could not create the account.'); if (r.status === 409) setTimeout(() => location.reload(), 1500); return; }
      AUTH.user = r.json.user; AUTH.name = r.json.name; AUTH.displayName = display.value.trim() || null; AUTH.role = 'admin'; AUTH.locked = false;
      paintUserChip();
      onDone();
    } catch { fail('Could not reach the server.'); }
    finally { btn.disabled = false; btn.textContent = 'Create account'; }
  });
  authShell(form);
  user.focus();
}

// A request came back 401 (the session ended, or someone signed out elsewhere): show the sign-in form over whatever was open.
function authLost() {
  if (AUTH.locked) return;
  AUTH.locked = true;
  runLeave(); teardownPlayer();
  document.body.classList.remove('watching');
  showLogin(() => { AUTH.locked = false; document.body.classList.remove('auth-screen'); route(); loadLiveStatus(); }, 'Your session ended. Sign in again to continue.');
}

async function signOut() {
  try { await fetch('/api/auth/logout', { method: 'POST' }); } catch { /* signing out anyway */ }
  location.hash = '#/';
  location.reload();
}

// ================================================================ welcome wizard (right after the account is created)
async function showWelcome() {
  document.title = 'Welcome · ' + APP.name;
  let step = 0;
  const body = h('div', { class: 'wizard-body' });
  const dots = h('div', { class: 'wizard-dots' });
  const card = h('div', { class: 'wizard' }, dots, body);
  setView(h('div', { class: 'page' }, card));

  const next = () => { step++; draw(); };
  const nav = (...buttons) => h('div', { class: 'wizard-nav' }, ...buttons);

  async function draw() {
    dots.replaceChildren(...[0, 1, 2].map((i) => h('i', { class: i === step ? 'on' : i < step ? 'done' : '' })));
    body.replaceChildren();
    if (step === 0) {
      const holder = h('div');
      body.append(h('h1', {}, 'Where is your media?'),
        h('p', { class: 'hint' }, 'Add the folders that hold your movies and shows. The server only ever reads them: it never changes your files.'), holder,
        nav(h('span'), h('button', { class: 'btn', type: 'button', onclick: next }, 'Continue')));
      await folderManager(holder, { wizard: true });
    } else if (step === 1) {
      const token = h('input', { class: 'dlg-input', type: 'password', autocomplete: 'off', spellcheck: 'false', placeholder: 'Paste your TMDB read access token', 'aria-label': 'TMDB token' });
      const out = h('p', { class: 'save-status' });
      body.append(h('h1', {}, 'Posters and details'),
        h('p', { class: 'hint' }, 'Free TMDB credentials add posters, descriptions, ratings and episode details. Create an account at themoviedb.org, open Settings → API, and copy the “API Read Access Token”. You can skip this and add it later.'),
        h('div', { class: 'inline wide' }, token, h('button', { class: 'btn glass', type: 'button', onclick: async () => {
          if (!token.value.trim()) { out.textContent = 'Paste a token first, or skip this step.'; out.className = 'save-status bad'; return; }
          out.className = 'save-status'; out.textContent = 'Saving…';
          const put = await fetch('/api/settings', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ values: { 'Tmdb:ReadAccessToken': token.value.trim() } }) });
          if (!put.ok) { out.className = 'save-status bad'; out.textContent = (await put.json().catch(() => ({}))).error || 'Could not save that.'; return; }
          const t = await (await fetch('/api/settings/test/tmdb', { method: 'POST' })).json();
          out.textContent = t.message; out.className = 'save-status ' + (t.ok ? 'good' : 'bad');
          if (t.ok) setTimeout(next, 900);
        } }, 'Save & test')), out,
        nav(h('button', { class: 'btn glass', type: 'button', onclick: () => { step--; draw(); } }, 'Back'), h('button', { class: 'btn glass', type: 'button', onclick: next }, 'Skip for now')));
    } else {
      fetch('/api/library/scan?background=true', { method: 'POST' }).then(() => watchScan(false)).catch(() => {});
      body.append(h('h1', {}, 'You’re all set'),
        h('p', { class: 'hint' }, 'Your library is being scanned now. Titles appear as they’re found; the first scan of a big library can take a few minutes.'),
        h('ul', { class: 'wizard-tips' },
          h('li', {}, h('strong', {}, 'Settings'), ' (the gear icon) has everything else: Live TV, captions, appearance and your account.'),
          h('li', {}, h('strong', {}, 'Random movie / episode'), ' on the home page picks something for you.'),
          h('li', {}, 'Press ', h('kbd', {}, 'C'), ' in the player for captions.')),
        nav(h('span'), h('a', { class: 'btn', href: '#/' }, 'Go to my library')));
    }
  }
  draw();
}
