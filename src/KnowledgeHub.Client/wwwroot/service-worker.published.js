// SPEC-20260918-ui-layout-polish RF-007 — published PWA worker.
// Precaches the app shell so the WASM UI launches offline; API traffic is
// always network-only (authenticated data must never be served from cache).
// The custom boot chain in js/boot.js still works: its /framework-assets
// mirrors fail offline and fall back to /_framework/*, which is precached.
self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'knowledgehub-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
// Side cache outside the evicted prefix — records the previous generation.
const metaCacheName = 'knowledgehub-meta';
const previousVersionKey = '/previous-version';
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/];
const offlineAssetsExclude = [/^service-worker\.js$/];

// Network-only surfaces: never intercept, never cache.
const networkOnlyPaths = [
    /^\/api(\/|$)/,
    /^\/hubs(\/|$)/,
    /^\/mcp(\/|$)/,
    /^\/health(\/|$)/,
    /^\/framework-assets(\/|$)/
];

async function onInstall(event) {
    const assets = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)));
    const assetsRequests = assets
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    const cache = await caches.open(cacheName);
    await cache.addAll(assetsRequests);

    // _framework assets are fingerprinted (dotnet.{fp}.js) but the boot chain
    // may request canonical names (dotnet.js) — MapStaticAssets serves both.
    // Precache canonical aliases so the offline boot resolves either way.
    const known = new Set(assets.map(a => a.url));
    const canonicalAliases = assets
        .map(a => a.url)
        .filter(u => u.startsWith('_framework/'))
        .map(u => u.replace(/\.([a-z0-9]{10})\.([^.]+)$/, '.$2'))
        .filter((u, i, all) => all.indexOf(u) === i && !known.has(u));
    await Promise.all(canonicalAliases.map(async u => {
        try {
            const response = await fetch(u, { cache: 'no-cache' });
            if (response.ok) await cache.put(u, response);
        } catch { /* alias not served — fingerprinted copy already cached */ }
    }));
}

async function onActivate(event) {
    // SPEC-20260930-pwa-stale-cache-eviction: keep the current AND the
    // immediately previous cache generation. Tabs still running the old app
    // version keep resolving their cached assets until they reload — deleting
    // every prior cache on activate made those tabs fail with 404s whenever
    // the server no longer serves their (renamed) boot assets. The previous
    // generation is recorded in a side meta-cache because manifest version
    // suffixes are content hashes, not ordered timestamps.
    const meta = await caches.open(metaCacheName);
    const prevResponse = await meta.match(previousVersionKey);
    const previous = prevResponse ? await prevResponse.text() : null;

    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix)
            && key !== cacheName && key !== previous)
        .map(key => caches.delete(key)));

    await meta.put(previousVersionKey, new Response(cacheName));
}

async function onFetch(event) {
    if (event.request.method !== 'GET') {
        return fetch(event.request);
    }

    const url = new URL(event.request.url);
    if (url.origin !== location.origin || networkOnlyPaths.some(p => p.test(url.pathname))) {
        return fetch(event.request);
    }

    // Deep links (/sources, /chat, ...) resolve to the cached app shell.
    const shouldServeIndexHtml = event.request.mode === 'navigate'
        && !url.pathname.substring(url.pathname.lastIndexOf('/')).includes('.');
    const request = shouldServeIndexHtml ? 'index.html' : event.request;

    const cache = await caches.open(cacheName);
    const cachedResponse = await cache.match(request, { ignoreSearch: shouldServeIndexHtml });
    if (cachedResponse) {
        return cachedResponse;
    }

    const response = await fetch(event.request);
    if (response.ok && !shouldServeIndexHtml) {
        await cache.put(request, response.clone());
    }
    return response;
}
