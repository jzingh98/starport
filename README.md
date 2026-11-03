# Starport

Starport is an open-source platform for publishing and playing browser games. A Free player can
play any game they are allowed to see. Pro and Team publishing rights belong to the game's owning
workspace; a paid Team's Owner, Admin, and Member can publish, while Viewers can play team games.
Publishers can choose specific people, their team, or the public. Public games need review before
listing. Player cloud saves are separate from publisher ownership.

The hosted application is planned for `app.starport.club`, with marketing at `starport.club` and
each game on its own `g-<game-id>.games.starport.club` origin. Other operators can host their own
Starports under the AGPL license. Game code and art retain their publishers' own licenses.

## What is in this repository

| Directory | Purpose |
|---|---|
| `backend/Starport.Domain`, `Application`, `Infrastructure`, `Api` | .NET 10 access rules, publishing, PostgreSQL, HTTP API |
| `backend/Starport.Processor` | Bounded Godot Web ZIP validation and isolated hosted Job entrypoint |
| `spa/starport` | Angular player, creator studio, review queue, OIDC, and shared UI kit |
| `sdk/javascript`, `sdk/godot`, `examples/cloud-save-demo` | Versioned save bridge, Godot addon, and a real Web export sample |
| `charts/starport`, `docker`, `.github` | Helm, images, CI, and release automation |
| `docs` | [Implementation and launch gates](docs/implementation-plan.md), [contracts](docs/contracts.md), [tests](docs/testing.md), [browser isolation evidence](docs/browser-isolation-spike.md) |

The SaaS control plane lives in the sibling `accounts` repository: Keycloak, Stripe, the product
catalog, workspace membership, and authoritative publishing entitlements. Starport does not hold
Stripe credentials. The sibling `jagveerllc` repository contains the marketing site, and
`Jagveer` contains the hosted deployment configuration. This public repository can run locally
without those private services.

## Run locally

You need .NET 10, Node 22/npm, and Docker. From this repository root:

```sh
docker compose up -d postgres
ASPNETCORE_ENVIRONMENT=Development dotnet run \
  --project backend/Starport.Api --no-launch-profile \
  --urls http://127.0.0.1:5084
```

In another terminal:

```sh
cd spa/starport
npm ci
npm start -- --host app.lvh.me
```

Open `http://app.lvh.me:4200`. The Angular dev server proxies `/api` to port 5084. Development
mode authenticates every request as the fixed `local-operator` subject and applies PostgreSQL
migrations on API startup. It is a single-user development mode, not production authentication.
The development configuration expects each game at
`http://g-<32-hex-id>.games.lvh.me:5084` and the app at `http://app.lvh.me:4200`;
both names resolve to loopback. Local game assets live under `starport/data`, which is excluded
from Git. This HTTP setup supports studio/API development. The real Godot browser smoke used an
HTTPS reverse proxy at `https://app.lvh.me:8443` and per-game
`https://g-<id>.games.lvh.me:8444`. See [the browser test record](docs/browser-isolation-spike.md)
for its overrides and evidence; hosted TLS and GCS/GKE still need staging verification.

Create a Personal workspace in the studio. A new workspace starts without publishing rights. In
the local development configuration the fixed subject is also an operator, so grant publishing
to the returned workspace ID:

```sh
curl -X PUT 'http://127.0.0.1:5084/api/admin/workspaces/<workspace-id>/publishing' \
  -H 'Content-Type: application/json' \
  --data '{"canPublish":true,"paidThrough":"2030-01-01T00:00:00Z","suspended":false,"limits":{"games":10,"published_games":5,"upload_bytes_mb":256,"retained_bytes_mb":2048,"monthly_delivery_mb":4096}}'
```

The studio then accepts a Godot browser export ZIP with `starport-game.json` at its root. See
[the SDK guide](sdk/README.md) and [sample game](examples/cloud-save-demo/README.md). A local
processor validates the ZIP in a credential-cleared child process. Public candidates can be
submitted to the moderator queue; the local operator can review them in the app.
The studio displays workspace game, published-game, retained-byte, and monthly delivery usage.
An inactive release can be removed to free retained space after the cleanup worker finishes.

Signed-in players can list, export, read, and delete their own cloud saves through
`/api/me/saves`, even when a publisher's plan has expired or the game is suspended. Deletion
requires the save's current ETag. The game bridge itself cannot use this data-management API.

Run the backend tests against a dedicated disposable PostgreSQL database:

```sh
STARPORT_TEST_DATABASE='Host=localhost;Port=5436;Database=starport_test;Username=starport;Password=starport' \
  dotnet test backend/Starport.sln
```

The PostgreSQL tests skip when this variable is absent. Frontend checks run from `spa/starport`
with `npm run lint`, `npm run typecheck`, `npm test`, and `npm run build`. See
[testing.md](docs/testing.md) for browser and hosted checks.

## Hosted release status

The Accounts catalog, identity client, price definitions, marketing site, API integration, and
Jagveer Helm/Terraform scaffold are implemented in sibling checkouts. Starport's hosted GCS
upload, restricted processor Job, private release delivery, and wildcard game-host code are in
this repository. The hosted values deliberately keep processing and game content **disabled**
until a real staging run verifies GCS, Kubernetes Jobs, wildcard DNS/TLS/Gateway behavior, and
browser delivery. The processor image needs a published digest pin. Production deployment is
also blocked by the current Cloud SQL connection budget, and live Stripe price provisioning
has not been verified. Draft legal terms/privacy pages are not launch-ready.

See [implementation-plan.md](docs/implementation-plan.md) for the remaining gates and
[charts/starport/README.md](charts/starport/README.md) for self-hosted chart configuration.

Starport is licensed under [AGPL-3.0-only](LICENSE). See [NOTICE](NOTICE) and
[CONTRIBUTING.md](CONTRIBUTING.md).
