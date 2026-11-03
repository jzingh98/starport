# Godot Web bridge isolation spike

The original [cloud save demo](../examples/cloud-save-demo/README.md) was exported
with Godot 4.7.2 stable and its matching official `web_nothreads` templates. The
templates came from the [official Godot 4.7.2 release](https://godotengine.org/download/archive/4.7.2-stable/).
The export uses the Compatibility renderer, single-threaded Web template, no
GDExtension/C# and no PWA. The generated output contained `index.html`, `.js`,
`.wasm`, `.pck` and audio worklet files. The project pack was about 8 KiB; the
engine WebAssembly file was about 38 MiB. The package step added
`starport-bridge.v1.js` and `starport-game.json`.

## Local browser evidence

Two HTTPS servers served the host at `https://localhost:8443` and the exported
game at `https://127.0.0.1:8444`. Both used a temporary self-signed certificate.
The game ran inside the host's sandboxed cross-site iframe. Playwright WebKit 26
rendered the real Godot canvas and completed `loadSave` on launch. Clicking the
Godot button saved the counter through the bridge; reloading the iframe restored
one star from the mock save. Opening two host tabs, then saving in one and
autosaving from a stale revision in the other produced `conflict` in the host and
the game's explicit conflict choice. Invalidation made a later bridge call fail
with `unavailable`. Browser evidence also showed cross-origin parent DOM access
throwing `SecurityError`, a cross-origin read failing with `TypeError`, and the
game bridge rejecting an unknown `getToken` method.

For the third-party-cookie check, a `SameSite=None; Secure` probe cookie for
`127.0.0.1` was visible in a first-party WebKit tab and absent from the game
iframe. The cloud save flow above still worked in that browser context. This
proves the sample bridge does not require a game-frame cookie in this setup.

The proposed hosted topology uses same-site subdomains. A second WebKit HTTPS
test used `app.lvh.me:8443` and `game.lvh.me:8444`, which resolve to loopback but
share one registrable domain. A host-only `SameSite=Lax; Secure` cookie for the
game host was visible inside that iframe, and the Godot canvas rendered. This
supports cookie-backed *asset delivery* on same-site subdomains while cloud
saves still use the parent bridge. Account cookies must remain host-only so the
game host never receives them.

The JavaScript bridge has four Node protocol tests for exact origin/source/nonce
replies, method allowlisting, invalidation and bootstrap redirect state. Godot editor import,
headless scene run, real Web export, packaging idempotency and host JavaScript
syntax checks passed. Chrome also rendered the export, but its DevTools cookie
restriction control did not hide the probe cookie in this local setup, so WebKit
provides the cookie-blocking evidence.

An Angular player-page mock launch also ran in WebKit. The app received a mocked
`POST /api/games/{id}/launch` response, validated the content origin, appended
bridge origin/nonce beside the opaque fragment ticket, and loaded the real Godot
export in its iframe. The game `loadSave` message reached an app-origin GET save
request. Clicking the Godot autosave button caused an app-origin PUT with
`If-None-Match: *` and displayed the returned revision in the game. No page
JavaScript errors were observed. This validates the SPA-to-SDK wiring, not the
live Accounts/OIDC or Starport save backend.

## Actual Starport delivery smoke

On November 2, 2026, the real Godot 4.7.2 Web export was also uploaded through
the development Starport API, processed to an immutable release, and launched
through its one-use ticket and game-host session cookie. This exercised the
application player, `.NET` delivery endpoints, PostgreSQL records, local release
storage, Godot runtime, JavaScript bridge and cloud-save API together.

The browser test used an HTTPS reverse proxy with the Angular build and API at
`https://app.lvh.me:8443` and the isolated game at
`https://g-<game-id>.games.lvh.me:8444`. Both names resolve to loopback and are
same-site. A temporary certificate covering those names was accepted only in
the test browser context; system trust and production headers were untouched.
To repeat the topology, proxy the app host's `/api` requests to the local API
and its other paths to Angular; proxy the game host to the same API while
preserving the requested `Host`. The API used these development overrides:

```text
GameDelivery__Scheme=https
GameDelivery__Port=8444
GameDelivery__RootDomain=games.lvh.me
GameDelivery__AppOrigin=https://app.lvh.me:8443
```

The 10.2 MB ZIP included the current `starport-bridge.v1.js` copied by
`examples/cloud-save-demo/package.mjs`. Refreshing that copy matters: an older
local export lacked the session-storage fallback used after ticket redemption.
The processor marked the upload Ready. The browser then received `200` for
`index.html`, `index.js`, `index.pck` (`application/octet-stream`) and the
39,514,754-byte `index.wasm` (`application/wasm`), and rendered the Godot canvas.
When the game was published to the People audience, adding a star sent a save
`PUT` on the application origin and returned `200`. After the game was taken
offline, its publisher preview loaded the existing save and sent another `PUT`
with `?preview=true`; both returned `200`. The canvas displayed two saved stars
and no JavaScript errors. The test game and its files were then removed.

## Limits and next production test

The earlier mock-host spike used browser local storage; the later API smoke
covered the local launch, delivery and save components. Production validation
still needs the hosted domains, GCS, the Kubernetes Job, OIDC and a game
package from an untrusted creator. The staging test must check access again
for every bridge request and artifact fetch, including entitlement expiry,
unpublication, moderation, account switching and provider outages. The
[official Godot Web export guide](https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_web.html)
describes the browser requirements; the
[JavaScriptBridge API](https://docs.godotengine.org/en/stable/classes/class_javascriptbridge.html)
documents the Web-only Godot interop used by the addon.
