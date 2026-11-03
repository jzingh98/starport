# Starport implementation and launch plan

Status: active implementation, 2026-11-02. This document distinguishes checked-in code from a
working hosted release. The current product is a publisher platform: all games are free to play;
Free accounts play, while an entitled Pro or Team workspace can publish. The earlier plan for
operator-only games and premium player membership is superseded.

## Product decisions

| Area | Decision |
|---|---|
| Product and domains | Starport; marketing at `starport.club`, app at `app.starport.club` |
| Ownership | Each game belongs to one Personal or Team workspace, including our own games |
| Publisher access | Personal Pro, Team, or matching All Apps entitlement; Team Owner/Admin/Member can publish; Viewer cannot |
| Player access | No paid player membership. Public games may be played anonymously; shared and Team games need the matching identity or membership |
| Audiences | Private publisher preview, named people, owning team, or public |
| Review | Private preview is immediate after validation; public listing needs approval of the exact release bytes and metadata revision |
| Expiry | Cancellation remains active through the paid period; games go offline at the exclusive paid-through boundary. Restoration requires an explicit republish |
| Games and progress | Godot browser exports first; opt-in cloud saves belong to the player and game |
| Game isolation | `g-<32-hex-game-id>.games.starport.club`, one origin per game; same registrable domain as the app with host-only cookies and strict app/game separation |
| Open source | AGPL-3.0-only platform; self-hosters run independent catalogs, accounts, policies, and games |

The Accounts example catalog sets standalone Starport Pro at $20/month or $200/year and Team at
$30/month or $300/year per paid seat, matching sibling products. All Apps includes the matching
publishing tier. Those definitions are checked in; live Stripe price provisioning and a complete
test-mode checkout have not been verified. Pricing and limits shown publicly must remain aligned
with the Accounts catalog before launch.

## Code and repository boundaries

| Repository | Implemented responsibility |
|---|---|
| `starport` (this repository) | .NET 10 API/domain/application/infrastructure, PostgreSQL migrations, bounded processor, Angular app, Godot/JS SDK, sample, Docker/Helm/CI |
| Sibling `accounts` | Product and price catalog, Stripe subscription rules, workspaces, product-scoped entitlement and identity APIs, durable serving-loss feed, Keycloak clients/roles |
| Sibling `jagveerllc` | Astro marketing, pricing/product registry, shared site kit, self-hosting/publisher pages |
| Sibling `Jagveer` | Staging/production values, identity and GCS/Terraform scaffold, Gateway/DNS/certificate design, deployment gates |

Game uploads are data operations. They do not start source builds, application deploys, or GitHub
Actions. An uploaded game's own source and assets retain their publisher's chosen license.

## Implemented application paths

1. **Identity and entitlement.** Production uses OIDC access tokens. Starport's confidential
   service identity asks Accounts for current workspace membership, serving availability, and
   verified-email identity resolution. Local mode uses PostgreSQL workspaces and operator grants.
   It never treats a paid workspace unrelated to the game as permission to publish. Accounts
   outages fail closed with 503. Its durable serving-loss feed and a Starport cursor consumer
   unpublish lapsed games, while every protected request also checks current availability.
2. **Publishing.** A paid editor creates a private draft, uploads a raw Godot Web ZIP, sees
   processing status/releases, chooses audience, shares with verified existing Accounts users,
   and activates a validated release. PostgreSQL serializes game-count and published-count cap
   checks; processing checks the workspace's retained-byte allowance. Editors can remove an
   inactive release to reclaim retained space after asynchronous file cleanup. The studio shows
   counts and current limits through `/api/workspaces/{id}/usage`. Local development runs the
   processor as a credential-cleared child. The hosted path writes the ZIP to a private GCS
   bucket; a trusted one-replica worker
   creates a credentialless restricted Kubernetes Job with one signed GET URL and one signed PUT
   URL. The Job returns a flat `validated.tar`; the worker independently checks its inventory,
   checksums and paths, uploads immutable release files, and commits metadata. This hosted path
   compiles and has component tests, but lacks a live GCS/GKE staging end-to-end run.
3. **Review.** A publisher submits a specific validated release and metadata revision. The
   moderator queue includes the submitted game metadata and exact digest. A moderator can open a
   short-lived candidate preview without a save bridge, then approve or reject it. An unapproved
   public candidate cannot be activated. An existing approved release remains live while its
   replacement is pending. Changing public metadata removes publication until reapproval.
4. **Delivery.** Launch creates a one-use 60-second ticket. A game-host bootstrap exchanges it
   for an HTTP-only, host-only game cookie; protected file requests recheck the owner entitlement,
   play audience, active release or pending moderator preview, and allowlisted inventory. Files
   are served through the API from local storage or a private GCS bucket, with HEAD and single
   range handling. Each non-moderator GET atomically reserves served bytes in the owning
   workspace's UTC calendar month and denies further GETs at its `monthly_delivery_mb` cap;
   HEAD is not charged. Public catalog eligibility also checks remaining delivery allowance.
   No public bucket URL or shared CDN bypasses authorization.
5. **Cloud saves.** A versioned JS/Godot bridge sends bounded messages from the game iframe to
   the trusted Angular parent. The parent validates exact source, origin, nonce and method, then
   calls the app-origin API with its own OIDC bearer. The game never receives that bearer. Saves
   are player/game/slot scoped JSON in PostgreSQL, up to 1 MiB per slot and 10 slots. ETag
   preconditions prevent silent overwrites; request-key replays are retained for seven days.
   A separate signed-in `/api/me/saves` API lists, streams an export, reads, and deletes the
   player's own saves without requiring current game access, so a publisher lapse does not trap
   progress. Its list is capped at 500 rows; export streams the full set.
6. **User interface.** The shared UI kit supports paged public catalog/detail/play pages, sign-in,
   workspace selection, creator draft/editor/upload/review/sharing controls, a moderator review
   queue, workspace usage, inactive-release removal, and save status. The sample Godot 4.7.2 Web
   export exercised the bridge in WebKit.
7. **Moderation.** An authorized signed-in player can file a bounded game report. Moderators can
   inspect and resolve the report queue, suspend or clear a game with a reason, and the action is
   recorded. Suspension immediately removes publication and blocks launch/file/save access;
   clearing it does not republish automatically.

The code above does not imply the hosted feature flags are on. The Jagveer values currently keep
`gameContent.enabled=false`, `processor.enabled=false`, `processingWorker.enabled=false`,
`config.storage.mode=local`, and `config.processing.mode=disabled`.

## Authorization and isolation invariants

- The stored game workspace is the owner. API path parameters cannot substitute another
  workspace for entitlement checks. Owner/Admin/Member are editors; Team Viewers can play but
  cannot publish. A named-person grant is bound to a stable subject, not an email or secret URL.
- A public game needs a validated active release and approval for its exact SHA-256 and current
  metadata revision. Moderator suspension and a lost owner entitlement override all audiences.
  `PaidThrough == now` is expired. A returned entitlement is never cached as a lasting positive
  grant. Reinstating billing does not silently republish previously removed games.
- Anonymous play is limited to public approved games. Publisher preview is limited to current
  editors; moderator candidate preview is bound to a pending review, specific release and digest,
  and a 15-minute game session. Review preview carries no cloud-save bridge.
- The same-site `*.games.starport.club` decision makes host-only cookie scope essential. The app
  stores its OIDC token on the app origin and sends it only to `/api` there. The game iframe has a
  separate origin, a sandbox, server CSP/permissions headers, no app bearer, and a host-only
  `__Host-` game cookie. The game host rejects ordinary app/API routes. Do not set auth cookies
  for `.starport.club`. A future cache must authorize every hit as well as every miss.
- A browser that already downloaded a game cannot be made to forget those bytes. “Offline at
  expiry” means the next launch, file, catalog, and save API request is denied when the
  authoritative owner entitlement is inactive. The serving-loss feed persists unpublication
  after an unexpected lapse so restoration still requires deliberate republishing.

## Local and self-hosted operation

[README.md](../README.md) gives the reproducible local API, PostgreSQL and Angular commands.
Development mode has a fixed operator subject and is never a multi-user authentication mode.
Production requires OIDC plus a secure PostgreSQL connection. Self-hosters can use local workspace
grants with their own OIDC issuer and manually grant publishing, or implement an authoritative
workspace provider using the existing interface. The public checkout needs no private Jagveer or
Accounts repository for its local mode.

The Helm chart defaults to hosted processing and game routing **off**. A self-hoster who enables
them must supply PostgreSQL, OIDC, private GCS buckets, the matching wildcard DNS/TLS/Gateway
route, a distinct game root domain and
exact app origin, SPA `frame-src`, a digest-pinned processor image, isolated Job namespace/RBAC,
and object-storage identities. The chart README contains the exact knobs. The current hosted GCS
implementation is not an S3/MinIO provider; that is a future portability task.

## Acceptance evidence already collected

- Backend xUnit suite exercises access policy, ZIP/TAR validation, release workflow, one-use
  launch, host-scoped file delivery, quota concurrency, inactive-release cleanup, save CAS and
  request-key replay, and Accounts serving-loss replay/concurrency with PostgreSQL. Set
  `STARPORT_TEST_DATABASE` to get the database tests;
  green results without it do not constitute PostgreSQL coverage.
- Accounts suite, Keycloak import/smoke, catalog and entitlement tests passed in the sibling
  checkout. Angular lint/typecheck/build and bridge tests, SDK tests, real Godot Web export,
  WebKit same-site cookie and cross-origin isolation checks passed locally. A real Godot export
  also passed an HTTPS `app.lvh.me` → `g-<id>.games.lvh.me` local API/Angular/browser run,
  including ZIP processing, protected WASM/PCK delivery and actual cloud-save writes. See
  [testing.md](testing.md) and [browser-isolation-spike.md](browser-isolation-spike.md).
- Helm lint/render, processor Docker build, actionlint, Jagveer release validation, Terraform
  validation/tests, and static staging/prod topology checks passed. They are configuration tests,
  not proof that the target domains, buckets, Gateway, Stripe or Cloud SQL are live.

## Remaining work before a hosted launch

| Gate | Concrete result needed |
|---|---|
| Hosted staging path | Publish/pin backend, frontend and processor images; provision staging bucket/identity/Job resources; enable staging GCS, worker and game content values; complete one real ZIP → Job → TAR → GCS release → wildcard HTTPS player flow |
| Content domain | Verify wildcard DNS, certificate and GKE Gateway listener for `g-<id>.games.staging.starport.club`; repeat the successful local HTTPS Godot run in WebKit plus Chromium/Firefox on the actual route, including private access, wrong-host denial, expired entitlement during fetch, HEAD/range and no OIDC bearer exposure |
| Database capacity | Raise or rebudget production Cloud SQL connection capacity before enabling production Starport. The checked-in release guard reports 86 base connections, 88 with the worker, against an 80-connection cap |
| Billing | Provision actual Starport Stripe prices in the target Accounts environments and run test-mode Pro/Team/All Apps checkout, seats, cancellation, failed renewal, paid-through and webhook replay tests |
| Moderation and abuse | Exercise the implemented report, resolution, suspension and audit routes in a live staging workflow; establish content policy, reviewer operations, appeals, and rate/serving abuse controls beyond the current request limits |
| Player data | Exercise the implemented save list/export/delete flow after a publisher lapse; add account-deletion propagation, retention/backup policy, a restore drill, and the player-facing data-management UI. Saves currently live as bounded PostgreSQL JSON; the reserved save bucket is unused |
| Sharing and quotas | Add invitations for as-yet-unregistered email recipients. The implemented game/published/retained/monthly-delivery quotas need live storage and egress measurements, failed-upload cleanup verification, and approved allowances. The current per-file ZIP and save caps remain separate from commercial tier limits |
| Discovery and product finish | Validate the implemented bounded `?page=0..100` public catalog under load; add creator attribution/media policy, responsive/accessible game controls, and the final legal/support pages. Current marketing terms/privacy pages are marked draft |
| Release | Complete full local multi-user OIDC and staging e2e, publish the open-source repository/images/Helm chart, pin release digests in Jagveer, deploy according to its normal approvals, then verify live routes and rollback |

These are explicit gates. Until they pass, do not claim `app.starport.club` or public user publishing
is live. Keep uploads, processing and game content disabled in hosted values.

## Recommended completion order

1. Finish the API/UI gaps that can be verified locally: the player-facing save-management UI,
   account-deletion propagation, invitations, usage/retention policy, and catalog load behavior. Add targeted
   tests for each security or data-integrity boundary.
2. Publish staging image digests, enable only staging GCS/worker/game content, and run the real
   browser/Job/entitlement and private-sharing matrix. Record load, storage and egress metrics
   from a representative Godot game. Resolve failures before production values change.
3. Verify Accounts/Stripe price provisioning and end-to-end subscription transitions in test
   mode. Reconcile the marketing copy, legal policies and hosted entitlement limits.
4. Resolve Cloud SQL production capacity and run a restore/rollback drill. Then pin and deploy a
   production release through the existing Jagveer process; keep the content domain and
   processing flags behind measured staging evidence.

The [contracts](contracts.md) document contains the current API and package contract; the
[chart guide](../charts/starport/README.md) covers self-hosted deployment settings.
