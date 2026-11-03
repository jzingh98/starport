# Testing Starport

Run the backend suite with a disposable PostgreSQL database to exercise the access, launch,
save, and Accounts event-consumer integration tests:

```sh
STARPORT_TEST_DATABASE='Host=localhost;Port=5436;Database=starport_test;Username=starport;Password=starport' \
  dotnet test backend/Starport.sln
```

The PostgreSQL tests return early when `STARPORT_TEST_DATABASE` is unset. Do not interpret a
green run without that variable as database coverage. The test user must be able to create and
migrate the test database schema. Use a dedicated database: the tests insert and delete rows.
The API integration tests assert their actual EF connection targets that database before seeding
any rows.

`PlayerApiPostgresTests` starts the real ASP.NET routes against PostgreSQL. It checks one-use
launch tickets, game-host scoped file delivery, save creation/list/read, ETag compare-and-swap,
stale writes/deletes, request-key replay and reuse, and access loss after a publisher's paid
period expires. It checks that game-scoped saves close on expiry while the player can still list,
read, export, and delete their own save through `/api/me/saves`.

`PublicReleaseWorkflowPostgresTests` keeps an approved public release live while a validated
replacement is submitted for review. It checks the publisher's release list, the moderator
queue and approval, and the activation gate before and after that decision. It also verifies that
the moderator's short preview serves the candidate bytes without changing the public release,
that approval invalidates that preview, and that HEAD and byte-range requests obey the same
session check. It creates and resolves a player report, then verifies moderation suspension
immediately denies an existing game session and leaves the game unpublished after unsuspension.
The moderator queue reflects the game's suspension state.

`HostedArtifactExtractorTests` uses the same TAR writer format as the isolated processor. It
checks a complete verified inventory and rejection of symbolic links and duplicate paths. The
processor's signed-URL mode has an in-memory HTTP test for its bounded remote output.

`PublishingQuotaPostgresTests` checks concurrent draft creation under the workspace game cap,
the published-game cap, retained bytes across releases, and the inactive-release cleanup that
frees retained capacity. It also runs concurrent monthly-delivery reservations to the exact cap.
The player API test confirms a live local game GET increments this month's delivery usage and a
HEAD request does not.

The [browser isolation spike](browser-isolation-spike.md) records the real Godot 4.7 Web export
and WebKit player/bridge tests. A live GCS, Kubernetes Job, Cloud SQL and wildcard TLS test is
still required before enabling the hosted processing and game-content flags.
