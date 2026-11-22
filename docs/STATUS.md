# Status

## DevOps phase 1

Built:
- `.github/workflows/ci.yml` - jobs `web` (Node 22, npm ci/lint/test/build, artifact `web-dist`), `api` (.NET 10 restore/build/test, TRX in artifact `test-results`, error logs in `build-logs`, readable compile-error and failed-test summary in the job log and run summary), `infra` (Bicep compile check), `package` (SPA into `wwwroot`, `dotnet publish` Release `win-x86` framework-dependent, artifact `api-publish`). npm and NuGet caches.
- `.github/scripts/trx_summary.py` - TRX to readable summary (tested locally with a sample file).
- `.github/workflows/deploy-test.yml` - manual, and after a successful CI run for a push to `main`. Web Deploy (msdeploy) to monsterasp.net, writes test-mode `appsettings.Production.json` at deploy time, skips with a clear message when secrets are missing, optional `/healthz` check.
- `infra/main.bicep` + `infra/main.example.bicepparam` - Azure production skeleton (swedencentral).
- `docs/DEPLOY_TEST.md`, `docs/RUNBOOK.md`, root `README.md`, root `.editorconfig`.

Not verified / stubbed:
- Neither workflow has run on GitHub yet. YAML was parsed locally; `actionlint` was not available.
- The Bicep has NOT been compiled or deployed (no Bicep CLI here). First validation is the CI `infra` job.
- No production (Azure) deployment workflow yet.
- monsterasp.net documents ".NET 8 and newer" / "current .NET versions" without naming .NET 10. Fallback: repository variable `PUBLISH_SELF_CONTAINED=true`.
- Whether `msdeploy.exe` is on the `windows-latest` runner path is unconfirmed; the workflow installs it with Chocolatey if missing.
- The msdeploy `-enableRule:AppOffline` and `-skip` (App_Data) arguments are standard msdeploy but are not in monsterasp.net's own example.

Requests to the backend engineer (not done by DevOps, outside its folders):
- State the exact connection string key in this file. Until then the deploy writes the string to `ConnectionStrings:Default`, `ConnectionStrings:DefaultConnection` and `Database:ConnectionString`.
- `/healthz` must exist, be anonymous, and return HTTP 200.
- CI checks that publish output contains `Dafa.Contracts.Api.dll`, `web.config` and `wwwroot/index.html`; keep the default assembly name and in-process hosting.
- monsterasp.net: persist Data Protection keys to `Path.Combine(ContentRootPath, "..", "private", "keys")` (their documented fix), otherwise every restart/deploy signs users out. Consider the same `private` folder for `Storage:Mode=Local` files.
- monsterasp.net Free stops the app after 3 hours idle, so the hourly `BackgroundService` only runs while the site is in use; the admin "run now" endpoint covers testing.
- Optional hardening for the test site: an app-level shared access code in Dev mode (monsterasp.net documents no site-level password protection).
- Root `.gitignore` (owned by another agent): please add `artifacts/`, `publish/` and `infra/main.bicepparam`.

## Frontend phase 1 (2026-11-22)

Scope: `src/web` only. Not committed.

### How to run
```
cd src/web
npm install
npm run dev        # mock API in the browser (default), http://localhost:5173
npm run dev:real   # VITE_API_MODE=real; proxies /api to http://localhost:5080 (POSIX shells; on Windows set the variable first)
npm run build      # tsc + vite build to src/web/dist (real API mode; VITE_API_MODE=mock for a demo build)
npm run lint
npm test
```
Mock state lives in memory and resets on reload; the selected demo user is remembered in localStorage.

### Built
- Scaffold: React 19, TypeScript strict, Vite, react-router, @tanstack/react-query, vitest + Testing Library, eslint, Inter self-hosted (latin + latin-ext). Plain CSS with tokens in `src/styles/tokens.css`.
- Components (`src/components`): Button/ButtonLink, Card, Pill/StatusPill/StatusDot/NoticePill, Segmented (toggle, tabs, radio), Avatar, Field/TextInput/Select/Textarea/ChipSelect, Table (sortable), Modal (bottom sheet under 720px, focus trap), Toast, EmptyState/ErrorState, Skeleton, Icon.
- Shell: 224px sidebar (Work/Manage, badges from `home`), user card with Dev-mode user switcher, top bar with global search (`GET search`, Ctrl/Cmd+K, keyboard navigable), entity scope select, New contract; bottom tab bar under 720px.
- API layer: `src/api/types.ts`, `Api` interface (`api.ts`), fetch client (`client.ts`: credentials include, problem+json to `ApiError`, multipart, downloads), mock (`src/api/mock`).
- Mock: every endpoint in the brief, including approvals and all admin resources, with state changes in memory. Access model enforced in `mock/access.ts` for the selected user. Seed: 9 entities, 13 departments, 10 types, 12 demo users, 30 suppliers, 47 contracts, obligations, links, documents, 4 approvals (3 in progress), reminders, approval rules by value band, retention rules, 2 templates, 1 holiday cover. Dates are relative to today.
- Screens working against the mock: Home (task actions: renewal dialog, approval review dialog with approve/reject/delegate, mark obligation done, missing data link), Contracts register (5 views, search, entity/department/type/status filters, sort, paging, quick look, CSV export, state in the URL), Contract record (lifecycle, AI summary, notice countdown, renewal decision dialog, key terms, documents upload/download, obligations CRUD + complete, links add/remove, people, reminders, access, approvals tab with decision form, activity tab, edit details, submit for approval, mark as signed), New contract (upload with suggestions accepted one by one or all at once, or template, or no file; validated details form; links and obligations; approval route preview; save as draft or submit).
- Reusable for the next screens: `features/Approval.tsx` (ApprovalRoute, RoutePreview, Deviations, KeyTerms, DecisionForm), `features/RenewalDialog.tsx`, `features/ContractForm.tsx`, `app/context.tsx` (useApi, query keys, permissions).
- Tests: 96 in 6 files (mock access filtering and flows, date/notice calculations, register filters/sort/paging/CSV, new-contract validation, suggestion mapping, page-level tests for shell, Home, register, record, new contract).

### Stubbed
- Approvals, Renewals, Overview, Settings: routed placeholder pages marked "Coming next". Their endpoints exist in the mock.
- Mock AI extraction derives suggestions from the file name and canned text; it does not read the file.
- Seeded documents download as generated one-page placeholder PDFs; template drafts are .rtf in the mock.
- `storeUrl` is null in the mock, so "Open in SharePoint" does not show.

### Known gaps
- Not run against the real API (no backend here); the open payload shapes in DECISIONS.md need backend agreement.
- No Entra sign-in redirect; a 401 only offers the Dev user picker.
- The reminder dates in the New contract side panel use the default 120/60/30 days, because reminder settings are an admin-only endpoint.
- Opening the register quick look calls `GET contracts/{id}`, which also counts as "recently opened" in the mock.
- The production bundle includes the mock code (about 495 kB JS, 149 kB gzip); it could be split out with a dynamic import.
- Checked in headless Chrome at 1440px and 360px only; no Firefox/Safari run, no screen-reader pass, no automated contrast audit. The filter chips size to their content with `field-sizing`, which older Firefox/Safari ignore (chips are then wider).
- No end-to-end test suite; the browser checks were manual scripts and are not in the repo.
