# DAFA Contracts - runbook

Operations and handover guide. Audience: DAFA IT (owner after go-live), the CFO as business
owner, and anyone doing technical due diligence.

Status of this document: the **test** sections describe what is built. The **production**
sections are the agreed plan; the Azure environment in `infra/main.bicep` has been compiled
but **has not been deployed**, and the Entra/SharePoint steps have not been executed.

## 1. Architecture summary

- One deployable unit: an ASP.NET Core (.NET 10) app that exposes the JSON API under `/api`
  and serves the React single-page app from its `wwwroot` folder (backend-for-frontend; the
  browser only ever talks to this app, with a session cookie).
- One relational database (EF Core). SQLite locally, SQL Server on the test host, Azure SQL in production.
- Every external dependency sits behind an interface with a test and a production
  implementation chosen by configuration (table in section 2), so the test site runs without
  any Microsoft tenant.
- Background work (reminders, escalations, obligation reminders, supplier sync) runs hourly
  inside the app process, with an admin "run now" endpoint (`POST /api/admin/jobs/run`).
- Access control is enforced server-side in one place (`IContractAccessService` plus a query
  filter). The frontend never filters for security.
- Every create/update/decision/approval/access grant/file open writes an audit event to the database.
- Health endpoint: `GET /healthz`.

```
Browser --> ASP.NET Core app (API + SPA) --> SQL database
                     |
                     +--> Microsoft Entra ID (sign-in)          production only
                     +--> Microsoft Graph: SharePoint, mail,
                     |    Teams, users/groups                   production only
                     +--> Azure OpenAI (optional)               production only
                     +--> Power BI executeQueries (suppliers)   production only
```

Environments:

| | Local | Test (monsterasp.net) | Production (Azure, planned) |
|---|---|---|---|
| Hosting | `dotnet run` + Vite | Shared IIS hosting, EU datacenter | App Service, `swedencentral` |
| Database | SQLite | MSSQL on monsterasp.net | Azure SQL, Entra-only auth |
| Sign-in | Dev (pick a demo user) | Dev (pick a demo user) | Microsoft Entra ID |
| Files | Local disk | Local disk on the host | SharePoint |
| Data | Demo | **Demo only - never real contracts** | Real |

## 2. Configuration keys by mode

Names are taken from `docs/BUILD_BRIEF.md`. **The backend engineer's section in
`docs/STATUS.md` is authoritative for the exact keys, sub-keys and defaults** - check it
before relying on anything below, in particular the connection string key and the keys marked
"assumed".

In environment variables and App Service settings, write `:` as `__` (for example `Auth__Mode`).

| Key | Test value | Production value | Effect |
|---|---|---|---|
| `Auth:Mode` | `Dev` | `Entra` | Dev = pick a seeded demo user, no real authentication. Entra = OpenID Connect via Microsoft.Identity.Web. **Required, no default outside local development:** a missing or misspelled value stops the app at startup. |
| `Auth:Dev:AccessCode` | the shared tester code (secret `MONSTERASP_DEV_ACCESS_CODE`), at least 12 characters | not used | Dev mode outside the Development environment refuses to start without it. Asked before the demo-user list is shown; 10 wrong codes per address in 10 minutes block that address. Changing it signs everyone out. |
| `Auth:BootstrapAdmins` | not used | list of e-mail addresses / UPNs (array, or one string separated by `,` or `;`) | Entra mode: these accounts are created at their first sign-in (even with `Auth:Entra:AutoProvision=false`) and given the Admin role. This is how the first administrators come into being. Remove an address to stop the role being re-granted at sign-in. |
| `Auth:Entra:AutoProvision` | not used | `true` (default) or `false` | `true`: any tenant user who signs in becomes a Reader without memberships. `false`: only users that already exist (or bootstrap admins) get in; others get 403. |
| `Database:Provider` | `Sqlite` (local) / `SqlServer` (test site) | `SqlServer` | EF Core provider. |
| `ConnectionStrings:Default` | monsterasp.net "Local access" string | Passwordless, managed identity (`Authentication=Active Directory Default`) | The only connection string key the API reads. |
| `Documents:Provider` | `Local` (disk under `App_Data/files`, default) | `SharePoint` | Where contract documents are stored. With `SharePoint` the site and library of every file come from the mapping under Settings (section 4c); there is no site list in configuration any more (`SharePoint:Sites` and `SharePoint:DefaultSiteId` are no longer read). |
| `Directory:Provider` | `Seeded` (default) | `Graph` | Where users and memberships come from. `Graph` imports the tenant's users and reads memberships from the Entra groups mapped under Settings (section 4c). |
| `Graph:Auth` | not used | `ManagedIdentity`, `Certificate` or `ClientSecret` (default) | How the app signs in to Microsoft Graph (SharePoint, directory, mail). See section 5.2. |
| `Graph:MaxAttempts`, `Graph:MetadataTimeoutSeconds`, `Graph:ContentTimeoutSeconds` | not used | `4`, `30`, `180` (defaults) | Retries and per-try timeouts of the shared Graph client (section 4c). |
| `Signing:AllowSimulated` | empty (= allowed, because `Auth:Mode=Dev`) | empty (= not allowed) or `false` | Whether the Simulated e-signature provider can be chosen in Settings. Never allowed by default outside Dev sign-in. |
| `Signing:Webhook:Secret` | empty (the webhook route answers 404) | a random secret of at least 24 characters | Shared secret the e-signature provider must prove on `POST /api/esign/webhooks/{provider}`. |
| `Signing:DocuSign:*`, `Signing:Penneo:*` | not used | set for the chosen provider | Credentials of the e-signature provider (section 4c). Never stored in the database. |
| `Analyzer:Provider` | `Heuristic` (default) | `AzureOpenAI` (or stay on `Heuristic`) | Field extraction and summary at intake. |
| `Suppliers:Provider` | `Seed` (default) | `PowerBi` | Supplier master source. |
| `Notifications:Provider` | `Outbox` (rows shown in admin, default) | `Graph` (mail) | How reminders are delivered. With `Graph`, a reminder whose mail fails is retried by the hourly job (up to 5 attempts) instead of being marked as sent. |
| `Seed:DemoData` | `true` (set by the deployment) | `false` (default) | Creates demo users, contracts and approvals. Off unless set to `true`, and ignored unless `Auth:Mode=Dev`. |
| `AzureAd:Instance`, `AzureAd:TenantId`, `AzureAd:ClientId`, `AzureAd:ClientSecret` (assumed, Microsoft.Identity.Web convention) | not used | set | Entra app registration. Secret comes from Key Vault. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | not used | set by Bicep | Telemetry. Only has an effect once the API includes Application Insights / OpenTelemetry (see known gaps). |

E-signature (release 4): the provider is chosen by an administrator under Settings > E-signature
(`None`, `Simulated`, `DocuSign`, `Penneo`), not in configuration; `Signature:Provider` is no
longer read. Only providers whose server credentials are present can be chosen. Marking a
contract as signed by hand still works with every choice. See section 4c.

Approval rule specificity (how the rule for a contract is picked): the rule with the most
criteria set wins, where entity, type, department and the value band (minimum and/or maximum
together) each count as one; ties go to the higher priority, then the name. The value compared
with the band is the **larger** of total value and annual value, in DKK. Consequences to keep
in mind when editing rules in Settings:

- A rule with only a contract type and no value band is exactly as specific as a plain value
  band, and wins on priority. Anyone who can choose the contract type could then route a large
  contract around the value bands. **Give every type rule a maximum value** (the seeded "NDA"
  rule has DKK 1m): type + band is then more specific than a band alone for small contracts,
  and larger ones fall through to the value bands.
- The seeded "Corporate" rule has no cap on purpose: its route (Legal and Group CFO, then
  Group CEO) is at least as strict as the top value band.
- The contract owner and the person who submits are never approvers. A role or person that
  resolves to one of them is replaced by an entity manager of the contract's entity, else the
  Group CFO, else the Group CEO, else an administrator; the step's role label says so.

How the test site gets its settings: the **Deploy test** workflow writes
`appsettings.Production.json` into the published folder at deploy time (Dev sign-in with the
access code, SQL Server, demo data, connection string from the GitHub secret). The file is never committed.
Environment variables set in the monsterasp.net Control Panel (website > Scripting >
Environment variables) override it.

## 3. Build and release

| Workflow | Trigger | What it does |
|---|---|---|
| `CI` (`.github/workflows/ci.yml`) | every push, every pull request | `web`: npm ci, lint, test, build. `api`: restore, build, test (TRX uploaded; compile errors and failed tests are summarised in the job log and the run summary). `infra`: compiles the Bicep. `package`: puts the SPA into `wwwroot`, runs `dotnet publish` (Release, `win-x86`, framework-dependent) and uploads `api-publish`. |
| `Deploy test` (`.github/workflows/deploy-test.yml`) | after a successful `CI` run for a push to `main`; or manually | Downloads `api-publish` from that CI run, adds the test-mode settings file, syncs to monsterasp.net with Web Deploy (app taken offline during the sync, `App_Data` left untouched), then checks `/healthz` if `MONSTERASP_SITE_URL` is set. Skips with a message when secrets are missing. |

Artifacts per CI run: `web-dist`, `api-publish` (30 days), `test-results`, `build-logs`.
`/build-info.json` on a deployed site shows the commit it was built from.

Repository secrets (test deployment): `MONSTERASP_WEBDEPLOY_URL`,
`MONSTERASP_WEBDEPLOY_SITE`, `MONSTERASP_WEBDEPLOY_USERNAME`,
`MONSTERASP_WEBDEPLOY_PASSWORD`, `MONSTERASP_DB_CONNECTION_STRING`,
`MONSTERASP_DEV_ACCESS_CODE`.
Optional repository variables: `MONSTERASP_SITE_URL`, `MONSTERASP_ALLOW_UNTRUSTED_CERT`
(`false` to require a trusted certificate on the Web Deploy endpoint), `PUBLISH_RUNTIME`
(default `win-x86`), `PUBLISH_SELF_CONTAINED` (default `false`).

Rollback on the test site: run **Deploy test** manually and enter the ID of an earlier CI run in `ci_run_id`.

Setup guide for the test site: `docs/DEPLOY_TEST.md`.

## 4. Test site: operations and known limitations

- **Test data only.** Dev sign-in lets any visitor act as any demo user. monsterasp.net does
  not document a site-level password feature. Keep the address private and stop the site when
  it is not in use.
- Free plan: the app is stopped after 3 hours without requests, so the hourly background job
  only runs while the site is being used. Use the admin "run now" action when testing reminders.
- Free plan: no automatic backups, archived after 6 months without use, HTTPS enabled by
  support on request. Nothing on the test site is worth backing up; it can be rebuilt from the
  repository at any time.
- The database schema is created with `EnsureCreated` and, from release 3, upgraded in place at
  start-up by the schema upgrader (see "Database schema upgrades" below). Emptying the test
  database is only the fallback when the upgrader reports an error.
- Sign-in cookies do not survive an app restart or deployment unless the API persists its Data
  Protection keys. monsterasp.net recommends the `private/keys` folder next to `wwwroot`
  (`Path.Combine(ContentRootPath, "..", "private", "keys")`). Open follow-up for the backend.
- Uploaded test files are stored under `App_Data` inside the site folder. The deployment skips
  that folder so they survive a deploy. Moving the local store to the host's `private` folder
  would be cleaner (open follow-up for the backend).
- Logs: Control Panel > website > Logs. Start/stop/restart: website > Overview.

## 4a. Database schema upgrades (release 3)

There are still no EF Core migrations (adding them safely needs a compiler run against both
providers). Instead the app runs an idempotent, versioned upgrader on every start
(`src/api/Dafa.Contracts.Infrastructure/Data/SchemaUpgrader.cs`, called by `DatabaseInitializer`
right after `EnsureCreated`):

- Table `SchemaVersion` (`Version`, `Description`, `AppliedAt`), outside the EF model, records the
  steps that have run. A step that is recorded is not run again.
- Each step lists tables to create, columns to add and tables whose indexes must exist. The DDL is
  generated from the EF Core relational model of the current provider (store types, nullability,
  keys, index names and filters), so it is exactly what `EnsureCreated` would create, on SQL Server
  and on Sqlite.
- Every statement is guarded: `INFORMATION_SCHEMA` / `sys.indexes` on SQL Server, `sqlite_master`
  / `pragma_table_info` on Sqlite. New NOT NULL columns on existing tables get a default
  (`0`, empty text, the empty Guid, `0001-01-01`; `LegalEntities.IsActive` defaults to 1).
- Steps only add; nothing is ever dropped or rewritten. A failing table or column statement stops
  the start (logged); a failing index (for example duplicates in old data under a new unique
  index) is logged as a warning and the app runs without that index.
- After the steps, the upgrader compares the database with the model and logs `Schema drift:`
  for anything still missing (a forgotten step).

Steps: 1 = phase 2 baseline (concurrency `Version` columns, `LastRenewedDeadline`, reminder
`TermKey`/`Attempts`, unique indexes); 2 = release 3 tables and columns; 3 = release 4 (tables
`TemplateCategories`, `BrandingAssets`, `SignatureRequests`, `Signatories`; SharePoint / Entra
mapping columns on `LegalEntities` and `Departments`; `CanSendForSignature`,
`SignatureEntityIdsJson`, `HomeEntityId` on `Users`; `CategoryId`, `ContractTypeIdsJson`,
`Language` on `Templates`; `Theme` on `UserPreferences`, default `light`); 4 = SharePoint
hardening (table `StorageReconciliationReports`; `SiteId`, `DriveId`, `ItemId`, `RelativePath`,
`ETag`, `CTag`, `ContentHash` on `Documents`; `StoreDriveId`, `StoreFolderId`, `StoreFolderUrl` on
`Contracts`; `LogPending` on `ApprovalInstances`; `Attempts`, `NextAttemptAt` on `DocumentTexts`).
All new columns are nullable or have a default (`0`, `'light'`), so existing rows stay valid.

Adding a schema change in a later release: change the model, then append a new step to
`SchemaUpgrader.Steps` that lists the new tables, the new columns (nullable, or with a default
the upgrader knows; add a `DefaultOverrides` entry when 0/empty is wrong) and the tables whose
indexes changed. Never edit a released step. Renames and type changes are not supported by the
upgrader: do them as add-new-column + copy + stop-using-old, or move to EF migrations first.
Tested on Sqlite (`Release3Tests.SchemaUpgrader_*` and `Release4SchemaTests`: an old database,
also one with the release 3 schema, is upgraded and a second run does nothing). The SQL Server path is only desk-checked until the first deployment.

## 4b. Release 3 operations

Background jobs (Settings > Jobs, `GET/PUT admin/jobs`): `Reminders`, `Obligations`,
`ContractEnd`, `Digest`, `CounterpartySync` (supplier master source + due sync sources),
`DirectorySync`, `UploadCleanup` (old intake uploads, expired import sessions, and the search text
backfill of 100 documents per run), `DataRoomCleanup`. Each has its own schedule (Off / Hourly /
Daily at HH:mm / Weekly on a day at HH:mm, Europe/Copenhagen) and a recorded last run. The hosted
service checks every `Jobs:TickMinutes` (default 5) which jobs are due. `POST admin/jobs/{key}/run`
runs one now; `POST admin/jobs/run` still runs the release 2 set in one go.

Sync sources (Settings > Imports and sync): admin-defined Power BI or SQL queries that feed the
import pipeline. No secret is stored in the database or accepted by the API:

- SQL: the source names a connection configured on the server:
  `Sync:Connections:<name>:Kind` = `Sql`, `Sync:Connections:<name>:ConnectionString` (or
  `ConnectionStrings:<name>`), optional `:Description`. The query must start with SELECT or WITH;
  it runs inside a transaction that is rolled back, with a 60 s timeout. The app's own `Default`
  connection is refused.
- Power BI: `Sync:Connections:<name>:Kind` = `PowerBi` (for the list), token from
  `Suppliers:PowerBi:TenantId` / `ClientId` / `ClientSecret`, or the managed identity
  (`Suppliers:PowerBi:UseManagedIdentity=true`, optional `Suppliers:PowerBi:ManagedIdentityClientId`),
  else the `AzureAd:*` app registration. The app registration needs access to the workspace.
- `Sync:Provider` = `Demo` (default; test site) returns generated rows for any source and lists
  two demo connections (`DemoErp`, `DemoPowerBi`); `Live` runs the real queries.

Data room exports: zips are written to `DataRoom:LocalPath` (default `App_Data/dataroom`), kept 7
days, then deleted by `DataRoomCleanup`. Limits: 5000 documents or 2 GB per export. Built
synchronously in the request (the API answers 202 with status Ready or Failed).

Document search: text is extracted at upload with the bounded analyzer extractor (PDF, Word,
text; scans and images have no text) and stored in `DocumentTexts` (max 400,000 characters per
document, lower-cased copy for search). Search uses contains-matching in SQL over that table,
scoped to visible contracts, capped at 1000 matching documents per query.

## 4c. Release 4 operations

### How a legal entity is linked (app record, SharePoint site, Entra group)

There are three things with the same name, and the app only connects them where an
administrator says so. Nothing is matched by name behind the scenes.

| In the app (Settings) | In SharePoint | In Entra ID | What the link does |
|---|---|---|---|
| Legal entity (Settings > Legal entities) | One site, `https://<tenant>.sharepoint.com/sites/<name>` (field "SharePoint site") | One security group for the entity's management (field "Management group", the group's object id) | Site: every document of a contract of this entity is stored on this site. Group: its members are "entity management" for this entity in the app (they see all contracts of the entity). |
| Department (Settings > Departments) | One document library **with the same name on every entity's site** (field "Document library"; empty = the department's name) | One security group for the department across all entities (field "Group", the group's object id) | Library: a contract's files go to `<entity site>/<department library>/<contract number>/`. Group: its members are members of the department in the app. |
| Contract | One folder named by the contract number, created by the app at the first upload | none | The folder's ids and address are stored on the contract. |

Rules worth knowing:

- **The app is the only door.** The app talks to SharePoint as itself (application permission
  `Sites.Selected`, granted per site). Users need no permission on the sites or libraries and
  should not be given any: who may see a document is decided by the app's access model alone,
  and the link "Open in SharePoint" is shown to administrators only.
- **No fallbacks.** A legal entity without a linked site, or a department whose library does not
  exist on that site, cannot store files. The upload is refused with a sentence that says what
  is missing (`storage-not-configured`); nothing is ever stored on another site or in another
  library instead.
- **Test link** (button on each legal entity and department) asks Microsoft Graph whether the
  site, the libraries and the group exist and shows Linked / Error with a message. Saving a new
  address resets the state to "Not linked" until it is tested. In test mode (`Documents:Provider`
  = `Local`, `Directory:Provider` = `Seeded`) the test only checks the format and answers Linked with "Test mode: saved, used in SharePoint mode".
- **Access mapping** (Settings > Access mapping) is the whole picture on one page: every legal
  entity against every department, with the site, the library, the groups and the link state.
- **Memberships** come from the mapped groups when `Directory:Provider=Graph`: the `DirectorySync`
  job adds members of a mapped group and removes Entra-linked users who left it. Departments and
  entities **without** a group, and users that are not linked to Entra, are left exactly as
  maintained under Settings > Users. A group that cannot be read is skipped; nothing is removed
  after a failed call. Roles (ContractOwner, Approver, Admin), group management and "head of
  department" are always maintained in the app.

### Adding a new legal entity, end to end

1. **Entra:** create an assigned security group for the entity's management, for example
   "Contracts - DAFA Norge AS - Management", with a named owner. Copy its object id.
2. **SharePoint:** create the site (for example `/sites/contracts-dafa-norge`). In it, create
   one document library per department, named exactly as the "Document library" of each
   department in the app (by default the department's name; see Settings > Access mapping for
   the list). Turn on versioning. Do **not** add users or department groups to the site.
3. **Grant the app access to the site** (a SharePoint or Global administrator):
   `POST https://graph.microsoft.com/v1.0/sites/{site-id}/permissions` with role `write` for the
   app's client id, or `Grant-PnPAzureADAppSitePermission -AppId <client id> -DisplayName "DAFA Contracts" -Site <site url> -Permissions Write`.
   Without this step the app gets "access denied" for the new site, because `Sites.Selected`
   gives access to nothing by itself.
4. **App, Settings > Legal entities > New:** name, default currency, the site address and the
   management group's object id. Save, then press **Test link**. It must say Linked and list no
   missing libraries.
5. **App, Settings > Access mapping:** check the new entity's row: every department shows its
   library as found. A department marked "library missing" cannot store files for this entity
   until the library is created (or the department's library name is corrected).
6. **People:** run Settings > Jobs > Directory sync > Run now (production) so the management
   group's members become entity management; in test mode, set "Manages entities" on the users
   by hand. Give the people who will send contracts of this entity for signature the entity under
   Settings > Users > "May send for e-signature".
7. **Rules:** review approval rules, retention rules and field rules that are limited to legal
   entities, and the entity's currency under Settings > Currencies.
8. **Check:** create a draft contract for the new entity with a file, open Settings > Storage:
   the state must be Healthy, and the next reconciliation report must be empty for the entity.

Removing an entity: deactivate it in the app (contracts and files stay). Do not delete the site.

### Storage: what users see and what to watch

- All calls to Microsoft Graph go through one client that retries throttling (429) and
  temporary errors (502, 503, 504, timeouts, connection errors) up to `Graph:MaxAttempts` times,
  waits as long as SharePoint asks (`Retry-After`), otherwise 1, 2, 4 seconds with jitter, and
  never longer than 20 seconds. After five failed calls in a row it pauses all calls for 30
  seconds (circuit breaker). Every request carries the User-Agent `NONISV|DAFA|Contracts/1.0`.
- Users then see "SharePoint is busy. Try again in a moment." (HTTP 503,
  `urn:dafa:problem:storage-unavailable`, with `Retry-After` when SharePoint gave one), "This file
  can no longer be found in storage. An administrator has been notified." (404,
  `urn:dafa:problem:file-missing`) or the configuration sentence (409,
  `urn:dafa:problem:storage-not-configured`). Never a blank error.
- Reading a contract, the register, search and the dashboards never call SharePoint, so they
  keep working when SharePoint is down. Only upload, download and the data room need it.
- A final approval never fails because of storage: when the approval log cannot be stored, the
  approval is saved and the `UploadCleanup` job writes the log later. The same job retries the
  text extraction of documents it could not read (15 minutes, doubling, six attempts).
- Files are never overwritten. A second upload of a name becomes `name v2.ext`; if that name is
  taken in the folder, a short random suffix is added. Each document row stores site, library,
  item id, path, size and a SHA-256 of what was uploaded.
- Uploads up to 10 MB are one request; larger files use an upload session in 5 MB parts.
  Microsoft allows single requests up to about 250 MB, but the app's own limit is 25 MB per
  file, so that limit is never reached.
- **Settings > Storage** shows the state (Healthy, Degraded, Down, Not configured), the last
  success and failure, failures in the last 24 hours, pending approval logs and documents
  waiting for text extraction. `GET api/admin/storage/health`.
- **Reconciliation** (job `StorageReconciliation`, daily 02:00, and the button on the same
  page) compares the document rows with the store and lists missing files, files whose size or
  content changed, renamed files, files in a contract folder the app does not know, and
  contracts whose files lie under another entity's site or another department's library than
  the contract now belongs to. It **reports only**; nothing is moved, renamed or deleted.
  Administrators get a notification when it finds something. A run stops after 50 seconds or
  2000 findings and says so; the last 30 reports are kept.
- Changing a contract's legal entity or department does **not** move its files. New files go to
  the new place; the report lists the old ones as "MisplacedFolder" for moving by hand.

### E-signature

- **Who may send:** only users with "May send for e-signature" (Settings > Users), optionally
  limited to named legal entities. Administrators have no implicit right. Every send, reminder,
  cancellation, signature, decline and expiry is in the contract's activity log.
- **Settings > E-signature:** provider, whether the contract must be Approved first (default
  yes), reminder days (default 3 and 7), days until a request expires (default 30).
- **Providers.** `Simulated` works completely inside the app (test site: a "simulate" panel lets
  a tester sign or decline for each signer; the signed copy is a PDF the app writes itself).
  `DocuSign` and `Penneo` are implemented against their published REST APIs but **have never
  been run against the real services**; treat them as a starting point, run every step against
  the provider's sandbox first, and expect corrections.
  - DocuSign: `Signing:DocuSign:BaseUrl`, `:AuthServer`, `:AccountId`, `:IntegrationKey`,
    `:UserId`, `:PrivateKeyPath` (RSA key file for JWT grant; consent for "signature
    impersonation" must be given once).
  - Penneo: `Signing:Penneo:BaseUrl`, `:ApiKey`, `:ApiSecret`.
- **How status comes back:** the provider calls `POST /api/esign/webhooks/{provider}` and the
  `SignatureSync` job (hourly) also asks the provider, so a lost webhook only delays things.
  The webhook is the one API route without a session and without the `X-Requested-With` header.
  It is closed (404) until `Signing:Webhook:Secret` holds at least 24 characters, and each call
  must then prove the secret: the secret itself in `X-Dafa-Webhook-Secret`, or an HMAC-SHA256 of
  the body in `X-Dafa-Signature` (hex) or `X-DocuSign-Signature-1` (Base64, DocuSign Connect).
  Ten wrong attempts from one address in ten minutes block that address.
- **When everyone has signed** the signed file is stored as a new Main document
  ("<name> – signed.pdf"), the signed date and method are set, and an Approved contract becomes
  Active. A declined, cancelled or expired request leaves the contract as it was.
- Rotating the webhook secret: set the new value on the server and at the provider at the same
  time; calls with the old secret are refused from that moment (the job still catches up).

### Branding, formats, templates, pickers

- Logo (Settings > Branding): PNG, JPEG, WebP or SVG, at most 1 MB, checked by content, not by
  file name. SVG files with scripts, event attributes, external references or embedded HTML are
  refused, and every logo is served with a policy that forbids scripts. Three variants: full,
  compact (collapsed menu) and dark (dark theme). The files are stored in the database
  (`BrandingAssets`) and served without sign-in at `api/branding/logo`, because the sign-in page
  shows them.
- Number and date formats (Settings > General) apply to everyone; the theme (light, dark,
  system) is each user's own preference.
- Template categories (Settings > Template categories) group the templates; a category in use
  cannot be deleted, only deactivated.
- "Who may create counterparties" (Settings > General): administrators only (default), contract
  owners, or all users. Contracts always point to a counterparty from the list.

### Jobs added in release 4

`SignatureSync` (hourly) and `StorageReconciliation` (daily 02:00). `UploadCleanup` now also
writes pending approval logs and retries text extraction. `DirectorySync` reads memberships from
the mapped groups.

## 5. Moving from test to production

Do these in order. Items 5.1-5.4 need a DAFA Entra administrator.

### 5.1 Azure resources

1. Create a resource group in `swedencentral` and an Entra security group for SQL administrators.
2. Copy `infra/main.example.bicepparam` to `infra/main.bicepparam`, fill in the values, and run
   `az deployment group what-if`, then `az deployment group create`, with `infra/main.bicep`.
3. Grant the web app's managed identity access to the database. Bicep cannot do this; a member
   of the SQL admin group runs this in the database (the user name is the web app name from
   the deployment outputs):

   ```sql
   CREATE USER [<webAppName>] FROM EXTERNAL PROVIDER;
   ALTER ROLE db_datareader ADD MEMBER [<webAppName>];
   ALTER ROLE db_datawriter ADD MEMBER [<webAppName>];
   ALTER ROLE db_ddladmin  ADD MEMBER [<webAppName>]; -- needed while the schema is created by the app
   ```

4. Add a production deployment workflow (GitHub OIDC federated credential to Azure, no stored
   password; publish for `linux-x64` or portable instead of `win-x86`). Not built yet.

What the skeleton sets: HTTPS only, TLS 1.2 minimum, FTP and basic publishing credentials off,
Always On, health check on `/healthz`, system-assigned managed identity, Azure SQL with
Entra-only authentication and 14-day point-in-time restore, Key Vault with RBAC and purge
protection, diagnostics to Log Analytics. What it deliberately leaves for the network design:
private endpoints (SQL and Key Vault are reachable on public endpoints, protected by identity).

### 5.2 Entra app registration

1. Register a single-tenant app "DAFA Contracts". Platform: Web. Redirect URI
   `https://<host>/signin-oidc`, front-channel logout `https://<host>/signout-oidc`.
2. Credential for Microsoft Graph, chosen with `Graph:Auth`:
   - `ManagedIdentity` (preferred on Azure App Service): no secret at all. Give the app
     service's managed identity the Graph application permissions of section 5.3 and the site
     grants of section 5.4. `AzureAd:ManagedIdentityClientId` only for a user-assigned identity.
   - `Certificate`: `AzureAd:ClientCertificateThumbprint` (certificate in the App Service
     certificate store, with `WEBSITE_LOAD_CERTIFICATES` set) or `AzureAd:ClientCertificatePath`
     (+ `AzureAd:ClientCertificatePassword`) for a PFX file.
   - `ClientSecret` (default): `AzureAd:ClientSecret` from Key Vault (`AzureAd--ClientSecret`).
     A secret expires: set a renewal reminder well before the date. When it expires, sign-in
     may keep working while every upload and download fails with "SharePoint is busy".
   The sign-in of users (OpenID Connect) still uses the app registration's own credential.
3. Token configuration: add the `groups` claim only if group count per user is small;
   otherwise the app reads memberships from Graph.
4. App roles (assign to groups under Enterprise applications): `Reader`, `ContractOwner`,
   `Approver`, `Admin`. Set "Assignment required" to Yes so only assigned users can sign in.
5. Apply the normal Conditional Access policies (MFA, compliant device) to the app.

### 5.3 Microsoft Graph permissions

The app works with **application** permissions only ("the app is the only door"). There are no
delegated SharePoint permissions and no calls on behalf of the signed-in user. An administrator
grants consent. This is the list the code actually uses:

| Permission | Type | Used for | Where in the code |
|---|---|---|---|
| `openid`, `profile`, `User.Read` | Delegated | Sign-in only | Microsoft.Identity.Web |
| `Sites.Selected` | Application | All SharePoint work: resolve the site, list its libraries, create contract folders, upload, download, read item properties | `SharePointDocumentStore`, `GraphOrgLinkProbe` |
| `User.Read.All` | Application | Directory sync: `GET /users` with `accountEnabled` and `userType` (`User.ReadBasic.All` is not enough for those two properties) | `GraphDirectoryService` |
| `GroupMember.Read.All` | Application | Directory sync: members of the mapped groups (`/groups/{id}/transitiveMembers`); "Test link": the group's name | `GraphDirectoryService`, `GraphOrgLinkProbe` |
| `Mail.Send` | Application | Reminders and notifications from one shared mailbox (`Notifications:Provider=Graph`). Restrict the app to that mailbox with Exchange RBAC for Applications. | `GraphMailNotifier` |
| Power BI `Dataset.Read.All` | Service principal | Supplier master and Power BI sync sources via `executeQueries`; needs the tenant setting that allows service principals to use Power BI APIs, and workspace access. | supplier source, sync readers |

Not needed and not to be granted: `Sites.Read.All`, `Sites.ReadWrite.All`, `Files.ReadWrite.All`,
`Group.Read.All`, `Directory.Read.All`, any delegated `Sites.*`. `TeamsActivity.Send` is not used
(Teams messages are only recorded in the outbox).

`Sites.Selected` grants nothing by itself. For each contract site, a SharePoint administrator
grants the app `write` on that site (`POST /sites/{site-id}/permissions` in Graph, or
`Grant-PnPAzureADAppSitePermission`). The health check on Settings > Storage asks Graph a
tenant-wide question that `Sites.Selected` answers with 403; that is expected and counts as
"reachable".

### 5.4 SharePoint structure

- One site per legal entity (nine sites): DAFA Group A/S, DAFA A/S, DAFA Building Solutions A/S,
  DAFA Sverige AB, DAFA Deutschland GmbH, DAFA Sealing Technology (Tianjin) Co., Ltd.,
  DAFA US Inc., DAFA Polska sp. z o.o., DAFA Italia S.r.l. Each site is linked to its legal
  entity under Settings > Legal entities (section 4c); the app does not find sites by name.
- In each site, one document library per department (13: Management, Finance, Purchasing,
  Sales, Production, Supply chain and logistics, Quality, R&D and engineering, Maintenance and
  facilities, HSE, IT, HR, Marketing), named exactly as the department's "Document library" in
  the app. The app does not create libraries.
- In each library, one folder per contract, named by contract number (`C-YYYY-NNNN`), created
  by the app at the first upload. Files keep their names; later versions are `name v2.ext`.
- **Permissions: the app only.** Users, department groups and entity groups get **no**
  permission on the contract sites and libraries. Site owners are the SharePoint administrators
  and, if wanted, a small break-glass group. The app's access model is the only place where
  "who may see what" is decided; SharePoint permissions are not a second copy of it, so they
  cannot drift apart. A user who opens a site address directly gets "access denied", which is
  intended. Consequences: co-authoring in Word Online and browsing in SharePoint are not
  offered; files are opened through the app.
- Turn on versioning in the libraries (it protects against mistakes by administrators; the app
  itself never overwrites a file). Retention is driven by the app's retention rules; agree with
  IT whether Purview retention labels should back them up, and note that a label that deletes
  files will show up as "missing file" in the reconciliation report.
- Do not rename, move or delete contract folders and files in SharePoint. Renaming a file or
  moving it within the same library keeps the link (the app stores item ids); moving it to
  another library or site, or deleting it, breaks it. The nightly reconciliation reports all of
  these.

### 5.5 Dynamic groups

Membership follows HR master data in Entra, so nobody maintains access lists by hand.

- One dynamic security group per department, across all entities, for example
  `(user.department -eq "Finance") and (user.accountEnabled -eq true)`. Its object id goes into
  the department's "Group" field in the app (section 4c).
- One assigned (not dynamic) group per legal entity for the entity's management, with a named
  owner and a periodic access review. Its object id goes into the legal entity's "Management
  group" field. These groups are small and sensitive.
- Group management is not a group: it is a switch on the user in the app.
- The groups are used for memberships in the app only. They are **not** given access to the
  SharePoint sites (section 5.4).

Prerequisite: `department` must be filled in consistently for every user. The names need not
match the names in the app: the link is the group's object id. Dynamic groups require Entra ID P1.

### 5.6 Cut-over checklist

- [ ] `Auth:Mode=Entra`, `Auth:BootstrapAdmins` set to the first administrators, `Seed:DemoData=false`, `Documents:Provider=SharePoint`, `Directory:Provider=Graph`, `Notifications:Provider=Graph`, `Graph:Auth` chosen
- [ ] Every legal entity linked to its site and management group, every department to its group; Settings > Access mapping shows no errors; Settings > Storage is Healthy
- [ ] The first-run checklist of section 5.7 done on a test site
- [ ] E-signature: provider chosen and tried in the provider's sandbox, `Signing:Webhook:Secret` set, the people who may send named; or left on None
- [ ] Database is empty of demo data (new database, not a copy of test)
- [ ] Migrations introduced before the first production schema change (replace `EnsureCreated`)
- [ ] Approval rules replaced with the real authority matrix; reminder settings reviewed
- [ ] Access model tested with real users from at least two entities and two departments
- [ ] Restore test of the database done once
- [ ] Alerts configured (section 7); test site stopped or deleted

### 5.7 First run against a real tenant

None of the Microsoft Graph code has ever run against a real tenant: it is written from the
documentation and tested against a simulated Graph. Do this once with **one test site with two
libraries** before touching the nine real sites, and write down what differs.

1. App registration: `Sites.Selected`, `User.Read.All`, `GroupMember.Read.All` (application)
   consented; no delegated SharePoint permissions.
2. Grant the app `write` on the test site. Link it to a test legal entity and press Test link:
   Linked. Link another, ungranted site to a second entity: Test link must show an error, and an
   upload must be refused with the configuration sentence.
3. A normal user with **no** SharePoint permission uploads and downloads through the app, and
   gets "access denied" when opening the site address directly.
4. Upload a small PDF to a contract with no folder yet: the folder is created, and the contract
   shows "Open in SharePoint" to an administrator only.
5. Upload 5 MB, 12 MB and 25 MB files (the 12 and 25 MB ones use an upload session); note the times.
6. Upload the same file name twice (expect `name v2.ext`), then twice at the same moment
   (expect two files, one with a short suffix; nothing overwritten).
7. File names with `#`, `%`, `&`, `'`, Danish, Polish and Chinese characters, a 120-character
   name, and `~$test.docx`.
8. Download; rename the file in SharePoint and download again (must work); move it within the
   library (must work); move it to the other library, then delete it (expect "This file can no
   longer be found in storage", an entry in the activity log and a notification to
   administrators); restore it from the recycle bin (must work again). Run the reconciliation
   after each step and compare the report.
9. A department whose library does not exist, an entity with no site: the upload is refused,
   not redirected, and a new contract created with a file is kept as a draft without the file.
10. Approve a contract to the end; the approval log file arrives. Repeat with the app's access
    to the site removed for a moment: the approval still completes, Settings > Storage shows one
    pending log, and the Upload cleanup job writes it once access is back.
11. Create a contract through intake and check the main document becomes searchable.
12. Data room export of 200+ documents; watch the log for 429/503 and for "SharePoint is busy".
13. Force failures: a wrong site address, an expired or wrong credential, no network. Check what
    the user sees (the three messages of section 4c), what is logged, and that Settings >
    Storage turns Degraded or Down and back to Healthy.
14. Run the directory sync with one mapped department group and one mapped management group:
    members appear, a user removed from the group loses the membership at the next run, users
    of unmapped departments are untouched. Check tenants with more than 999 users are paged.
15. Check that errors in the App Service log carry enough to open a Microsoft support case
    (time, site, the HTTP status). The Graph `request-id` header is **not** logged yet (known gap).
16. With `Graph:Auth=Certificate` or `ManagedIdentity`: repeat steps 4 and 8 once.

## 6. Backup and recovery

| What | Test | Production (planned) |
|---|---|---|
| Database | None on the Free plan. Rebuild from seed. | Azure SQL automatic backups, point-in-time restore 14 days (parameter), geo-redundant backup storage. Consider long-term retention (monthly, several years) because contract metadata and the audit trail are evidence. |
| Files | Demo files only. | SharePoint: versioning, recycle bin (93 days), plus DAFA's Microsoft 365 backup if one is in place. |
| Secrets | GitHub secrets; can be re-created from the Control Panel. | Key Vault with soft delete (90 days) and purge protection. |
| Code and infrastructure | Git repository. | Git repository; infrastructure is re-creatable from `infra/main.bicep`. |

Recovery of production in outline: restore the database to a point in time (new database
name), point the app's connection string at it, restart. Files are restored in SharePoint
independently; the database stores each document's SharePoint key, so restore both to a
consistent point when possible.

## 7. Monitoring

- `/healthz` is used by the App Service health check and can be polled by any uptime monitor.
- Planned alerts in Azure Monitor: health check failing, HTTP 5xx rate, response time, SQL
  DTU/CPU above 80 %, failed sign-ins spike, Key Vault secret/certificate close to expiry.
- Functional monitoring inside the app: the admin outbox (were reminders produced and sent)
  and the audit log.
- A weekly check that the background job has run recently is worth an alert of its own:
  missed notice deadlines are the main business risk this app exists to prevent.

## 8. Handover and due-diligence notes

- **Ownership:** business owner is the CFO; DAFA IT owns operations and the Azure subscription after go-live.
- **Code:** all source, infrastructure-as-code and documentation are in this repository. No
  proprietary frameworks; no UI kit; standard .NET, EF Core, React and Vite.
- **Data location:** test hosting is in the EU; production is planned for `swedencentral`,
  files in DAFA's own Microsoft 365 tenant.
- **Personal data:** names, work e-mail addresses, department and company of employees;
  contact details that appear inside contracts. No special categories are intended. Customer
  and HR contracts are out of scope.
- **Security model:** server-side access filter in one place; audit trail of every change,
  decision and file open; production has no shared passwords (managed identity, Entra-only SQL).
- **Third parties in production:** Microsoft (Azure, Entra, Microsoft 365, optionally Azure
  OpenAI). monsterasp.net is used for the test site only and never receives real data.
- **Dependencies to keep current:** .NET 10 is a long-term support release; npm and NuGet
  packages should be reviewed at least quarterly (Dependabot is not configured yet).
- **Known gaps at this stage:** no EF migrations; no production deployment workflow; Azure
  skeleton never deployed; Application Insights wiring in the API to be confirmed; no
  site-level protection on the test site; e-signing is manual.
- **Where decisions are recorded:** `docs/DECISIONS.md` (append-only). Current state: `docs/STATUS.md`.
