# Starport chart

The chart runs the trusted Starport API and SPA. It needs PostgreSQL, an OIDC
issuer, and a Kubernetes Secret. The database migration is a pre-install and
pre-upgrade hook; create the Secret before `helm install`.

```sh
kubectl create namespace starport
kubectl -n starport create secret generic starport-secrets \
  --from-literal=database-password='<password>'
helm upgrade --install starport ./charts/starport -n starport \
  --set externalDatabase.host='<postgres-host>' \
  --set config.auth.oidc.authority='https://login.example.org/realms/example' \
  --set config.auth.oidc.audience='starport'
```

Set `config.auth.oidc.clientId` to the SPA's public client ID. For a hosted
Accounts control plane, set `config.workspaces.source=accounts`, its URL/token
endpoint/client ID, and add `accounts-client-secret` to the same Secret. Local
workspaces need no Accounts secret. The backend uses explicit database parts
with `externalDatabase.maxPoolSize`; it will not start with an incomplete
database or OIDC configuration. The frontend calls `/api/config` at runtime.

`httpRoute.enabled=false` by default. When using Gateway API, set the
`parentRefs` and exact `hostnames`; the chart routes `/api` to the API and `/`
to the SPA and gives both Services an HTTP health check policy. Set the
frontend's `config.csp.connectSrc` to the OIDC origin.

Game content routing is opt-in (`gameContent.enabled=false`). Set
`gameContent.rootDomain` to a dedicated wildcard domain, such as
`games.example.org`, `gameContent.appOrigin` to the exact SPA origin, and
`config.csp.frameSrc` to `https://*.games.example.org`. Provision the matching
wildcard DNS, TLS certificate, and Gateway listener first. The chart then
routes `*.games.example.org` to the API's content-only host handler. The handler
must accept only `g-<32 hex game ID>.games.example.org` and serve validated
release files; it must reject ordinary app/API endpoints on game hosts. Each
game gets its own browser origin.

Hosted publishing uses three private buckets configured by
`config.storage.uploadBucket`, `releaseBucket`, and `saveBucket`. A trusted
API streams the source ZIP to the uploads bucket. The optional one-replica
`processingWorker` runs with a dedicated Kubernetes ServiceAccount that can
only create and inspect Jobs in the restricted process namespace. Set
`config.storage.mode=gcs`, `config.processing.mode=job`, `processor.enabled=true`,
`processingWorker.enabled=true`, and `processor.image` to a registry digest.
The public API remains token-free. A processor Job gets only short-lived signed
GET and PUT URLs for one source ZIP and one `validated.tar`; it gets no service
identity or application Secret. Helm creates the worker, role, quota, and
network policies, while the backend dispatcher creates each Job. The Job
shape is in [examples/processor-job.yaml](examples/processor-job.yaml).

Self-hosted operators can use a different wildcard game domain. Keep it off
the app and identity origins, keep the release bucket private, and configure
the SPA's `frame-src` for that domain. The app origin, game root domain, and
Gateway wildcard must agree exactly.
