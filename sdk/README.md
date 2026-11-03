# Starport game bridge v1

`javascript/starport-bridge.v1.js` runs inside an untrusted Godot Web export. The
`godot/addons/starport/starport_cloud_save.gd` wrapper exposes `load_save`, `save`,
`list_slots`, `delete_save` and `capabilities` as asynchronous GDScript calls. Copy
both into a game; load the JavaScript file from the exported HTML before Godot starts.
The [cloud save demo](../examples/cloud-save-demo/README.md) does this in its packaging script.

The trusted player page opens the game iframe with a fragment containing
`starport_origin=<exact parent origin>` and `starport_nonce=<launch nonce>`, or the
delivery bootstrap stores those two routing values under
`starport.bridge.v1.origin` and `starport.bridge.v1.nonce` in the game frame's
session storage before redirecting to `index.html`. The game
bridge sends `{protocol:"starport.bridge",version:1,type:"request",id,method,payload,nonce}`
to that exact origin. Responses use the same protocol/version/nonce and the request
ID, with either `{ok:true,result}` or `{ok:false,error:{code,message}}`. A verified
`invalidate` message ends the session and fails pending calls. Each call times out
after 15 seconds. The game-side bridge limits requests and checks response origin and
source. These checks do **not** make the game trustworthy: the parent must validate
exact origin, `event.source`, current iframe, nonce, method, payload, access and save
revision for every request. The parent must use its own credentials; never send an
OIDC token, player ID or cross-game storage key into the frame.

Save payloads are JSON objects. `loadSave({slot})` returns a result with `data` and
`revision` (or null for a new slot). `save({slot,data,expectedRevision,requestKey})`
returns the new revision. An empty expected revision means create-only; a known
revision means compare-and-swap. `listSlots({})` returns slot metadata and
`deleteSave({slot,expectedRevision})` requires a revision. `capabilities({})` reports
session save availability. Conflict errors carry code `conflict`. Games should keep
unsaved local progress and offer an explicit choice on conflict.

This is protocol v1. Add a new versioned file for incompatible changes; do not
silently change v1 semantics. The bridge makes no direct API calls and does not
automatically synchronize Godot's `user://` filesystem.
