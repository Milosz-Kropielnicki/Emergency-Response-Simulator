// ERS field companion: keeps the app itself on the phone so it opens with no signal. The API is never cached.
const CACHE = "ers-field-v1";
const SHELL = ["./", "index.html", "field.css", "field.js", "manifest.webmanifest", "icon.svg"];

self.addEventListener("install", e => e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL))));
self.addEventListener("activate", e => e.waitUntil(
  caches.keys().then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))));
self.addEventListener("fetch", e => {
  if (e.request.method !== "GET" || e.request.url.includes("/api/")) return;
  e.respondWith(fetch(e.request).then(r => {
    const copy = r.clone();
    caches.open(CACHE).then(c => c.put(e.request, copy));
    return r;
  }).catch(() => caches.match(e.request)));
});
