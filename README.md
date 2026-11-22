# DAFA Contracts

Internal contract management web app for DAFA Group: one register for the contracts of all
nine legal entities, with owners, approvals, renewal and notice reminders, obligations, document
intake with field suggestions, and a management overview. English UI, internal users only.

Status: version 1 under construction. The first deployment is a **test environment with test
data only** on monsterasp.net. Production will run on Azure with Microsoft Entra sign-in and
files in SharePoint.

## Repo layout

| Path | What it is |
|---|---|
| `src/web` | React 19 + TypeScript + Vite single-page app |
| `src/api/Dafa.Contracts.Api` | ASP.NET Core (.NET 10) host: the API, and it serves the built SPA from `wwwroot` |
| `src/api/Dafa.Contracts.Core` | Domain, application services, interfaces |
| `src/api/Dafa.Contracts.Infrastructure` | EF Core and the providers (test and production implementations) |
| `tests/Dafa.Contracts.Tests` | xUnit tests |
| `infra` | Bicep skeleton for the Azure production environment (not deployed yet) |
| `.github/workflows` | CI and the test deployment |
| `docs` | Build brief, decisions, status, runbook, deployment guide, design reference |
| `Dafa.Contracts.sln` | Solution for the API and tests |

## Run it locally

### Frontend only (mock mode - no backend needed)

Requires Node 22.

```bash
cd src/web
npm ci
npm run dev        # http://localhost:5173, in-browser mock API with demo data
```

Other scripts: `npm run lint`, `npm test`, `npm run build` (output in `src/web/dist`).

### Backend

Requires the .NET 10 SDK.

```bash
dotnet restore Dafa.Contracts.sln
dotnet test Dafa.Contracts.sln
dotnet run --project src/api/Dafa.Contracts.Api
```

With default settings the API runs in test mode: Dev sign-in (pick a demo user), SQLite, local
file storage and seeded demo data. Check `/healthz` to confirm it is up.

### Frontend against the local backend

```bash
cd src/web
npm run dev:real   # proxies /api to the backend on http://localhost:5080
```

The backend section of `docs/STATUS.md` is authoritative for the local port and the exact
configuration keys.

## Configuration in short

Every external dependency has a test implementation and a production implementation, chosen
by configuration: `Auth:Mode`, `Database:Provider`, `Storage:Mode`, `Ai:Mode`,
`Suppliers:Mode`, `Notifications:Mode`, `Seed:DemoData`. See the table in `docs/RUNBOOK.md`.

## CI and deployment

- **CI** (`.github/workflows/ci.yml`) runs on every push and pull request: lint, test and
  build the SPA; build and test the API; compile-check the Bicep; then publish the API with the
  SPA inside it and upload the result as the `api-publish` artifact.
- **Deploy test** (`.github/workflows/deploy-test.yml`) sends that artifact to the
  monsterasp.net test site with Web Deploy. It runs after a successful CI run on `main`, or
  manually from the Actions tab. Without the deployment secrets it skips with a clear message.
- **Production** (Azure) is not set up yet. `infra/main.bicep` is a starting skeleton.

The test site uses Dev sign-in, which has no real authentication. It must only ever hold test data.

## Documentation

- [docs/BUILD_BRIEF.md](docs/BUILD_BRIEF.md) - scope, architecture, domain and API contract (source of truth)
- [docs/DEPLOY_TEST.md](docs/DEPLOY_TEST.md) - step-by-step: set up and deploy the test site
- [docs/RUNBOOK.md](docs/RUNBOOK.md) - operations, configuration, moving to production, handover notes
- [docs/STATUS.md](docs/STATUS.md) - what is built, stubbed and outstanding
- [docs/DECISIONS.md](docs/DECISIONS.md) - decision log (append-only)
- [docs/design](docs/design) - approved visual design reference
