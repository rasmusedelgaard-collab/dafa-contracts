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
