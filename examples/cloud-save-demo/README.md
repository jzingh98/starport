# Cloud save demo

This original, asset-free Godot project is available under the repository's MIT
license. It adds stars to a counter, autosaves, loads after a browser restart and
shows a conflict when another tab changes the same slot. It offers explicit cloud
or new-slot choices; it never overwrites a conflict automatically.

## Export

Use Godot 4 with the Web export templates installed. The project uses the
Compatibility renderer, no C#, no threads, no GDExtension and no PWA/service worker.
The [official Web export guide](https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_web.html)
explains these browser requirements. Run from the repository root:

```sh
/path/to/Godot --headless --path examples/cloud-save-demo --editor --quit
mkdir -p examples/cloud-save-demo-build
/path/to/Godot --headless --path examples/cloud-save-demo --export-release Web ../cloud-save-demo-build/index.html
node examples/cloud-save-demo/package.mjs
cd examples/cloud-save-demo-build && zip -r ../cloud-save-demo.zip .
```

The packaging step copies the versioned JavaScript bridge, adds it before Godot
starts and writes `starport-game.json`. Inspect the ZIP root: it must contain the
manifest, `index.html`, the Godot `.js`, `.wasm`, `.pck`, and
`starport-bridge.v1.js`. Repackage after every export. The addon in `addons/`
is copied from `sdk/godot/`; keep them in sync when changing the SDK.

## Cross-origin browser test

Serve `test-host.html` on one localhost port and the exported build on another:

```sh
python3 -m http.server 8000 --directory examples/cloud-save-demo
python3 -m http.server 8001 --directory examples/cloud-save-demo-build
```

Open `http://localhost:8000/test-host.html` and click **Launch**. The host uses
an iframe and a random launch nonce. Save, reload the game, and open a second
host tab for revision conflicts. **Invalidate launch** must make later save calls
fail. Inspect the browser console/network panel for unexpected account/API
requests from the game frame. The mock host keeps saves in browser local storage
to support this test; it is not persistent server storage or real entitlements.

Production must serve the game from a separate registrable content domain, use
short-lived launch/session credentials, and recheck access on every bridge call.
This localhost test does not prove full production isolation or third-party-cookie
behavior. The bridge does not rely on cookies in the game frame.
