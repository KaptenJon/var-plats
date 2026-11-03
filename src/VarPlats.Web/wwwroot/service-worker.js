// Network only: no tile prefetch, no API or user-content caching.
// Blazor Server requires connectivity; installing this PWA does not enable offline maps.
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));
