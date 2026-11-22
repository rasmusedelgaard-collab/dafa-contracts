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

## Backend phase 1 (2026-11-22)

Scope: `src/api`, `tests`, `Dafa.Contracts.sln`, root `.gitignore`. Not committed.

**Nothing here has been compiled or run.** There is no .NET SDK or NuGet access in the workspace. The code was written conservatively and desk-checked (usings, signatures, DI graph, JSON shapes against `src/web/src/api/types.ts`), but the first real compile and test run is the CI `api` job. Expect some compile errors and failing tests on the first run.

### How to run (once a .NET 10 SDK is available)
```
dotnet restore Dafa.Contracts.sln
dotnet build Dafa.Contracts.sln
dotnet test Dafa.Contracts.sln
dotnet run --project src/api/Dafa.Contracts.Api      # http://localhost:5080 (Properties/launchSettings.json)
```
Defaults need no configuration: Sqlite file `App_Data/dafa-contracts.db`, demo data, Dev sign-in, local files in `App_Data/files`. The SPA is served from `wwwroot` (only a `.gitkeep` is committed; CI copies the build there).

### Built
- Solution with `Dafa.Contracts.Core`, `.Infrastructure`, `.Api` (assembly `Dafa.Contracts.Api`, in-process hosting defaults untouched) and `tests/Dafa.Contracts.Tests`.
- Core: entities and enums, calculations (notice deadline, days to notice, next reminder, lifecycle, missing data), access rules, required-field validation, and services for contracts (list/detail/create/update/submit/sign/renewal decision/CSV export), intake, documents, obligations, links, approvals (rule matching, parallel stages, role resolvers, delegate, holiday cover, reject to Draft, final approval with an approval log text file), jobs (contract end/auto-renewal, notice reminders, escalation, obligation reminders, holiday-cover rerouting, supplier sync, directory sync, upload cleanup), home, renewals, overview, search, admin settings, audit.
- Access model in one place: `ContractAccessRules` (pure rules) + `ContractAccessService` (query filter and `GetReadable/GetEditable`). Every contract, document, obligation, link and approval read goes through it.
- Infrastructure: one EF Core model for Sqlite and SQL Server, `EnsureCreated`, idempotent seeding (reference data always; demo data when `Seed:DemoData=true` and `Auth:Mode` is not `Entra`): the 9 entities, 13 departments, 10 types, 10 fictional users covering every access path, 28 fictional suppliers, 46 contracts with obligations, links, placeholder documents, 4 approvals in progress + 1 rejected + 1 approved awaiting signature, 6 default approval rules, retention rules, reminder settings, 2 Word templates generated with Open XML.
- Test-mode providers: Local document store, Heuristic analyzer (PdfPig + Open XML text, regex extraction, summary, deviations), Seed supplier source, Outbox notifier, Seeded directory, no e-signing.
- API host: all endpoints in brief section 5, RFC 7807 errors, cookie session (HttpOnly, SameSite=Lax, Secure outside Development), Dev and Entra sign-in in separate extension methods, role `Admin` on `api/admin/*`, static files + SPA fallback, hourly `BackgroundService` + `POST api/admin/jobs/run`, anonymous `GET /healthz` (200, "Healthy").
- Tests (xUnit, 69): access matrix (5 paths + denial + current approver, pure rules and the SQL filter), calculations, validation, rule matching, approval engine (parallel, reject, delegate, holiday cover at start and via the job, fallback approver), reminders (idempotency, schedule, escalation, decision stops reminders, auto-renew), and 8 `WebApplicationFactory` tests on in-memory Sqlite with the demo seed.

### Requests from DevOps: answers
- Connection string key: **`ConnectionStrings:Default`** (only this one is read). Provider: `Database:Provider` = `Sqlite` | `SqlServer`.
- `/healthz` exists, is anonymous, returns 200.
- Assembly name `Dafa.Contracts.Api`; local dev port **5080**.
- Data Protection keys: `DataProtection:KeysPath`, default `App_Data/keys` under the content root; set it to `../private/keys` on monsterasp.net. The folder is created; if it cannot be written, a warning is logged and keys stay in memory.
- Dev access code: `Auth:Dev:AccessCode`; empty means no code is asked for.
- `.gitignore`: `artifacts/`, `publish/`, `infra/main.bicepparam` added.

### Configuration keys
| Key | Default | Notes |
|---|---|---|
| `Auth:Mode` | `Dev` | `Dev` or `Entra` |
| `Auth:Dev:AccessCode` | empty | when set, required for `dev/users` and `dev/login` |
| `Auth:Cookie:AllowInsecure` | `false` | `true` allows the session cookie over plain http outside Development |
| `Auth:Entra:AutoProvision` | `true` | create unknown tenant users as Reader at first sign-in |
| `AzureAd:*` | none | Entra mode and Graph/Power BI tokens: `Instance`, `TenantId`, `ClientId`, `ClientSecret`, `CallbackPath` |
| `Database:Provider` | `Sqlite` | `SqlServer` in `appsettings.Production.json` |
| `ConnectionStrings:Default` | empty | empty + Sqlite = `App_Data/dafa-contracts.db` |
| `Seed:DemoData` | `true` | ignored in Entra mode. Set `false` for any real data |
| `DataProtection:KeysPath` | `App_Data/keys` | relative paths are under the content root |
| `Jobs:Enabled`, `Jobs:IntervalMinutes`, `Jobs:StartupDelaySeconds` | `true`, `60`, `30` | |
| `Documents:Provider`, `Documents:LocalPath` | `Local`, `App_Data/files` | `SharePoint` uses `SharePoint:Sites:[{Entity,SiteId}]`, `SharePoint:DefaultSiteId` |
| `Analyzer:Provider` | `Heuristic` | `AzureOpenAI` uses `AzureOpenAI:Endpoint`, `Deployment`, `ApiKey`, `ApiVersion` |
| `Suppliers:Provider` | `Seed` | `PowerBi` uses `PowerBi:DatasetId`, `Query`, `Columns:Id/Name/Country/VatNo` |
| `Notifications:Provider`, `Notifications:Sender`, `App:BaseUrl` | `Outbox` | `Graph` sends mail from `Sender` |
| `Directory:Provider` | `Seeded` | `Graph` |
| `Signature:Provider` | `None` | `DocuSign` is a stub |

The deploy workflow also writes `Database:ConnectionString`, `ConnectionStrings:DefaultConnection` and `Suppliers:Mode`; these are not read and can be dropped.

### Stubbed / production-mode code that has never run
All of these compile-targets sit behind the same interfaces and are only registered when their provider key is set: SharePoint document store (Graph REST, app-only token rather than on-behalf-of-user, simple upload so files up to about 4 MB), Azure OpenAI analyzer (falls back to heuristics on any error), Power BI supplier source, Graph mail notifier (no Teams), Graph directory sync (users only, first 999; no group-to-department mapping), DocuSign (throws), Entra sign-in (`Microsoft.Identity.Web`, local sign-out only).

### Contract mismatches (frontend `types.ts` / `api.ts` vs backend)
Checked against the working-tree versions of `types.ts`, `api.ts` and `client.ts` (including `dev/config`, the access code header/body, `contracts/{id}?peek=true`, `GET reminder-settings`, multipart `admin/templates`, `admin/templates/{id}/content`, `ruleId`/`ruleName` on the preview, `auth/login`). Remaining differences:
1. `ContractDetail.changeOfControl` is typed `boolean`; the backend returns `null` for contracts where it was never filled in (one seeded contract; it is listed in `missingData` as "Change of control").
2. `missingData` labels: the backend uses "Notice period", "Contract value", "Signed date" like the mock, and additionally "Title", "Counterparty", "Currency", "Change of control", "Renewal period".
3. `approvals/preview` approver strings follow "Role · Name"; an unresolved role is "Role · not assigned" and a specific user is "Approver · Name". An optional `currency` parameter converts `value`; without it `value` is DKK as the frontend sends it.
4. LetExpire on an auto-renew contract is accepted by the API (the frontend just does not offer it).
5. `renewals` also returns contracts whose notice deadline has passed without a decision while the contract is still running (negative `daysToNotice`).
6. `POST admin/templates` also accepts JSON `{ name, description, typeId }` without a file and then generates a starter .docx (the earlier frontend shape); the multipart form the frontend now uses stores the uploaded file.
7. With an access code configured, a caller who already has a session is not asked for the code again on `dev/users` / `dev/login` (so the user switcher keeps working).
8. Extra fields the frontend can ignore: `openEnded`, `renewalDecidedById` on the detail; `isActive`/`sortOrder` on departments and types; `isActive` on rules and templates; `contractNo` on audit events; `recipient`, `recipientEmail`, `kind`, `deliveredAt` on outbox; more counters on the job result; `email`, `roles`, `access` on dev users.
9. `search` returns nothing for fewer than 2 characters.

### Known risks (could not be verified without a compiler)
Most likely to fail compilation or tests, in rough order:
1. `Auth/EntraAuthExtensions.cs`: `Microsoft.Identity.Web` API surface and version, and its transitive OpenID Connect package on net10.0.
2. `Providers/OpenXmlTemplateEngine.cs` and `TextExtractor.cs`: Open XML SDK 3.x and PdfPig member names (`SdtElement`, `SdtAlias`, `ExtendedFilePropertiesPart.Properties.Pages`, `Page.GetWords()`).
3. Package versions: `PdfPig 0.1.10`, `Microsoft.Identity.Web 3.14.1`, `DocumentFormat.OpenXml 3.3.0` and the test packages were chosen from memory.
4. EF Core query translation on a real provider: `List<Guid>.Contains` in the access filter, `StartsWith` on the contract number, the DateTime UTC value converter, `DbSet` properties declared as `=> Set<T>()`.
5. ASP.NET details: two `POST admin/templates` actions selected by `[Consumes]`, `IFormFile?` parameters, `WriteAsJsonAsync(object, Type, ...)` overload, `MapFallback("/api/{**path}")` next to `MapFallbackToFile`.
6. Integration tests: settings are passed as process environment variables and the database is a named shared-cache in-memory Sqlite; Development environment also turns on DI scope validation.
7. Nullable-reference warnings are likely; warnings are not treated as errors.

Behaviour to check on the first real run: SQL Server `EnsureCreated` against the monsterasp database, the session cookie behind their HTTPS front end (`Auth:Cookie:AllowInsecure` is the escape hatch), startup time of the first seed (46 small files are written), heuristic extraction quality on real PDFs.

### Known gaps
- No migrations: a schema change needs a new database (or manual SQL) until migrations are introduced.
- Money totals use fixed placeholder FX rates in `CurrencyRates`.
- Filtering, sorting and paging of contracts happen in memory after the access filter (fine for about 800 contracts).
- Retention rules are stored and editable but nothing acts on them yet.
- No endpoint for legal entities or users/memberships in admin (the brief lists none); they come from the seed or the directory.
- Uploads are buffered in memory and pending intake uploads are stored in the database (25 MB limit; cleaned after 7 days).
- Moving a contract to another entity or department does not move its stored files.
- Under IIS in-process hosting, uploads above about 28 MB are rejected by the IIS request limit unless `web.config` raises it.
