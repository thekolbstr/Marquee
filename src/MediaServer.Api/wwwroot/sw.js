// Service worker: makes the app installable and keeps the page shell available offline.
// It never touches the API, streams or images of your library: those always go to the network.
const CACHE = 'shell-v2';
const SHELL = ['/', '/style.css', '/live.css', '/shared.js', '/app.js', '/auth.js', '/settings.js', '/extras.js', '/player.js', '/live.js', '/vendor/hls.min.js', '/vendor/qrcode.js'];

self.addEventListener('install', (e) => { e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting())); });

self.addEventListener('activate', (e) => {
  e.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))).then(() => self.clients.claim()));
});

self.addEventListener('fetch', (e) => {
  const req = e.request, url = new URL(req.url);
  if (req.method !== 'GET' || url.origin !== location.origin) return;
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/stream/') || url.pathname === '/health') return;   // live data: never cached
  // Network first, so a new version of the app is picked up straight away; the cache only answers when the server can't be reached.
  e.respondWith(fetch(req).then((res) => {
    if (res.ok && (SHELL.includes(url.pathname) || url.pathname.startsWith('/icons/'))) { const copy = res.clone(); caches.open(CACHE).then((c) => c.put(req, copy)); }
    return res;
  }).catch(() => caches.match(req).then((hit) => hit || (req.mode === 'navigate' ? caches.match('/') : Response.error()))));
});
