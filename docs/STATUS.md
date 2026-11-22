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

## Frontend phase 2 (2026-11-22)

Scope: `src/web` only. Not committed.

### How to run (changed)
```
cd src/web
npm run dev          # mock API in the browser, http://localhost:5173
npm run dev:real     # vite --mode real: proxies /api to http://localhost:5080 (works on Windows; no inline env vars)
npm run build        # real API mode; the bundle contains no mock code and no seed data
npm run build:demo   # vite build --mode demo: static demo with the in-browser mock
npm run lint
npm test
```
The API mode follows the Vite mode (`real`, `demo`, `production`) in `vite.config.ts`; `VITE_API_MODE` still overrides it. No `.env.*` files are used, because the root `.gitignore` ignores them. `VITE_MOCK_ACCESS_CODE=<code> npm run dev` makes the mock ask for an access code.

### Results of the last run here
- `npm run lint`: clean. `npm test`: 171 tests in 10 files pass. `npm run build` and `npm run build:demo`: succeed.
- Real build: no file in `dist/assets` contains the seed strings "Nordpolymer", "rubber compounds", `createSeed` or "Sample data" (grep). Demo build: the mock is its own chunk (`mockApi-*.js`, about 74 kB).
- Bundle (real mode): entry 251 kB (78 kB gzip), shared components 95 kB, and one chunk per lazy route (Settings 45 kB, Record 24 kB, New contract 23 kB, Renewals 9 kB, Approvals 8 kB, Overview 6 kB).
- Headless Chrome on the demo build at 360, 768 and 1440 px, 22 pages each (Home, register, record, record with Edit details open, New contract, Approvals queue and detail, Renewals by entity and by department, Overview, both no-access states, the ten Settings sections): no console errors or warnings and no horizontal page overflow. The timeline and tables scroll inside their own card by design. The check script is not in the repo.

### Built
- Approvals: queue with waiting time (new today / waiting n days / red from 7 days), decision panel with key-term tiles, "What differs from the DAFA standard" with severity pills, route as cards with avatars grouped per stage ("in parallel"), comment with Approve / Reject (comment required) / Delegate (user picker), link to the contract, empty state. Phone: queue first, then one approval with a back link and a sticky Reject/Approve bar; the tab bar is hidden on that view.
- Renewals: 12-month timeline, one lane per legal entity or per department (toggle), markers placed by date and colored by urgency, a "today" line, overlapping markers stacked in rows, arrow keys/Home/End between markers, selected-contract card with "Open contract" and "Record decision" (the existing RenewalDialog), list of all deadlines below. Under 720 px the timeline is replaced by the list as cards with a full-width action. Overdue deadlines are red, pinned at the left edge and first in the list. The top-bar entity scope filters the page.
- Overview: five KPI tiles (links into the register views), annual value by legal entity and contracts by type as plain HTML bar lists, expiring without renewal, change-of-control list, missing-data tile linking to the register's missing view. Users without access get a no-access page and the API is not called; a 403 from the API gives the same page.
- Settings (admin only, `/settings/<section>`, side navigation; a select under 1000 px): Legal entities (read-only, from lookups), Departments and Contract types (add, rename, delete), Approval rules (create/edit with criteria and ordered stages, role or specific person per approver, move/remove stages; delete; "Which rule applies?" tester on the preview endpoint), Reminders, Retention (one row per contract type, saved together), Templates (upload, download, edit, delete), Holiday cover, Audit log (filter by contract, 50 per page, state in the URL), Notifications outbox, and "Run jobs now". All forms validate before sending and show toasts.
- Sign-in: `GET dev/config` on start; access-code screen before the demo-user picker when required (code in sessionStorage, re-asked when the API stops accepting it); in Entra mode a 401 from `GET me` redirects to `/api/auth/login?returnUrl=<path>` with a guard against redirect loops (a second 401 within 15 seconds shows a "Sign in" link instead).
- Mock loaded with a dynamic import only in mock mode; routes other than Home and the register are lazy.
- Phase 1 gaps: the reminder preview and hint in New contract use `GET reminder-settings`; quick look and the renewal dialog read with `?peek=true`; "Open in SharePoint" unchanged (only with `storeUrl`).
- Phone layouts: Home tasks and register rows are separate cards with full-width actions; the register gets a sort select because the table header is hidden.
- Backend contract changes from the project manager, all nine: `X-Requested-With: dafa-contracts` on every request; `changeOfControl` nullable, shown as "Not set"; unknown `missingData` labels shown as they are; overdue renewals; `lockedFields` disables inputs in Edit details with the hint; `newEndDate` for Renew on a fixed-term contract, GiveNotice for open-ended contracts (a "Give notice" button on the record); delegate picker filtered and 400/409 toasts; upload type and 25 MB checks in the document dialog and New contract, Main document replacement hidden for non-admins after approval; 409 on approve/submit refetches and says "This was just updated by someone else." All are mirrored in the mock.

### Stubbed / assumptions
- Nothing here has run against the real API; the frontend was only read against the backend sources in `src/api`.
- The mock uses the backend's `ApprovedTerms.LockedFields` list and locks it for non-admins on every contract that is not a Draft.
- The delegate picker can only filter by role if lookup users carry `roles`, and can only exclude the submitter if the detail carries `submittedById` (both requested in DECISIONS.md). Without them it excludes yourself and the owner, and the API's 400 is shown as a toast.
- The mock's seeded templates download as .rtf; uploaded ones return the uploaded bytes.
- With `VITE_MOCK_ACCESS_CODE` set, the mock starts signed out on every reload.

### Known gaps
- A 409 on submit also covers "no approval rule matches" in the current backend. The toast then reads "This was just updated by someone else." followed by the server's own text, which is misleading for that case. A distinct status or problem `type` for a stale update would fix it.
- Because locked fields include the notice period, values and change of control, an owner can no longer complete missing data on an imported Active contract; Edit details shows those inputs disabled and only an administrator can fill them in. The "Complete" task on My work still points owners there.
- Deviations from the designs: see DECISIONS.md (timeline axis, list under the timeline, approver names on the route, Settings layout, phone approval comment field).
- The Settings dialogs (rule editor, template upload, holiday cover) were exercised in tests but not screenshot-checked at 360 px. Chrome only; no Firefox/Safari run, no screen-reader pass, no automated contrast audit.
- `:has()` hides the tab bar on the phone approval view; in a browser without it the Reject/Approve bar covers the tab bar instead.
- Retention saves changed rows one request at a time; if one fails the earlier ones stay saved (the list is refetched).
- The mock seed still has two in-progress approvals where the department head may also be the contract owner; only the ERP one was corrected to the substitute rule.

## Backend phase 2 (review fixes) (2026-11-22)

Scope: `src/api`, `tests`, `.github/workflows/deploy-test.yml`, `docs/DEPLOY_TEST.md`, `docs/RUNBOOK.md` (configuration keys and rule specificity), this file and `docs/DECISIONS.md`. `src/web` not touched. Not committed.

**Nothing here has been compiled or run.** There is still no .NET SDK in the workspace. Every changed file was read back and desk-checked, and brackets were checked with a script, but the first real compile and test run is the CI `api` job. Expect some compile errors and failing tests on the first run (most likely places are listed below).

**The data model changed** (new columns `Version`, `LastRenewedDeadline`, `TermKey`, `Attempts`; new unique indexes). There are no migrations: the test database must be recreated once before this version is deployed (`docs/DEPLOY_TEST.md`, "After an update that changes the database"). **The deployment now needs a sixth secret, `MONSTERASP_DEV_ACCESS_CODE`** (at least 12 characters); without it the deploy is skipped, and the app itself refuses to start in Dev mode outside Development.

### Findings from docs/REVIEW_BACKEND.md

| Finding | Result | What was done / why not |
|---|---|---|
| H1 Approved contract can be changed | Fixed | From submission on, the approval-relevant fields are locked for owners (409 `locked-fields`); administrators may change them; every update audits old and new value per field, administrator changes to approved terms get their own audit event. `lockedFields` on the detail. Draft stays fully editable. Empty locked fields can be filled in once (see frontend requests below). |
| H2 End date / notice period edit kills reminders | Fixed | When the computed notice deadline changes (edit, Renew, automatic roll-forward) the Notice*/Escalation rows are deleted and a stale decision on an Active contract is cleared, with an audit line. Reminder rows also carry the term. |
| H3 Sign-in mode fails open | Fixed | Unknown or missing `Auth:Mode` stops startup; Dev outside Development needs an access code of 12+ characters; `Seed:DemoData` defaults to false and never seeds unless the mode is Dev; wrong codes throttled (10 per 10 minutes per address); deploy workflow requires and writes the access code and no longer writes unused keys. |
| M1 No concurrency control on approvals | Fixed | Guid concurrency token on Contract, ApprovalInstance, ApprovalStep; decisions collide on the instance row and the loser is replayed from fresh rows (stage completion re-evaluated); double decision and double submit are 409; filtered unique index for one pending approval per contract. |
| M2 No separation of duties | Fixed | Owner and submitter are never approvers (substitute: entity manager, Group CFO, Group CEO, then an administrator; label records it); delegation cannot target owner, submitter or oneself and needs the Approver or Admin role; admin reassignment recorded distinctly; holiday cover never routes to owner or submitter. |
| M3 Submitter can steer the route | Fixed | Approval value is the larger of total and annual value; seeded NDA rule capped at DKK 1m; specificity documented in the runbook. Not done: rejecting a total below the annual value (not asked for). Existing databases keep their old NDA rule until an administrator edits it. |
| M4 Renewal decisions | Fixed | Renew once per term (`LastRenewedDeadline`) and only from the first reminder date on; fixed-term Renew requires `newEndDate`; LetExpire on auto-renew is 400. Residual: with a renewal period shorter than the reminder window, two Renew calls one after the other (not simultaneous) can each be valid for consecutive terms. |
| M5 Open-ended contracts | Fixed | GiveNotice at any time (NoticeGiven, end date = today + notice period); yearly review reminder 60 days before each anniversary. Gap: the planned review date is not shown on the record before it is sent. |
| M6 Documents: any type, replaceable after approval | Fixed | Allowlist by extension plus content signature, 25 MB, Main upload or new version of a Main document after submission is admin-only, same-name uploads are new versions (unique index, unique store keys, CreateNew), `X-Content-Type-Options: nosniff`. Admin template upload is not covered by the allowlist. |
| M7 Text extraction without limits | Fixed | Limits on file size, pages (300), characters (400k), decompressed .docx size (measured, not taken from the zip directory) and a 20 second budget; over a limit the result is "no suggestions" with a note. A parser stuck inside one page cannot be killed; the request returns but the thread runs on until it finishes. PdfPig's own memory ceiling on hostile PDFs is still unverified. |
| M8 Reminder idempotency | Fixed | Unique index (contract, kind, term key); one job run at a time per process; a failed part discards its changes; failed deliveries stay unsent and are retried (max 5). Limit: with two app instances in Graph mode a mail could still go out twice before the second instance's save is refused. |
| M9 Entra provisioning | Fixed (never run) | Unique index on the object id, race-safe upsert, e-mail linking only onto rows without an object id (also in the Graph directory sync), `Auth:BootstrapAdmins`. Provisioning was kept in the claims transformation rather than moved to OnTokenValidated. Unit-tested on Sqlite; never run against Entra. |
| M10 Principals without an app user | Fixed | Default authorization policy requires the app-user claim; `suppliers` and `approvals/preview` also load the profile; in Dev mode such a cookie is treated as no session and never replaces the access code. |
| L1 CSV formula guard | Fixed | Tab and carriage return added. |
| L2 Enums accept integers | Fixed | String-only enum converter; `Enum.IsDefined` checks where enums come in by other routes. |
| L3 "Today" is the UTC date | Fixed | Date in Europe/Copenhagen, UTC fallback. Not configurable. |
| L4 Over-long input | Partially | Counterparty name validated (300); role labels truncated. Other free-text columns are unlimited in the model; not every string was audited. |
| L5 Contract number retry | Partially | Retries (up to 4) only when the number is really taken, and the audit text follows. Not done: in SharePoint mode the file is still stored before the number is final. |
| L6 No throttling on the access code | Fixed | In-memory throttle; the cookie carries a fingerprint of the code, so changing the code ends old sessions. Behind a proxy all users may share one address. |
| L7 Concurrent same-name uploads | Fixed | See M6. A lost race leaves an orphan file in the store. |
| L8 Audit text shows linked contract number | Fixed | Only the kind is logged. |
| L9 Pending upload cleanup | Fixed | Deletes by id without loading content; 20 pending uploads per user. |
| L10 due90 list vs home tile | Fixed | Same rule in both. |
| L11 Obligation and approval dead ends | Partially | Obligation responsible must be able to read the contract; an approver who is inactive when the stage opens is replaced. Not done: holiday cover for steps that were already delegated or rerouted. |
| CSRF (needs verification) | Fixed | `X-Requested-With: dafa-contracts` required on every non-GET/HEAD `/api` request. |
| Parity 4 (LetExpire on auto-renew) | Fixed | 400. |
| Parity: `RenewalItem` nullability | Fixed | Non-nullable in the DTO. |
| Needs verification: multi-tenant sign-in, stale Dev cookie after a switch to Entra, claims transformation frequency, IIS request limit, PdfPig on hostile PDFs, SharePoint mode | Not fixed | Cannot be settled without running the system. Upload limit is now 25 MB (below the IIS default), which removes the mismatch in the messages. |

### Requests from frontend phase 2

| Request | Result | Notes |
|---|---|---|
| 1. Problem `type` on 409 | Done | `stale`, `no-approval-rule`, `no-approver`, `locked-fields`, `duplicate`, `invalid-state` (list in DECISIONS.md). |
| 2. `roles` on lookup users, `submittedById` on the detail | Done | |
| 3. Empty locked fields can be filled in once | Done, with one deviation | `lockedFields` lists only locked fields that have a value. Deviation: `annualValue` and `totalValue` are locked together as soon as either is set, because the approval value is the larger of the two; otherwise an owner could turn an approved 900,000 contract into a 50m one by filling in the empty field (finding H1 again). When both are empty they can be filled in. |
| 4. Seed: no owner or submitter as approver | Done | The seed starts its approvals through the engine, so the M2 substitution applies; an integration test checks every seeded step. The backend seed has no ERP approval (that is the frontend mock); here the affected ones were the TPE supply agreement and the rejected webshop subscription. |
| "Contract additions for backend" in DECISIONS.md | Met | The list has 13 entries (the request said 11): dev/config, access code header and body, auth/login, reminder-settings, peek, preview rule id/name, templates multipart and content, X-Requested-With, changeOfControl null and lockedFields with 409, newEndDate, negative daysToNotice with months=13, roles and submittedById, audit by contract. Two things the frontend should know: a throttled access code is 429 (not 401/403), and `.dotx` template uploads were not checked. |

### Tests added (not run)
`tests/Dafa.Contracts.Tests/ReviewFixTests.cs` (service level) and `ApiSecurityTests.cs` (HTTP), about 125 new test cases (85 test methods): one or more per High and Medium finding, the review's untested access paths (by-id endpoints without access, scoped search/export/home/renewals/overview, cross-user uploadId), CSRF header, deactivated user, access code and throttle over HTTP, Entra mode (dev endpoints 404, no demo seed), Entra provisioning. Changed existing tests: approval value basis, the obligation reminder test (an open-ended contract now also gets a review reminder), and the API test factory (sends the header, several configurations, all host tests in one xUnit collection so they do not run in parallel).

### Most likely to fail compilation or tests
1. `Providers/TextExtractor.cs`: `OpenSettings.MaxCharactersInPart` and the three-argument `WordprocessingDocument.Open` (Open XML SDK 3.3.0), `Task.WaitAsync`.
2. `Data/AppDbContext.cs`: the named, filtered unique index `HasIndex(x => x.ContractId, "IX_ApprovalInstances_OnePendingPerContract").HasFilter("[Status] = 0")` next to the plain index on the same column, on both providers; overriding `SaveChanges(bool)` / `SaveChangesAsync(bool, CancellationToken)`.
3. `tests/.../ApiIntegrationTests.cs`: the `CreateHost` override and several factories with different environment variables; `EntraModeApiTests` boots the host in Entra mode with a placeholder app registration, which has never been done (Microsoft.Identity.Web may reject the placeholder values at startup).
4. `Controllers/GeneralControllers.cs`: returning `ObjectResult?` from actions typed `ActionResult<T>`.
5. `Program.cs`: `new JsonStringEnumConverter(null, false)`, the inline `app.Use` overload, `AuthorizationPolicyBuilder.RequireClaim`.
6. Tests that interleave two requests (`InterleavingClock`) block on a second task inside the first; they assume the Sqlite calls complete synchronously.
7. Integration tests that depend on demo data details (first Production contract, the EPDM contract's supplier, obligations and links).
8. Nullable-reference warnings; warnings are not errors.

### Configuration keys (changes to the phase 1 table)
| Key | Default | Notes |
|---|---|---|
| `Auth:Mode` | none (`Dev` only in `appsettings.Development.json`) | must be `Dev` or `Entra`, otherwise the app does not start |
| `Auth:Dev:AccessCode` | empty | required (12+ characters) for Dev mode outside Development |
| `Auth:BootstrapAdmins` | empty | Entra mode: e-mail addresses / UPNs that become administrators at sign-in |
| `Seed:DemoData` | `false` (`true` in Development and on the test site) | only with `Auth:Mode=Dev` |

The deploy workflow now writes only `Auth:Mode`, `Auth:Dev:AccessCode`, `Database:Provider`, `ConnectionStrings:Default` and `Seed:DemoData`.

### Known gaps after this phase
- Still no migrations; this phase needs a new test database.
- The throttle and the job lock are per process; nothing is shared between app instances.
- A rejected or expired contract keeps its documents and approval history; reopening a Draft after rejection unlocks all fields again (by design: it must be re-approved).
- Moving the lock to "submission" means a rejected contract is the only way back to free editing; there is no "withdraw from approval" action.

## Frontend phase 3 (QA fixes) (2026-11-22)

Scope: `src/web` (source, tests, new `e2e/`), `docs/REVIEW_FRONTEND.md` (the independent review, committed as written), this file, `docs/DECISIONS.md`, and two deploy items (`.github/workflows/deploy-test.yml`, `docs/DEPLOY_TEST.md`). `src/api` not touched. The work was started by one engineer and finished by a second; the table reflects the state after both.

### Results of the last run here

| Command (in `src/web`) | Result |
|---|---|
| `npm run lint` | exit 0 |
| `npm test` | exit 0, 11 files, 202 tests |
| `npm run build` | exit 0, entry 254 kB (78.6 kB gzip) |
| `VITE_API_MODE=real npm run build` | exit 0; `grep -rlE "Nordpolymer\|createSeed\|rubber compounds\|Sample data" dist` finds nothing; no `mockApi-*` chunk |
| `npm run build:demo` | exit 0, `mockApi-*` chunk 78 kB |
| `npm run e2e:demo` | exit 0: passes at 390 and 1440 px, motion on (headless Chrome for Testing 153 via playwright-core) |

`npm run e2e:demo` builds the demo, serves it with `vite preview` and runs one journey per width: sign in as a reader, switch user (shell name and "New contract" must change), create a contract from the Supply agreement template (the counterparty field must keep what is typed; one "Submit for approval"), switch to an approver and approve (at 390 px the Reject/Approve bar must be `position: fixed`, at the bottom edge, inside the viewport, with no transformed ancestor, also at 390x560 after scrolling, and must not cover Delegate), sign out (sign-in screen, no shell). It was checked once against a deliberately re-introduced H3 (fill mode `both`): it fails with "Ancestors of the fixed bar that un-pin it: approvals__body". It needs a local Chrome (`CHROME_PATH`) and is not run in CI.

### Findings from docs/REVIEW_FRONTEND.md

| Finding | Result | Note |
|---|---|---|
| C1 Counterparty cannot be typed into | Fixed | The form sends one patch per user action (`onPatch`), so the name and the matched supplier are set together. Unit tests on both paths and in Edit details; e2e types into it. |
| H1 User switch keeps the old shell | Fixed | `me` is set from the `dev/login` response, the previous user's cache is dropped and the shell is keyed on the user id. Tests in both directions (reader to admin, admin to reader); e2e. |
| H2 Sign out does not sign out | Fixed | Cache dropped and `me` reset; the 401 shows the sign-in screen. Entra mode shows "You are signed out" instead of redirecting back at once (unit test with a fake API; never run against Entra). |
| H3 Phone Reject/Approve bar not pinned | Fixed | No entrance animation keeps a transform (`backwards` fill), Approvals fades without a transform, `.decide` has bottom padding for the bar. Measured with motion on: bar at y 763–844 of 844 (390 px) and 659–740 of 740 (360 px); Delegate receives the click. |
| M1 Entity filter not in the URL | Fixed | `?entity=<id>`, survives reload and in-app navigation. |
| M2 Register at 768 px cuts off Status | Fixed | 720–1000 px shows Contract, Notice and Status (no horizontal scroll, table 504 px in a 504 px card); the top bar is one row (72 px). Entity and Owner are not shown at this width. |
| M3 Renewals offers a decision the user may not take | Fixed | "Record decision"/"Decide" only for administrators and people on the contract; others get "Open contract". Costs one extra list request (see DECISIONS.md). |
| M4 Selected card below the fold at 1440x900 | Fixed | Lanes 44 px; timeline card 486 px high, selected card at y 734–850. |
| M5 29 tab stops on the timeline | Fixed | Roving tabindex (one stop). |
| M6 Edit details opens past the locked notice on a phone | Fixed | Focus goes to the dialog when the first field is locked; sheet opens at scroll 0 (checked at 360 px). |
| M7 "across 9 legal entities" for everyone | Fixed | Counts the entities of the visible contracts. |
| L1 `?page=99` shows the empty state | Fixed | Goes to the last page. |
| L2 Captions that do not fit the state | Fixed | |
| L3 Phone cards named "Preview" | Fixed | "Open <title>" on a phone. |
| L4 Phone toasts over sheets | Fixed | Top of the screen, newest only. |
| L5 11 px text | Fixed | Small avatar initials and the file tile are 12 px. |
| L6 Sign date error only as a toast | Fixed | Server refusal shown at the field. The Renew dialog still shows its inline error; the duplicate toast for a client-side error was not re-checked. |
| L7 "One field ... They are" | Fixed | |
| L8 Delegate picker offers approvers of the same contract | Fixed | Excludes everyone already in the same stage (the API's rule). Approvers of later stages are still offered, as the API allows them. |
| L9 Seed: approval log without an approval | Fixed | The flagship contract has its finished approval; imported ones say "Registered as approved". |
| L10 Mock signs back in after sign-out | Fixed | |
| L11 No skip link | Fixed | |
| L12 Hint on a locked Counterparty | Fixed | |
| L13 Submit on a record without confirmation | Fixed | Confirmation dialog with the route. |
| L14 Timeline track scrolls 12 px | Fixed | scrollWidth = clientWidth (908) at 1440. |
| L15 Disabled placeholder contrast | Fixed | #5b6672 on #f4f6f9. Computed, not measured in the browser. |
| Design: phone Home | Fixed | Task-first: date, "My work", avatar; compact hero with one line; 44 px segmented control; first task card at about y 280 of 844; route tiles after the tasks; icon tiles tinted by urgency. |
| Design: phone approval view | Fixed, one deviation | No top bar or tab bar, 2x2 term tiles, one-sentence route, fits 390x844 without scrolling, pinned bar. Deviation kept: comment field and Delegate above the bar. |
| Design: Home 1440 urgency tint | Fixed | |
| Design: Contracts 1440 | Fixed | Rows 55 px (15 rows measured), Contract column 40% with one-line meta, four chips on one row under the views, Export beside New contract in the top bar. |
| Design: Record 1440 | Fixed | 15 cells in five full rows (Contract no. and Next reminder added), no trailing hairlines, "14 Feb 2024 · both parties". |
| Design: New contract 1440 | Fixed, one point open | One "Submit for approval" (page only), value-band chips. Not changed: error toasts on desktop still appear bottom right for 8 s and can lie over the right-hand column (they can be closed). |
| Design: Approvals 1440 | Fixed | Five term tiles in one row, route cards top-aligned with their own height, the substitute explanation on its own line (two lines max). |
| Design: Renewals 1440 | Fixed | M4, and overdue markers have their own "Overdue" column. |
| Design: Overview 1440 | Fixed | KPI labels on one line ("Notice within 90 days"), columns stretch to the same height. |
| Design: Settings rule dialog on a phone | Fixed (not re-checked in the browser) | The subtitle scrolls with the body under 720 px. |
| Design: 768 px top bar | Fixed | 72 px on every shell page. |

### Backend alignment (mock, types, client)

Done, per the "Contract additions for frontend" list of backend phase 2: problem types and their messages, 429, `lockedFields` semantics and lock from submission, `roles`/`submittedById` and the delegate filter, `AnnualReview`, open-ended GiveNotice only, Renew window (the 409 detail stays in the dialog), `note` on extract, `annualReviewReminders`, 25 MB + allowlist (already there), `approvals/preview` value = max(total, annual), `due90` without decided contracts, non-null renewals rows. Enums were already sent as strings. None of this has run against the real API from here.

### Verified in a browser (headless Chrome, motion on, mock API)

- 390 and 1440 px: the e2e journey above. Screenshots looked at: sign-in, Home (reader and owner), New contract steps 2 and 4, the new record, the approval view, signed-out screen.
- 360 px (Mette): Home, Contracts, Renewals, record, Edit details; approval view as Rasmus (bar pinned, Delegate clickable after scrolling).
- 768 px: Home, Contracts, Approvals, Renewals, Overview, record, New contract, Settings: no horizontal page overflow, top bar 72 px.
- 1440 px: Home, Contracts, Approvals, Renewals (marker selected), Overview, record, Edit details as administrator, New contract; compared with the designs by eye.

### Known gaps

- Not verified in a browser in this phase: Settings dialogs, the Submit confirmation dialog, the Renewal dialog's 409 text, the "note" under an unreadable upload, the skip link (all have unit tests or are small CSS changes).
- Nothing was run against the real .NET API, Firefox, Safari or a real phone.
- Tablet register (720–1000 px) has no Entity or Owner column.
- Desktop error toasts can still cover content at the bottom right (New contract step 4).
- `e2e:demo` is not in CI.
- The value-band chips in New contract are hard-coded to the seeded rule limits.

## Backend release 3 (2026-11-22)

Scope: `src/api`, `tests`, `docs/RUNBOOK.md`, `docs/DEPLOY_TEST.md`, this file and `docs/DECISIONS.md`. `src/web` not touched.

Committed in 9bc0165 + af2fcf7; CI run 45036791674 passed with 227 backend tests.

**The data model changed again, but the test database does not have to be recreated**: a new start-up schema upgrader adds the new tables and columns in place (see "Schema upgrade" below and `docs/DEPLOY_TEST.md`).

### Built, by item

1. **Analytics for every user.** `GET overview/analytics?<ContractFilter>&ddThresholdDkk=` (Core `AnalyticsService`): KPIs, buckets by entity/department/type with share %, top 10 counterparties with concentration %, 24 notice months by urgency (decided/high/medium/low; overdue undecided in the current month), 8 expiry quarters, status funnel, data-quality score per entity, due-diligence lists (change of control, exclusivity, above threshold, missing signed document). `GET overview` no longer has a role gate (scoped). The shared filter (`ContractFilterEngine`, `ContractFilterParser`) is used by `GET contracts`, `GET contracts/export` (optional `columns`), analytics and the data room: access filter plus ids/entity/department/type/status/counterparty/owner/person/tags/watching/custom fields/flags in SQL, calculated criteria (notice, DKK value bands, missing data, text) in memory. Drill-down = `GET contracts` with the same filter (paged, sortable; unknown sort is 400).
2. **Search.** `GET search/full` (contracts, documents, people, counterparties; every term must match; snippets with `[start, end)` highlights; filters; paging per group) and `GET search` (palette: 4 contracts, 2 documents, 2 people, then suppliers). Document text: table `DocumentTexts` (text and a lower-cased copy, max 400,000 characters), filled at upload with the bounded `TextExtractor` and backfilled by the `UploadCleanup` job (100 documents per run). Contains-matching in SQL (no full-text catalog), scoped to visible contracts, capped at 1000 documents per query; snippets read only a 400-character window of the text. `GET people/{id}`, `GET counterparties?q=`, `GET counterparties/{id}`.
3. **Import framework** (`ImportService`, Infrastructure `TabularFileReader`): fields per kind (contracts include custom fields), CSV template, upload (.csv with separator detection and quotes, .xlsx first sheet with shared strings and date cells), suggested mapping, preview of every row (Create/Update/Skip/Error with errors and warnings, nothing written), run (each row on its own, session runs once), history. Counterparties, users (roles, departments, managed entities, group management; Entra-linked users' synced fields not overwritten) and contracts (Draft or Active, skip approval, required-field errors, missing-data warnings, Active with a past end date becomes Expired, new counterparties on request, field-rule defaults, tags, custom fields). Counterparty master: list, create, update (rename updates contracts), deactivate, duplicates (VAT, external id, normalised name), merge (re-points contracts, deactivates sources).
4. **Sync sources** (`SyncService`; Infrastructure `DemoSyncSourceReader` and `LiveSyncSourceReader`): Power BI (workspace, dataset, DAX or table) and SQL (named server-side connection, SELECT only, read-only transaction, 60 s); mapping and options as imports; schedule Off/Hourly/Daily/Weekly (Europe/Copenhagen); test without saving; run now; last 50 runs; failed runs notify admins (`SyncFailed`). No secret in the database or the API. In test mode (`Sync:Provider=Demo`, default) test and run return demo rows and two demo connections are offered.
5. **Admin customisation.** Custom fields (Text, Number, Money, Date, YesNo, Choice, MultiChoice, Person; per contract types; required, help, order, register/filter flags; EAV table `CustomFieldValues`, filterable in SQL on both providers) validated on create, Draft update and submit, shown in detail/list/export/analytics filters; field rules per type (required optional standard fields, defaults used by imports); legal entities (name, country, currency, active); currencies with DKK rates (used by every DKK figure, including approval value and preview); tags (CRUD, assign, bulk); users (list, create, edit roles/memberships/group management/active; synced fields read-only for Entra-linked users; no self-demotion); effective access (visible contracts with every granting rule); bulk reassignment with preview (skips contracts in approval, ended unless asked, owner without the role, and separation of duties: never makes an open approver the owner); notification settings per event (enabled, Email/Teams/InApp, recipient rules, extra recipients, `{{placeholder}}` templates validated per event, preview, rendered by the new `NotificationDispatcher`), reminder schedule per contract type (used by notice reminders, escalation and the renewal window), job schedules with last run (`JobRunner`; hosted service checks every 5 minutes), general settings (app name, formats, landing page per role, feature toggles `aiSuggestions`/`templates`/`eSign` enforced on intake and templates, session timeout, data-quality fields, due-diligence threshold). All admin changes are audited.
6. **Collaboration.** Saved views (personal/shared, columns validated, query stored, default view), tags on contracts, watch/unwatch and `GET watching` (watchers get in-app `WatchedContractChanged` for status changes, renewal decisions, edits, uploads and comments, not for their own actions; preference `autoWatch`), comments with mentions (mentioned users must see the contract; in-app + e-mail per settings), in-app notification centre (list with unread count, unread count, mark read, mark all read; notifications about contracts the user can no longer see are hidden), preferences (`GET|PUT me/preferences`) and `GET app-config`, digest job (daily/weekly per preference; only visible contracts; nothing sent when empty), version comparison of key terms rebuilt from the audit trail (contract updates now store structured old/new values in `AuditEvent.DataJson`; release 2 texts are parsed), data room export (Admin, group or entity management; zip with `index.csv`, `manifest.json` with SHA-256 and the permitted documents; 5000 documents / 2 GB; kept 7 days; audited), bulk actions (add/remove tag, reassign, request data completion).
7. **Schema upgrade**: see below.

### Schema upgrade

`Infrastructure/Data/SchemaUpgrader.cs`, run by `DatabaseInitializer` right after `EnsureCreated` on every start, on SQL Server and Sqlite. A `SchemaVersion` table records numbered steps; each step only creates missing tables, adds missing columns (NOT NULL columns get a default) and creates missing indexes, every statement behind an existence check. The DDL is generated from the EF Core relational model (store types, keys, index names and filters), not hand-written. Step 1 = phase 2 baseline, step 2 = release 3. Failed table/column DDL stops the start; a failed index is a warning. After the steps, anything in the model that is still missing is logged as `Schema drift:`. Tested on Sqlite: an old database (a release 2 style `LegalEntities` table with a row) is upgraded, the row keeps its data and gets `IsActive = 1`, a second run does nothing; on a fresh `EnsureCreated` database only `SchemaVersion` is created. The SQL Server path is desk-checked only. EF Core migrations were not introduced (decision below).

### Tests added (not run)

`Release3Tests.cs` (service level, 22 tests): analytics scoping for admin, department member, entity manager and outsider, filters and value bands; document text search scoped by user, multi-term matching, snippets and highlights; backfill; palette; person and counterparty pages scoped; counterparty import (column detection, preview writes nothing, duplicates in the file, run once), update and skip modes; contract import (required fields, owner role, Draft/Active, Expired for past end dates, new counterparties, warnings, no approval); unmapped required fields; merge re-points contracts; custom field validation (type, option, applicable type, required), storage, full-set update and SQL filters; field rules; effective access compared with the access service; bulk reassignment with skip reasons and separation of duties; data room holds only permitted documents and is refused to a reader; comments, mentions and watcher notifications; version comparison; tags and saved views; notification templates (validation, preview, rendering, channel switch); schedule calculation; schema upgrader on an old database run twice and on a fresh database. `Release3ApiTests.cs` (HTTP, 8 tests on the demo seed): schema versions recorded at start-up; analytics open to every user and scoped (thomas, nina, mette), repeated filter parameters, unknown sort 400; document text search after running the `UploadCleanup` job, scoped; person and counterparty pages for a user without access; data room 403 for a planner and only Sverige contracts for karin; counterparty import over HTTP (admin only, runs once with 409 `import-state`); sync source with the demo reader (test, 400 for a non-SELECT query, run, history); app-config, preferences and jobs. Changed existing tests: the three assertions that expected 403 on `GET overview` for non-management users now expect a scoped 200 (contract change 0.2a); `TestWorld` wires the release 3 services.

### Configuration keys (release 3)

| Key | Default | Notes |
|---|---|---|
| `Jobs:TickMinutes` | `5` | how often the hosted service checks which jobs are due. `Jobs:IntervalMinutes` is no longer read (each job has its own schedule in Settings > Jobs). `Jobs:Enabled`, `Jobs:StartupDelaySeconds` unchanged |
| `Sync:Provider` | `Demo` | `Demo`: sync sources return generated rows (test site). `Live`: real SQL / Power BI queries |
| `Sync:Connections:<name>:Kind` | none | `Sql` or `PowerBi`; lists the connection in `admin/sync-connections` |
| `Sync:Connections:<name>:ConnectionString` | none | SQL sources; falls back to `ConnectionStrings:<name>`. Never returned by the API. `Default` (the app's own database) is refused |
| `Sync:Connections:<name>:Description` | generated | shown in the connection picker |
| `Suppliers:PowerBi:TenantId`, `ClientId`, `ClientSecret` | none | Power BI token for sync sources; without them the `AzureAd:*` app registration is used |
| `Suppliers:PowerBi:UseManagedIdentity`, `ManagedIdentityClientId` | `false`, none | App Service managed identity for Power BI instead of a secret |
| `DataRoom:LocalPath` | `App_Data/dataroom` | data room zips (kept 7 days) |
| `App:BaseUrl` | empty | now also used for `{{contract.url}}` in notification templates |

### Contract alignment with `docs/CONTRACT_V3.md`

Every route in section 13 exists with the documented roles and shapes (`src/api/Dafa.Contracts.Api/Controllers/Release3Controllers.cs`, plus changes in `ContractsControllers.cs` and `GeneralControllers.cs`). Item by item (mismatch numbers refer to the list below): §0 ground rules met; §1.1-1.7 met (1.1 see 14); §2 met with 1, 2; §3 met with 4; §4 met; §5 met with 3; §6 met with 5, 8, 9, 16; §7 met with 7; §8 met; §9.1-9.8 met with 6, 7, 10, 12; §10 met; §11 met with 13; §12 met with 10, 11, 12, 13; §13 met (all routes and roles); §14 met plus the extra actions in 15; §15 (mock) is frontend, not touched.

### Contract mismatches (release 3)

Deliberate or known differences from `docs/CONTRACT_V3.md` / `types.ts`:
1. Malformed filter values (an id that is not a Guid, a date that is not `yyyy-MM-dd`, a boolean that is not true/false, an unknown `valueBand` or `dateField`) are 400 on that parameter; well-formed unknown ids still match nothing. An unknown `cf.<key>` matches nothing.
2. `cf.<key>.min` / `cf.<key>.max` range filters for Number, Money and Date are accepted in addition to the equality filter.
3. `missingSignedDocument` counts Active and NoticeGiven contracts (spec text); the `types.ts` comment also mentions Approved.
4. Document search counts at most 1000 matching documents per query (`documents.total` is capped there).
5. `WatchedContractChanged` is delivered in-app only (and summarised in the digest); Email/Teams channels selected for that event are not sent. Its default settings are InApp only, so the default matches.
6. Recipient rules `Approver` and `Mentioned` resolve to the people the event is about (they have no other source). People added by rules must be able to see the contract; administrators always can.
7. `sessionTimeoutMinutes` sets the cookie lifetime at Dev sign-in; the sliding renewal still uses the fixed 12 hours, and Entra sign-in is not affected yet.
8. Version comparison: text values longer than 80 characters are compared as their first 80 characters (the audit trail stores them that way); field keys are the contract body names (`counterpartyName`, `typeId`, ...), plus `status` and `cf.<key>`. Custom field history exists only from release 3 on.
9. `POST data-room/exports` builds the zip in the request and answers 202 with status `Ready` (or `Failed`); there is no `Queued`/`Running` phase. `GET data-room/exports/{id}` of another user's export is 404 for management users and 403 for others. Downloads are audited as `dataroom.downloaded`.
10. `POST admin/sync-sources/{id}/run` and `POST admin/jobs/{key}/run` always run synchronously (no switch to `Running` after 120/60 s).
11. In `Sync:Provider=Demo` mode `GET admin/sync-connections` also lists `DemoErp` (Sql) and `DemoPowerBi` (PowerBi).
12. `syncedFields` on admin users depends on whether the user is linked to Entra ID (has an object id), not on the sign-in mode. The department head flag is not exposed by `AdminUser`; new memberships are not head, existing ones keep their flag.
13. Imports do not change the status of existing contracts and never apply `autoWatch`. Validation errors in an import or sync input (mapping, options) are 400 before anything runs; a sync source whose query fails at run time gets a `Failed` run.
14. `ContractListItem.customFields` holds every active custom field with a value (not only `showInRegister` ones).
15. Extra audit actions: `import.counterparty-created|updated`, `import.user-created|updated`, `settings.sync-sources`, `dataroom.downloaded`.
16. Home tasks do not include watched contracts (the task brief asked for it; the contract's `HomeTask.kind` has no such kind, so it was left out rather than breaking the union).

### Most likely to fail compilation or tests

1. `Infrastructure/Data/SchemaUpgrader.cs`: the EF relational-model API (`IDesignTimeModel`, `GetRelationalModel()`, `ITable.Columns/PrimaryKey/Indexes`, `IColumn.StoreType/PropertyMappings`, `ITableIndex.Filter`), and the generated DDL on SQL Server (only Sqlite is covered by tests).
2. `Infrastructure/Providers/TabularFileReader.cs`: Open XML SDK 3.x spreadsheet members (`CellValues` comparisons, `NumberingFormat`, `CellFormat.NumberFormatId`).
3. EF query translation on both providers: `string.IndexOf`/`Substring` in `SearchService.DocumentSnippetAsync`, the left join plus per-term `Contains` in `SearchService.MatchDocumentsAsync`, captured `IQueryable` subqueries (`values.Any(...)`, `visible.Contains(...)`) in `ContractFilterEngine`, `CollaborationService` and `DigestService`.
4. `Infrastructure/Providers/SyncReaders.cs`: `Microsoft.Data.SqlClient` (transitive via the EF SqlServer package), `BeginTransactionAsync` cast to `SqlTransaction`.
5. `Core/Services/ImportService.cs` (large: closures that reassign captured variables, the `ValueParser`) and `Core/Services/CustomFieldService.cs` (JSON value parsing).
6. `Api/Controllers/Release3Controllers.cs`: route overlaps (`admin/jobs/run` vs `admin/jobs/{key}/run`, `admin/custom-fields/order` vs `{id:guid}`), `[FromQuery] FullSearchQuery` with `DateOnly?`.
7. `Program.cs`: `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes` (needed because release 3 DTOs have non-nullable strings).
8. Tests that depend on demo-seed details (`Release3ApiTests`: the seeded document text "stands in for the signed agreement", supplier S-10010, users thomas/nina/karin) and on the exact count of 13 notification events and 8 jobs.
9. Nullable-reference warnings; warnings are not errors.

### Not done / known gaps

- No EF Core migrations (the upgrader replaces them for additive changes; renames or type changes need a new approach).
- Teams delivery is a stub (outbox rows with channel `Teams`), as before.
- Email for `WatchedContractChanged` (see mismatch 5); Home tasks for watched contracts (mismatch 16).
- Background (asynchronous) data room builds and sync runs; very large exports run inside the request.
- Retention rules still do nothing.
- Per-type reminder schedules cover notice offsets and escalation; obligation offsets stay global (the contract has no field for them).
- The SQL and Power BI live readers and the Power BI managed-identity token have never run.

## Frontend release 3 (2026-11-22)

Scope: `src/web`, `docs/CONTRACT_V3.md` (new), this file and `docs/DECISIONS.md`. `src/api` not touched. Not committed.

### API contract
`docs/CONTRACT_V3.md` + the "Release 3" section of `src/web/src/api/types.ts`, the `Api` interface (`api.ts`) and the fetch client (`client.ts`) were written first, before any UI, for the backend engineer working in parallel. All earlier endpoints keep their meaning; two intentional changes: `GET overview` is open to every user (scoped), and `GET contracts` accepts repeated and new filter parameters. Nothing has been run against the real API.

### Built, by item of the brief
1. **Full width.** The 1240/1180 px page and flow containers are gone; gutters are `--gutter` (20–32 px). Wide screens get more: the register shows 5 / 8 / 12 default columns at ≤1700 / ≥1700 / ≥2200 px, Overview charts are 2–4 columns, search groups 2–3 columns, asides are fixed at 400–460 px from 1700 px so the main column takes the rest. Long text uses `.prose` (72ch). Checked at 1440, 1920 and 2560 px: no horizontal page overflow.
2. **Overview for every user.** No access gate (nav item for everyone). Filter bar (entity = the shell's entity scope, department, type, status, counterparty search, owner, value band, date presets or custom range on notice/end/start/signed/created, custom fields marked "filter", include ended) in the URL; breadcrumb of drill-downs with remove and Clear all; 7 KPI tiles (4 drill down); charts in plain HTML/CSS: annual value by entity, department, type, top 10 counterparties with concentration %, notice deadlines by month for 24 months stacked by urgency (palette run through the dataviz validator: CVD separation passes; the gray "decided" segment fails the chroma floor by design and the lightest two are below 3:1, so every segment has a tooltip/aria-label and the list below is the table view), contracts ending by quarter (8), status, data-quality score per entity. Every bar, segment, column and KPI applies its filter to the whole page and the matching contract list below (sortable, paged, "Export to Excel" = CSV with the current filter). Due diligence card: change of control, exclusivity, above an adjustable threshold, no signed (Main) document, each with "Show in the list". "Data room export" dialog (admins, group and entity management) creates, polls and downloads the zip.
3. **Search.** `/search` page across contracts, documents (file name and text, highlighted snippet, download), people and counterparties, grouped with counts, filters person/entity/department/type/document kind/uploaded from–to, paging per group. Ctrl+K palette now also returns documents and people and ends with "See all results"; Enter opens the search page. Person page `/people/:id` ("Contracts of <name>": counts per role, value, list) reachable from owner names in tables, the record's People card, comments and search. Counterparty page `/counterparties/:id` (contracts, totals, renewals, documents), linked from the record header, tables and search.
4. **Settings** (now grouped Organization / Data / Rules / System, 21 sections, all earlier ones kept): legal entities editable (name, country, currency, active); departments and types gain Deactivate/Activate; currencies with DKK rates; tags; users and access (list, edit roles/departments/managed entities/group management/active, Entra-synced fields shown read-only with a tag, "Effective access" with the reason per contract, bulk "Reassign contracts" with preview); counterparties (search, paging, create/edit, deactivate, find duplicates and merge); imports for counterparties, contracts and users (upload CSV/XLSX → column mapping with examples → check with per-row action, errors and warnings → run → result, template download, history); sync sources (Power BI dataset with DAX or table, SQL with a server-side connection name + query, mapping, schedule, Test connection with sample rows, Run now, run history); custom fields (8 types, per contract type, required, help, order, register column, filter, active); field rules per type (extra required standard fields, defaults); notifications (per event on/off, channels, recipients, extra people, subject/body with placeholder chips and a server preview, digest default, reminder schedule per type); jobs (schedule, Run now, last run status); general (name, formats, start page per role, feature toggles, session timeout, data-quality fields, due-diligence threshold).
5. **Extras.** Saved views (personal/shared, columns + filters + sort, update, delete, personal default applied when the register opens without filters) and a column chooser incl. custom fields; bulk bar on selected rows (export selected, add/remove tag, reassign a role, request data completion, failures listed); tags on the record (edit) and in tables; Watch/Watching on the record and a "Watching" card on My work; Comments tab with @mention autocomplete (only people who can see the contract are accepted by the API); notification bell with unread badge (polled every 60 s), unread filter, mark all read; "My settings" page (`/preferences`: start page, default entity scope, auto-watch, digest, per-event channels); "Compare versions" dialog on the Activity tab; data room export (see 2). Custom fields render in New contract (with type defaults), Edit details, the record ("Additional details"), register columns and filters.
6. **Mock.** `src/api/mock/r3.ts` implements every new route with the same access filter; `seedR3.ts` adds tags, 6 custom fields with values, field rules, comments, notifications, watchers, two views, a sync source with runs, jobs, settings, document text for search, two duplicate counterparties and version history on the flagship contract. Pure parts are separate and tested: `query.ts` (filter), `analytics.ts`, `importer.ts` (CSV, a minimal XLSX reader, mapping, parsing, row plans), `zip.ts`.

### Results of the last run here (src/web)
| Command | Result |
|---|---|
| `npm run lint` | exit 0, no warnings |
| `npm test` | exit 0, 14 files, 250 tests (new: `api/mock/r3.test.ts` 29, `lib/r3.test.ts` 12, `pages/r3.test.tsx` 8) |
| `npm run build` | exit 0, entry 272 kB (83.6 kB gzip), Settings chunk 127 kB |
| `VITE_API_MODE=real npm run build` | exit 0; no `mockApi-*` chunk; `grep -rlE "Nordpolymer\|createSeed\|rubber compounds\|Sample data\|FSC certified\|Vejle Gummi\|extendSeed" dist` finds nothing |
| `npm run build:demo` | exit 0, `mockApi-*` 189 kB |
| `npm run e2e:demo` | exit 0 at 390 and 1440 px, motion on: the phase 3 journey plus a release 3 journey per width — Overview as Rasmus (44 contracts, drill-down Purchasing → 11 in KPIs and list) and as Emma (2 contracts, one department bar), search as Jonas finds Camilla's "Packaging supply – signed.pdf" by words in the text; at 1440 also Peter imports a supplier CSV (1 created, 1 skipped) and finds it in Counterparties, then adds a custom field that appears in New contract. |

### Looked at in the browser (headless Chrome, mock)
- 1440: Overview (CFO), search, register, record, all new Settings sections, compare dialog, bell, bulk bar, column chooser, data room dialog, import mapping, New contract with a custom field.
- 1920: register, record, Overview, Home, Approvals, Renewals.
- 2560: Overview, register, Home.
- 390: Overview (CFO and reader), search, record comments, Settings imports and users, person, counterparty, register, My settings, data room sheet.
Fixed after looking: KPI row wrapping at 1440, list-header layout, register control rows, a class clash that broke the Home month chart (`.bars`) and a legend clash, the import drop zone, the counterparty date column, job action buttons wrapping, select checkboxes on phone cards, wide settings tables squeezed on a phone (now scroll), KPI tiles one per row on a phone, the person-page Back button, raw numbers in the comparison, the bulk bar scrolling away (now sticky), highlighted names losing their space for screen readers.

### Not done / deviations / known gaps
- Nothing ran against the real .NET API; CONTRACT_V3 is a proposal the backend may report mismatches on.
- General settings: `appName`, `dateFormat`, `numberFormat` are stored and returned but the UI still uses its fixed formats and name; `sessionTimeoutMinutes` is for the server. The "templates" and "AI suggestions" toggles are enforced by the API (mock: no templates / no suggestions); the UI does not hide the options. E-sign is a disabled placeholder button on an approved record.
- The digest and Teams delivery are not built (outbox rows only, as before); the mock's "Digest" job does nothing.
- The New contract approval preview and value-band chips still use the indicative FX rates in `lib/format.ts`, not the admin currency table (the API uses the table).
- The mock XLSX reader (DecompressionStream + DOMParser) is not covered by a test and was not tried with a real Excel file; CSV is tested.
- Mock document search uses generated text for seeded files and the text of uploaded `.txt` files only.
- Version comparison in the mock knows the seeded history and changes made through Edit details; renewals and roll-forwards are not recorded as key-term changes.
- Overview's counterparty filter is a type-ahead over the first 20 matches; the register has no counterparty chip (it accepts `counterparty=` from links).
- Tooltips on chart marks are CSS hover/focus tooltips; on touch devices the value is in the accessible label and the list, not in a tap tooltip.
- No screen-reader pass, no Firefox/Safari/real phone run; `e2e:demo` is still not in CI.

## Frontend fix: filters can be removed again (2026-11-22)

Scope: `src/web`, this section. `src/api` not touched.

Owner's report: "If I add an entity to the overview, I cannot remove it from the filters again."

**Root cause.** The top-bar entity scope (`ScopeProvider`, `app/context.tsx`) kept its own copy of `?entity=` in component state and had an effect that wrote that copy back into the URL whenever the parameter was missing. Overview's entity filter is the same `entity` parameter. React Router applies URL changes in a transition, so when a removal cleared the copy (`adopt('')`), the next render still saw the old URL and re-adopted the entity; when the URL change landed, the effect put `?entity=` back. Every removal path for the entity (breadcrumb, Clear all, filter bar "All", top bar "All entities", browser back) was undone, on Overview, the register and the search page.

**Fix.** The URL is now the only state for the scope: `ScopeProvider` reads `entity` from the URL; it only remembers the last value per path and carries it over when the user moves to a *different* page without one. `adopt` is gone; `setEntityId` only writes the URL. The register reads every filter from the URL (repeated parameters included) and its entity select writes `entity` in the same update as the rest. `ChipSelect` takes a list: several values (or one the options do not list, e.g. an inactive owner) show "<label>: N selected" and "All" can always be chosen. The top bar shows "N legal entities" for several. Overview's counterparty chip has a clear button; removing a custom date range resets the Dates control; an emptied custom field is dropped from the filter (never sent as `cf.<key>=`). The landing-page redirect carries the default entity in its URL.

**Backend check (not changed).** `ContractFilterParser`: absent or empty id/date/number/boolean parameters are "no filter" (empty entries are skipped), so removal never 400s. One gap: an empty custom-field value (`cf.riskLevel=` in a query, or `"cf": {"riskLevel": null}`/`""` in the data-room JSON) is kept as a filter on `""` and matches nothing. The frontend never sends one (`qs` drops empty values, `applyDrill` drops empty `cf` keys); the backend should ideally skip blank `cf.*` values too.

| Command | Result |
|---|---|
| `npm run lint` | exit 0, no warnings |
| `npm test` | exit 0, 15 files, 292 tests (new `pages/filters.test.tsx` 40: every entity add/remove path, each filter type by chip and Clear all, repeated parameters, register and search; 2 new in `lib/r3.test.ts`) |
| `npm run build`, `VITE_API_MODE=real npm run build`, `npm run build:demo` | exit 0; no `mockApi` chunk in the real build |
| `npm run e2e:demo` | exit 0 at 390 and 1440 px, motion on; new: entity added with the filter bar and with a chart drill-down, removed with the breadcrumb and with Clear all, totals back to 44 contracts / DKK 60.7m |

Note: filter-bar selects replace the history entry (as before); browser back undoes chart/tile drill-downs, which push.

## Backend release 4 (2026-11-22)

Scope: `src/api`, `tests`, `docs/RUNBOOK.md`, `docs/DEPLOY_TEST.md`, this section, a section in `docs/DECISIONS.md`. `src/web` not touched. No git commands run.

Committed in 8e46125 (+ 901510e, 82c90bc, 56e775a); CI green with 314 backend tests.

### Built, by item of the owner's feedback

1. **Filters.** An empty value is "no filter" for every parameter, custom fields included (`cf.key=`, JSON `null`/`""`). `entityId` (repeatable) is accepted and applied after the access filter on `home`, `renewals`, `approvals/mine`, `overview`, `search` (palette), `search/full`. New `ContractFilter` parameters: `title`, `contractNo`, `counterpartyName`, `delegateId`, `responsibleId`, `currency`, `annualValueMin/Max`, `totalValueMin/Max`, `noticeFrom/To`, `endFrom/To`, `startFrom/To`, `signedFrom/To`, `createdFrom/To`, repeated `cf.<key>` (any of), `cf.<key>.min/.max`. `GET contracts/distinct` gives the values of a column under the current filter (the column's own filter left out), counted over the contracts the caller may see.
2. **Pickers.** `GET people` (type-ahead, role filter, inactive excluded). `POST counterparties` for non-admins per the general setting `counterpartyCreation` (AdminOnly default / ContractOwners / AllUsers; 409 `duplicate` with `existingId`). `POST contracts` requires an active `supplierId`; the name always comes from the master record; an existing counterparty cannot be removed again; owner, delegate and responsible must be active users. `AppConfig.canCreateCounterparty`.
3. **Templates.** Template categories (CRUD, order, in-use protection), templates with `categoryId`, `typeIds`, `language`, `updatedAt`; `GET templates` filtered to active templates in active categories, with optional `categoryId`, `typeId`, `language`, `q`; `GET template-categories`; `GET templates/{id}/preview` (placeholders with label and sample, excerpt).
4. **Branding and formats.** Logo upload / replace / delete in three variants (full, compact, dark): PNG, JPEG, WebP, SVG, 1 MB, recognised by content; dangerous SVG refused; served anonymously with ETag, immutable caching on the versioned address, CSP and nosniff; in `app-config`, `dev/config` and `GET branding`. Number and date format defaults `da-DK` / `dd-MM-yyyy` for new installations. Personal `theme` preference (light default / dark / system).
5. **Legal entity mapping.** Site address and management group on legal entities, library name and group on departments, link state, `test-link` for both, `GET admin/access-mapping`. The SharePoint store and the Graph directory sync **read these mappings** (no name conventions, no configuration list). RUNBOOK section 4c explains the three-way link and adding a new legal entity end to end.
6. **E-signature.** Permission `canSendForSignature` with optional entity limit (administrators get nothing implicitly); settings; `SignatureRequest` / `Signatory`; send, resend, cancel, simulate; webhook `POST api/esign/webhooks/{provider}` (shared secret, the only route exempt from `X-Requested-With`); hourly `SignatureSync` job (status polling, reminders, expiry); completion stores the signed PDF as Main, sets signed date and method, Approved becomes Active. Providers behind `ISignatureProvider`: **Simulated** complete; **DocuSign** and **Penneo** written against their REST documentation, isolated in one file, **never run**. Manual "record signature" kept (owner, delegate, admin).
7. **Settings reliability.** Roles and rights were already read from the database on every request; `me` now uses the same mapping everywhere and carries the new fields. Every `/api` answer gets `Cache-Control: no-store` unless the action sets its own, and the SPA shell `no-cache`. No admin GET depends on an earlier request (job rows and settings are created on first read). Tests sign in freshly before each admin GET and change a role mid-session.

Plus the SharePoint hardening (below) and the theme / dark logo additions.

### Contract alignment (docs/CONTRACT_V4.md)

Routes, parameter names, JSON field names, status codes and problem types follow CONTRACT_V4 sections 1 to 9a. Not checked against `src/web/src/api/types.ts` line by line (frontend scope); field names were taken from the contract text.

#### Contract mismatches (release 4)

Deliberate, each also in DECISIONS:

- **SVG logos** are not "stored as is" unconditionally: a file with scripts, event handlers, `foreignObject`, external references or embedded HTML is refused with 400 `file`. Accepted files are stored unchanged and served with the headers the contract names.
- **`provider`** in `PUT admin/esign-settings`: only a provider whose server configuration is present can be chosen (400 `provider` otherwise); `Simulated` only when `Signing:AllowSimulated` allows it (default: only with Dev sign-in).
- **Partially signed** is stored internally but reported as `Sent`, as the contract's status list has no such value. Extra field `signedCount`.
- **Completion** moves the contract to Active only from `Approved`. A Draft or Active contract sent with `requireApproved=false` gets the signed document, date and method but keeps its status (a draft must not skip approval).
- **`POST contracts/{id}/sign`** is also refused (409 `signature-state`) while a signature request is open, and still needs the ContractOwner role or Admin as before.
- **Test link in test mode** returns `Linked` with the contract's detail text; in SharePoint mode a missing library is reported as `Error` on the department, and the entity result lists missing libraries in `details`.
- **Reconciliation run** is always synchronous (budget 50 s, 2000 findings) and never answers `Running`; when it stops early, `status` is `Completed` and `error` says how far it got. `Orphan` is only detected in folders of contracts whose folder ids are stored (uploads made after this release); `Mismatch` in SharePoint mode compares size, content tag and name (the hash is compared in Local mode, where the file is read).
- **`counterpartyCreation`** also accepts the brief's words `Admins` and `Everyone` on input and stores the contract's values.
- **Value ranges** (`annualValueMin/Max`, `totalValueMin/Max`): a contract without that value never matches a range.
- **`GET watching`** is not entity-scoped (the contract scopes "Watching" inside `home`, which is).
- **StorageProblem** notifications are outbox and in-app rows to administrators outside the notification catalogue (no template, cannot be switched off).

Additions the frontend can ignore: `SignatureRequest.createdById`, `signedCount`, `statusMessage`; `GET signature-requests/{id}`; `AppConfig.theme`; `PersonOption.departmentNames`; `GET templates?categoryId=&typeId=&language=&q=`; audit actions `signature.resent`, `signature.expired`, `storage.reconciled`, `storage.file-missing`, `document.upload-failed`, `approval.log-saved`; webhook headers (`X-Dafa-Webhook-Secret`, `X-Dafa-Signature`, `X-DocuSign-Signature-1`).

### SharePoint hardening (REVIEW_SHAREPOINT "Do now")

| # | Item | State | Notes |
|---|---|---|---|
| 1 | Graph client: retry with Retry-After, backoff with jitter, per-try timeouts, circuit breaker, User-Agent | done | `GraphResilienceHandler`, client "graph"; mail uses its own plain client "graph-mail" (no retries, no breaker; a failed mail is retried by the reminder job as before) |
| 1 | Exceptions become 503 `storage-unavailable` (+ Retry-After) and 404 `file-missing` | done | middleware; `FileNotFoundException` from the local store is mapped too |
| 2 | No store call on contract detail and lists | done | `storeUrl` is the stored folder link, administrators only |
| 2 | Final approval does not fail when the store is down | done | `ApprovalInstances.LogPending`; `UploadCleanup` writes the log later |
| 3 | Silent fallbacks removed, `storage-not-configured` | done | no default site, no first library; unmapped = refused |
| 4 | Never overwrite; versioned names; upload only after the contract number is final; idempotent folder creation; folder ids and link on the contract | done | `conflictBehavior=fail` everywhere; a draft whose file cannot be stored is kept, with `document.upload-failed` in its log |
| 5 | Per document: site id, drive id, item id, path, cTag, eTag, size, SHA-256 | done | rows from before this release have none of these (reconciliation falls back to size only for them in SharePoint mode) |
| 6 | Text extraction backfill retries with capped attempts and backoff | done | status `Retry`, 15 min doubling, 6 attempts; a missing file fails at once |
| 7 | Streamed downloads; upload session above 10 MB | partial | downloads are streamed; uploads are still buffered in memory (25 MB limit) and an interrupted upload session is restarted, not resumed |
| 8 | Nightly reconciliation (report only), admin endpoints, storage health | done | job `StorageReconciliation` daily 02:00; see the mismatch list for limits |
| 9 | Graph sign-in with certificate or managed identity besides the secret | done, never run | `Graph:Auth`; the client assertion is built by hand (no MSAL); only the App Service / Container Apps identity endpoint (`IDENTITY_ENDPOINT`), not the VM metadata endpoint |
| 9 | Docs: RUNBOOK 5.2 to 5.5 and 5.7, DEPLOY_TEST | done | app-only, `Sites.Selected`, no user access to libraries, permissions as used in code, first-run checklist |
| - | `DocumentWriter` comment corrected | done | |
| - | Data room: throttle-aware pacing per file | not done | exports use the same client (retries, breaker) but no extra pacing |
| - | Log the Graph `request-id` on errors | not done | |
| - | Alert before the client secret expires | not done | documented in RUNBOOK 5.2 |
| - | Automatic move of files when entity or department changes | not done, by decision | reported as `MisplacedFolder` |

All of the Graph code (store, link probe, directory sync, token provider with certificate and managed identity) is tested against a scripted HTTP handler only. **It has never run against a tenant**; RUNBOOK 5.7 is the checklist for the first run.

### Configuration keys (new or changed)

| Key | Default | Meaning |
|---|---|---|
| `Signing:AllowSimulated` | empty = allowed only when `Auth:Mode=Dev` | whether the Simulated provider can be chosen |
| `Signing:Webhook:Secret` | empty = webhook route answers 404 | shared secret, at least 24 characters |
| `Signing:DocuSign:BaseUrl`, `:AuthServer`, `:AccountId`, `:IntegrationKey`, `:UserId`, `:PrivateKeyPath` | none | DocuSign (JWT grant) |
| `Signing:Penneo:BaseUrl`, `:ApiKey`, `:ApiSecret` | none | Penneo |
| `Graph:Auth` | `ClientSecret` | `ClientSecret`, `Certificate` or `ManagedIdentity` |
| `AzureAd:ClientCertificateThumbprint`, `AzureAd:ClientCertificatePath`, `AzureAd:ClientCertificatePassword` | none | certificate sign-in to Graph |
| `AzureAd:ManagedIdentityClientId` | none | user-assigned managed identity |
| `Graph:MaxAttempts`, `Graph:MetadataTimeoutSeconds`, `Graph:ContentTimeoutSeconds` | 4, 30, 180 | retries and timeouts |

No longer read: `Signature:Provider` (the provider is an admin setting), `SharePoint:Sites`, `SharePoint:DefaultSiteId` (the mapping is in the database). The table rows for these in the "Backend phase 1" section above are out of date. **The test site needs no new setting.**

### Schema (SchemaUpgrader, additive and idempotent)

- **Step 3 "release 4".** Tables: `TemplateCategories`, `BrandingAssets`, `SignatureRequests`, `Signatories`. Columns: `LegalEntities` (`SharePointSiteUrl`, `SharePointSiteId`, `ManagementGroupId`, `ManagementGroupName`, `LinkStatus`, `LinkCheckedAt`, `LinkMessage`); `Departments` (`LibraryName`, `GroupId`, `GroupName`, `LinkStatus`, `LinkCheckedAt`, `LinkMessage`); `Users` (`CanSendForSignature` NOT NULL default 0, `SignatureEntityIdsJson`, `HomeEntityId`); `Templates` (`CategoryId`, `ContractTypeIdsJson`, `Language`); `UserPreferences.Theme` (NOT NULL default `'light'`). Indexes ensured on `Templates`, `SignatureRequests` (incl. a filtered unique index "one open request per contract", `[Status] < 3`), `Signatories`.
- **Step 4 "storage hardening".** Table `StorageReconciliationReports`. Columns: `Documents` (`SiteId`, `DriveId`, `ItemId`, `RelativePath`, `ETag`, `CTag`, `ContentHash`, all nullable); `Contracts` (`StoreDriveId`, `StoreFolderId`, `StoreFolderUrl`, nullable); `ApprovalInstances.LogPending` (NOT NULL default 0); `DocumentTexts` (`Attempts` NOT NULL default 0, `NextAttemptAt` nullable).
- Tests: `Release4SchemaTests` upgrades a database with the release 3 schema, checks the columns and defaults, and runs the upgrader twice (Sqlite). **The SQL Server path of both steps is desk-checked only**; the filtered index is the statement to watch in the first deployment log (a failing index is a warning, the app still starts, but then "one open request per contract" is only enforced by the service).
- Demo seed, once per database: three template categories, Simulated e-signature, signing permission for Lars Winther (all entities) and Jonas Berg (two entities).

### Tests

70 new test methods in four files, plus updates to existing tests where the rules changed (schema versions 1 to 4, 16 notification events, 10 jobs, contracts need a `supplierId`).

- `Release4Tests.cs` (27): filters and empty values, entity scope, distinct values scoped by access, pickers and counterparty rules, template categories and filtering, logo type / size / SVG rejection and the dark variant, org links and access mapping, theme, settings right after sign-in, schema upgrade from release 3 and idempotency.
- `Release4SignatureTests.cs` (12): permission and entity limit, require-approved, input validation, the simulated flow to Active (parallel and sequential), decline, cancel, resend, reminders, expiry, webhook events, manual signature, the generated PDF.
- `StorageHardeningTests.cs` (20): retry and Retry-After with a fake handler, circuit breaker, no fallback site or library, no overwrite, upload session, streamed download, hash stored, backfill retry, reconciliation report, contract detail and final approval with the store down, problem responses.
- `Release4ApiTests.cs` (11, over HTTP): every admin GET right after sign-in, `me` after a role change, empty parameters on every list, logo upload and anonymous caching, theme, signing permission and the simulated flow, webhook secret (own host with the secret set), storage endpoints, `storeUrl` for administrators only.

### Most likely to fail compilation or a first test run

1. `Infrastructure/Providers/AccessTokenProvider.cs`: `X509CertificateLoader.LoadPkcs12FromFile`, certificate store lookup, hand-built client assertion (RS256, `x5t`).
2. `Infrastructure/Providers/SignatureProviders.cs`: DocuSign JWT (`RSA.ImportFromPem`), multipart and JSON shapes; largest never-run file.
3. `Infrastructure/Providers/GraphClient.cs`, `DocumentStores.cs`, `DirectoryAndSignature.cs`: `HttpRequestOptionsKey`, `ContentRangeHeaderValue`, tuple-valued `ConcurrentDictionary`.
4. `Core/Abstractions/Interfaces.cs`: default interface methods on `IDocumentStore`.
5. `Core/Services/SignatureService.cs` (about 1250 lines), `StorageServices.cs` (a local function declared inside a loop that captures loop variables), `ContractFilterEngine.cs`, `PickerServices.cs`.
6. `Core/Dtos/DtosRelease3.cs`: `AdminUserInput.EntityId` as a non-nullable `JsonElement` (model binding and validation of an omitted value).
7. `Infrastructure/Data/AppDbContext.cs` + `SchemaUpgrader.cs`: a named filtered unique index next to a plain index on `SignatureRequests.ContractId`; the filter text on SQL Server versus Sqlite.
8. `Api/Controllers/Release4Controllers.cs`: `HMACSHA256.HashData`, `Request.HasJsonContentType`, `Content("null", ...)`; `Api/Hosting/HostingSupport.cs`.
9. Tests: `StorageHardeningTests.cs` (fake Graph drive parses request addresses; `DefaultHttpContext` with the exception middleware), `Release4ApiTests.cs` (assumptions about seeded contracts of Lars Winther in DAFA A/S and Jonas Berg in DAFA Polska; header parsing), `Release4SignatureTests.cs` (notification counts), and overload resolution of `HomeAsync()` in older tests.
10. Behaviour, not compilation: older tests that create contracts without a counterparty were updated where found; one that was missed will fail with 400 `supplierId`.

### Not done / known gaps

- DocuSign and Penneo have never been called. Simulated is the only provider that has even been desk-tested end to end.
- `restrictToAuthorised`: internal signers are matched by user id, external ones only by name (case-insensitive), which anyone typing the request can satisfy; treat it as a reminder, not a control, for external signatories.
- Upload sessions are not resumed; uploads are buffered in memory.
- The reconciliation does not page through libraries looking for stray folders; it only looks inside known contract folders.
- No per-file pacing in data room exports; no Graph `request-id` in logs; no secret-expiry alert; Purview and tenant settings are out of scope.
- Changing a contract's entity or department does not move files.
- `GET watching` is not entity-scoped.
- The number and date formats are settings only; CSV exports still use the server's fixed formats.
- Missing-file alerts are recorded once per document version (not repeated daily).

## Frontend release 4 (2026-11-22)

Scope: `src/web`, `docs/CONTRACT_V4.md` (new, written first for the backend), this section and the frontend section of `docs/DECISIONS.md`. `src/api` not touched.

### Items
| Item | State | What was built |
|---|---|---|
| A1 Overview entity filter cannot be removed | Done | Root cause in `ScopeProvider` (see "Frontend fix" above): the URL is now the only state. |
| A2 Search entity back to "All" | Done | Same root cause; Search sends `entityId` repeatable and its Entity select can always be set to All. |
| A3 Top-bar scope | Done | One model: `?entity=` (repeatable) = scope, sent as `entityId` by Home, Contracts, Renewals, Approvals (page; nav badges stay unscoped), Overview, Search page and the Ctrl+K palette (client parameter names checked against `client.ts`). Removable "Showing <entity>" chip on Home, Renewals and Approvals (Overview/register/search show it in their own filters). Persisted per user on the device (`dafa.scope.<userId>`), else the server default (`defaultEntityId`). Renewals no longer filters only in the browser. |
| A4 Settings sometimes blank | Done (frontend) | Lazy pages reload once after a failed chunk import (deploy removed old hashed files; loop guard 60 s), then a "A new version is available – Reload" screen; a route error boundary keeps the shell; every Settings section shows an error with Retry instead of an endless skeleton (General, Currencies, Tags, Jobs, import history/fields, sync runs). Deployment request in CONTRACT_V4 §9: keep previous `assets/*` one release or serve `index.html` no-cache. |
| B Register header filters | Done | Filter button in every column header (text contains; counterparty contains or pick; entity/department/type/status/owner/delegate/responsible/tags multi-select with search from `GET contracts/distinct`; values min/max DKK; dates from/to; custom fields by type; renewal/change of control yes/no). Active = filled icon + removable chip; in the URL, saved views and export. |
| C Person and counterparty pickers | Done | `Combobox` (ARIA combobox/listbox, keyboard, clear button, no free text), `PersonPicker` (name/e-mail/initials, shows department · entity, inactive hidden) and `CounterpartyPicker` (master data, "Create new counterparty" dialog when `canCreateCounterparty`; a duplicate picks the existing one). Used in New contract, Edit details, obligations (2), delegate (2), bulk reassign, users reassign/effective access, holiday cover, approval rules, Person custom fields, e-signature, Overview owner/counterparty filters, Search person filter. A suggested name is matched to the master on extract. |
| D Number formats | Done | `NumberInput` with thousand separators while typing (caret kept), canonical text in state, numbers to the API; amounts, custom Number/Money, header filters, approval rule bands, rule tester, due-diligence threshold, e-sign expiry. General settings date and number formats now apply to every formatted date and number. |
| E Template categories | Done | Settings > Template categories (add, edit, order up/down, active, delete when empty); template dialog: category, contract types (several), language, active; New contract: category chips, search, description, category · language · types, field preview. |
| F Logo | Done | Settings > General: logo, dark-theme logo, compact icon (PNG/SVG/JPG/WebP, 1 MB), previews on light and dark, Replace/Remove. Shown as `<img>` (never inlined), transparent, max 40 px high, in the sidebar and on the sign-in screen. The compact icon is stored and offered but not yet placed in the phone header. |
| G Entity/SharePoint/Entra link | Done | Legal entities: site URL, resolved site id (read-only), management group, link state with last check and "Test link"; Departments: library and group with "Test link"; Settings > Access mapping matrix with warnings. Marked "Used in SharePoint mode" in test mode. |
| H E-signature | Done (Simulated) | User right "Can send for signature" (+ per entity) in Users and access; Settings > E-signature (provider, message, order, reminders, expiry, require approved, authorised signatories per entity). Record: "Send for signature" (documents, DAFA/external signatories, order, message, preview), Signature card (status per signatory, resend, cancel, "Simulate signing"/decline in Simulated mode); completion stores the signed document and activates the contract. Others see "Ask a colleague with signing rights". "Mark as signed" is now "Record manual signature" for owner, delegate and admins. DocuSign/Penneo are only settings on the frontend. |
| I Light/dark theme | Done | My settings > Appearance: Light, Dark, System; saved via `me/preferences` (`theme`), copied to localStorage and applied before first paint by `public/theme-init.js` (sign-in included). Every color is a token; a test checks both token sets have the same keys and that no other stylesheet has a hard-coded color. |
| Storage | Done | `storage-unavailable` → "SharePoint is busy. Try again in a moment." with Retry; `file-missing` sentence; `storage-not-configured` shows the server text, admins get "Open Access mapping"; on document upload/download, extract, template download, data room, signature send. Settings > Storage health (state, last success/failure, failures 24 h) and the latest reconciliation report with "Run check now". "Open in SharePoint" only for admins. |

### Contrast (WCAG ratios, computed)
Dark: text 13.96 on surface / 15.43 on page; secondary 7.41 / 8.19; label 5.48; links/primary #5aa9d6 6.32 on surface, 5.01 on tint; white on primary fill #2a72a3 5.21 (hover 6.84); white on hero #0d3a5a 11.86; pills red 7.91, amber 8.13, green 7.94, blue 7.33, gray 8.34; placeholder 6.13; chart marks high 5.68, medium 8.56, low 6.32, decided 3.74, bar 4.18 (all ≥ 3:1). Light (changed so every text passes): label #66717d 4.97 (was 3.08), amber dot and "31–90 days" segment #a87300 4.10 (was 2.75), "decided" segment #8593a2 3.14 (was 2.10), soft bar #5f8fb2 3.47 (was 2.50).

### Results of the last run here (src/web)
| Command | Result |
|---|---|
| `npm run lint` | exit 0, no warnings |
| `npm test` | exit 0, 17 files, 312 tests (new: `pages/filters.test.tsx` 42, `pages/r4.test.tsx` 9, `lib/r4.test.ts` 8) |
| `npm run build` / `VITE_API_MODE=real npm run build` | exit 0; no `mockApi` chunk and no sample-data strings in the real build |
| `npm run build:demo` | exit 0 |
| `npm run e2e:demo` | exit 0 at 390 and 1440 px, motion on; new release 4 journey: Search entity back to All, top-bar scope chip removal (1440), register header filter with separators and chip removal (1440), counterparty/owner pickers need a click, template category, e-signature denied for Henrik and simulated end-to-end for Rasmus, SVG logo in the sidebar (1440, transparent, 32 px), dark theme switch (page rgb(15,22,32)). |

Screens looked at: dark at 1440 (Home, Contracts, Overview, Renewals, Approvals, Search, New contract, My settings, Settings General/E-signature/Access mapping/Storage/Entities/Template categories), dark at 390 (Home, Contracts, record, approval), light at 1920 (Contracts with header filters, Overview, New contract, Settings). Fixed after looking: select chevrons invisible in dark (now a token), logo preview captions, chevrons on category chips.

### Not done / known gaps
- Nothing ran against the real API; CONTRACT_V4 is the shape the backend engineer implements.
- Person pickers search the users already in lookups (instant, no request); `GET people` is in the contract for later.
- Compact logo not yet used in the phone header; theme screenshots of every Settings section at 390 not taken.
- Header filters are not offered in the phone card list (filters there: the chips and selects above).
- The dataviz CVD validator was not re-run for the changed light amber/gray chart colors (contrast was computed).

## Release 4 alignment fixes (2026-11-22)

Fixes for `docs/REVIEW_ALIGNMENT_R4.md` (frontend client against the real API). Frontend only, except one read-only field on the API. The mock now answers as the API does in each of these places, so the tests exercise the real behaviour.

| Finding | Status | What changed |
|---|---|---|
| H1 department link wiped on rename/deactivate | Fixed | Settings > Departments sends the department's existing `documentLibrary` and `groupId` with every update. The other settings forms were checked (legal entities, contract types, template categories, templates, tags, currencies, counterparties, users, custom fields, sync sources, general, notification and e-signature settings): each sends the whole item. |
| H2 Edit details without a master counterparty | Fixed | The counterparty is required on create and when the contract already has one; a contract without one keeps its name and can be saved (the field says so). |
| M1 generic "One or more fields are invalid." | Fixed | `lib/errors.ts`: a 400 shows the first message of each field in `errors` (`errorMessage`, `fieldErrors`, `fieldError`); forms map the keys to their fields. |
| M2 signer row errors | Fixed | `signers[0].userId`, `$.signers[0].userId` and `Signers[0].UserId` are read as `signers.0.userId`; a refused field brings the dialog back from the preview. |
| M3 New contract upload | Fixed | Only `.pdf` and `.docx` with a clear message; Documents keeps the wide list. |
| M4 Record manual signature | Fixed | Offered only when the user can edit the contract. |
| M5 `requireApproved` off | Fixed | "Send for signature" is offered on Draft and Active too. `app-config.eSign.requireApproved` added on the API (one field, `ESignConfigDto`); the frontend also works without it through `canSendForSignature`. |
| M6 403 on `me` (Entra) | Fixed | "Your account does not have access to DAFA Contracts. Contact an administrator." |
| M7 session ended | Fixed | A 401 on any query or mutation asks `me` again once; its 401 shows sign-in (Dev: access code/user picker; Entra: redirect with the current path, with the existing loop guard). The page stays the same after signing in again. |
| L1 Resend/Cancel/Simulate | Fixed | Shown to the sender, colleagues with the signing right for the entity and administrators. |
| L2 e-signature toggle in General | Fixed | Read-only line, derived from the provider, with a link to Settings > E-signature. |
| L3 template upload | Fixed | `.docx` only. |
| L4 column-filter counts | Fixed | The active tab (`view`) is sent; the API cannot count within the free-text search, so the list says so when a search text is active. |
| L5 counterparty picker | Fixed | VAT number and external id matches are shown; a duplicate found by VAT number is chosen under its master name. |
| L6 approval log | Fixed | Not offered as a document to sign. |

Needs a live check, made safe from the client:
- **User without a home legal entity:** the client sends `entityId: ""` (never JSON null); the API clears on both. `Release4ApiTests.Me_ReflectsARoleChange_OnTheVeryNextRequest` now checks `""` and `null` over HTTP.
- **Export selected:** the API has only `GET contracts/export`, so the ids are sent in several requests that each keep the URL under 2000 characters, and the CSV answers are joined into one file (`lib/exportSelected.ts`).
- **Template upload:** the client uses one path, the multipart `POST admin/templates` (no `Content-Type` of its own, so the browser adds the boundary); the JSON action is not used by the frontend. A test pins the request shape.
- **Still open:** the first request after sign-in on the hosted site (cookie `Secure` behind the HTTPS front end), and one real upload of a template and a 45+ row "Export selected" on the test site.

Backend files touched: `Dtos/DtosRelease4.cs` and `Services/PersonalService.cs` (`eSign.requireApproved` in `app-config`), `tests/.../Release4ApiTests.cs` (two blocks of assertions). Not compiled here (no .NET SDK); GitHub Actions compiles and runs them.

| Command (src/web) | Result |
|---|---|
| `npm run lint` | exit 0, no warnings |
| `npm test` | exit 0, 19 files, 353 tests (new: `lib/r4align.test.ts` 20, `pages/r4align.test.tsx` 21) |
| `npm run build` / `VITE_API_MODE=real npm run build` / `npm run build:demo` | exit 0 |
| `npm run e2e:demo` | exit 0 at 390 and 1440 px; new step: the owner edits an active contract without a master counterparty and saves |
