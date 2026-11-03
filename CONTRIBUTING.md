# Contributing to Starport

Bug reports, fixes, documentation and features are welcome. For a bug, include
the version from `/api/config`, your runtime setup, steps to reproduce, and the
expected and actual results. Please discuss larger changes in an issue before
building them. Report security problems privately to the repository maintainers.

## Development

Use the .NET 10 SDK and Node.js 22. Start the API and Angular dev server with
the commands in [README.md](README.md). The frontend proxies `/api` to the API.

Before a pull request, run:

```sh
dotnet test backend/Starport.sln
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project backend/Starport.Infrastructure --startup-project backend/Starport.Infrastructure --context StarportDbContext
cd spa/starport
npm ci
npm run lint
npm run typecheck
npm run build
```

Keep changes focused and update the relevant docs when a contract changes. Add
tests for authorization, billing boundaries, untrusted packages and other
behavior that needs a regression check.

By contributing, you agree that your contribution is licensed under the
[AGPL-3.0-only](LICENSE), the license of this project (see [NOTICE](NOTICE)).
