# Starport implementation contracts

These are the current repository contracts. Hosted billing/identity values live in the sibling
Accounts and Jagveer repositories. [Implementation and launch gates](implementation-plan.md)
distinguishes working local code from unverified hosted services.

## Ownership and entitlement

- `GameRecord.WorkspaceId` is the sole publishing owner. Mutations read the game first and check
  a path workspace ID only for consistency. Membership and entitlement are checked against that
  stored owner.
- `GameAccess.CanPublish` needs an active, unexpired owner entitlement and a matching
  Owner/Admin/Member. Viewer cannot publish. `PaidThrough` is exclusive, so equality with current
  time is expired.
- `GameAccess.CanPlay` requires a validated release, no moderation suspension, active owner
  entitlement, publication where applicable, and the chosen audience. Team Viewers may play Team
  games. A named-person grant binds a stable subject, not an email or guessed URL. Public games
  require approval of the exact active release digest and metadata revision.
- `IWorkspaceMemberships` and `IWorkspaceEntitlements` have local PostgreSQL adapters and a
  hosted Accounts adapter. Hosted checks use the Starport-scoped `starport-service` JWT and fail
  closed if Accounts is unavailable. Accounts' durable `serving_lost` event feed is consumed with
  a PostgreSQL cursor and atomically unpublishes games. Request-time access still rechecks current
  state.
- People grants may be created from an existing verified Accounts email. The stored subject is
  authoritative; the normalized email is only an editor-visible label. The local provider may
  map configured emails or accept direct subjects. Pending invitations for unregistered emails
  are not implemented.

## Godot package and processing

A ZIP root contains `starport-game.json`, `index.html`, and the Web export's `.js`, `.wasm` and
`.pck` files. The manifest begins:

```json
{"schemaVersion":1,"engine":"godot","entrypoint":"index.html"}
```

The package inspector allows at most 256 MiB ZIP bytes, 512 MiB expanded bytes, 2,000 files and
4,000 archive entries including directories. It rejects unsafe paths, links, case-colliding
names and malformed exports. Local development extracts in a credential-cleared child process.
Hosted mode stages the ZIP in a private upload bucket, dispatches one restricted processor Job
with signed per-object URLs, verifies the returned flat TAR/inventory/checksums again in a trusted
worker, and writes immutable release files into a private bucket. The hosted path is disabled in
deployment values until real staging integration succeeds. No package analysis makes game code
trusted; it always runs on a separate game origin.

Workspace limits use the Accounts catalog keys `games`, `published_games`, `upload_bytes_mb`,
`retained_bytes_mb`, and `monthly_delivery_mb`. Concurrent game creation and publication checks
are serialized in PostgreSQL. Processing checks retained bytes before a release is committed.
An editor can mark an inactive release for deletion; it becomes unavailable immediately and a
background worker deletes its local or private GCS files before freeing its retained-byte count.
The active release must be replaced before deletion. These caps are provisional catalog values,
not verified hosted pricing allowances.

## API families

| Route | Contract |
|---|---|
| `GET /api/config` | Safe runtime auth and feature configuration |
| `GET /api/workspaces`, `POST /api/workspaces` | Current workspaces; local-only creation |
| `PUT /api/admin/workspaces/{id}/publishing` | Local operator grant; not a hosted billing path |
| `GET|POST /api/workspaces/{id}/games` | Editor list/create; paid entitlement required for create |
| `GET /api/workspaces/{id}/usage` | Editor-only counts for games, published games, retained bytes, delivered bytes this UTC month, current limits and paid-through |
| `GET|PUT /api/workspaces/{id}/games/{gameId}` | Studio metadata and audience |
| `POST .../uploads`, `GET .../uploads/{uploadId}` | Bounded ZIP upload and processing status |
| `GET .../releases`, `PUT .../active-release`, `DELETE .../releases/{releaseId}` | Validated releases, exact-review status, activation and inactive-release cleanup |
| `PUT .../publication` | Publish or unpublish after audience/release/review checks |
| `GET|PUT|DELETE .../people` | Subject-bound named-person play grants; email resolver for hosted PUT |
| `POST .../review-requests` | Submit exact public release and metadata revision for review |
| `/api/admin/review-requests` | Moderator queue, candidate preview launch, approval/rejection |
| `POST /api/games/{gameId}/reports` | Signed-in authorized player's bounded report |
| `GET /api/admin/reports`, `POST .../resolve` | Moderator report queue and resolution |
| `PUT /api/admin/games/{gameId}/suspension` | Audited moderation suspension/clear with a reason; clear never republishes |
| `GET /api/games`, `GET /api/games/{gameId}` | Public catalog and authorized details; `?preview=true` for editor details |
| `POST /api/games/{gameId}/launch` | One-use, 60-second fragment ticket for authorized play or publisher preview |
| `GET|PUT|DELETE /api/games/{gameId}/saves` | Signed-in player's own game saves, via app-origin bearer and ETag conditions |
| `GET /api/me/saves`, `GET /api/me/saves/export` | Signed-in player's save inventory and streaming JSON export, independent of publisher/game access |
| `GET|DELETE /api/me/saves/{gameId}/{slot}` | Read or delete the player's own saved slot after access loss; delete requires its current ETag |

The moderator preview launch route is `/api/admin/review-requests/{id}/preview-launch`. It binds
the pending request, candidate release/digest and current metadata. Its session is short-lived
and carries no save bridge. The ordinary game launch returns `{embedUrl, contentOrigin,
bridgeNonce, expiresAt, canSave}`. `embedUrl` contains only an opaque one-use ticket in its URL
fragment; it never contains an OIDC bearer.

All denied private game metadata and files use non-disclosing 404 responses. Publisher plan
errors are separate from Accounts outages; verification outages return 503. String enum JSON and
camelCase fields follow the shared .NET/Angular conventions.

## Game host and bridge

The canonical hosted content origin is `https://g-<32-hex-game-id>.games.starport.club`. The
content-host bootstrap exchanges the fragment ticket for an HTTP-only, host-only `__Host-` cookie
and removes the fragment before loading `index.html`. The app origin holds the OIDC token; a game
origin never receives it. Every protected HTML/JS/WASM/PCK/HEAD/range request rechecks the
current owner entitlement and player ACL, then serves only a file recorded in the validated
release inventory. Game hosts reject app `/api` and `/health` routes.

Each non-moderator game-file GET atomically reserves its full or single-range byte count against
the owning workspace's `monthly_delivery_mb` cap for the current UTC month. HEAD requests do
not consume bytes. Exhausted workspaces are omitted from the public catalog and further file
GETs receive `429 delivery-quota-exceeded`; publisher entitlement checks still apply. This is
a serving limit, not an automatic overage charge.

The JS/Godot bridge uses versioned `postMessage` requests with exact origin, frame source, nonce,
method and payload checks. The trusted parent performs save API calls with its own app-origin
bearer. Cloud saving is opt-in: uploading a Godot export does not sync `user://` automatically.
Saves are bounded JSON in PostgreSQL, keyed by `(gameId, subject, slot)` with 1 MiB per slot and
10 slots per game/player. `If-None-Match: *` creates, a quoted ETag in `If-Match` overwrites or
deletes, and stale revisions fail. A supplied `requestKey` makes identical PUT retries return
the original revision for seven days; reusing it for different data or preconditions fails.
The `/api/me/saves` routes use the signed-in subject directly and remain available if the game
is unpublished, the publisher's paid access expires, or moderation suspends the game. They do
not let the player relaunch or write through a game whose play access has ended. The inventory
returns at most 500 most recently updated slots; `/export` streams all slots for that subject.

The [SDK README](../sdk/README.md) and [sample](../examples/cloud-save-demo/README.md) show
game-side integration. [Browser evidence](browser-isolation-spike.md) covers a real Godot Web
export, parent bridge, and same-site game-host cookie behavior.
