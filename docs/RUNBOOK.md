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
| `Auth:Mode` | `Dev` | `Entra` | Dev = pick a seeded demo user, no real authentication. Entra = OpenID Connect via Microsoft.Identity.Web. |
| `Database:Provider` | `Sqlite` (local) / `SqlServer` (test site) | `SqlServer` | EF Core provider. |
| Connection string (assumed `ConnectionStrings:Default`) | monsterasp.net "Local access" string | Passwordless, managed identity (`Authentication=Active Directory Default`) | The test deployment writes the string under `ConnectionStrings:Default`, `ConnectionStrings:DefaultConnection` and `Database:ConnectionString` so it works whichever the API reads. |
| `Storage:Mode` | `Local` (disk under `App_Data/files`) | `SharePoint` | Where contract documents are stored. |
| `Ai:Mode` | `Heuristic` | `AzureOpenAI` (or stay on `Heuristic`) | Field extraction and summary at intake. |
| `Suppliers:Mode` | `Seed` | `PowerBi` | Supplier master source. |
| `Notifications:Mode` | `Outbox` (rows shown in admin) | `Graph` (mail + Teams) | How reminders are delivered. |
| `Seed:DemoData` | `true` | `false` | Creates demo users, contracts and rules. Must be `false` in production. |
| `AzureAd:Instance`, `AzureAd:TenantId`, `AzureAd:ClientId`, `AzureAd:ClientSecret` (assumed, Microsoft.Identity.Web convention) | not used | set | Entra app registration. Secret comes from Key Vault. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | not used | set by Bicep | Telemetry. Only has an effect once the API includes Application Insights / OpenTelemetry (see known gaps). |

E-sign is `None` in both modes for version 1 (the owner marks a contract as signed). DocuSign is a later stub.

How the test site gets its settings: the **Deploy test** workflow writes
`appsettings.Production.json` into the published folder at deploy time (Dev sign-in, SQL
Server, demo data, connection string from the GitHub secret). The file is never committed.
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
`MONSTERASP_WEBDEPLOY_PASSWORD`, `MONSTERASP_DB_CONNECTION_STRING`.
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
- The database schema is created with `EnsureCreated` (no migrations yet). After a change to
  the data model, the test database has to be emptied (drop the tables or recreate the
  database in the Control Panel) and the app restarted so it is recreated and reseeded.
- Sign-in cookies do not survive an app restart or deployment unless the API persists its Data
  Protection keys. monsterasp.net recommends the `private/keys` folder next to `wwwroot`
  (`Path.Combine(ContentRootPath, "..", "private", "keys")`). Open follow-up for the backend.
- Uploaded test files are stored under `App_Data` inside the site folder. The deployment skips
  that folder so they survive a deploy. Moving the local store to the host's `private` folder
  would be cleaner (open follow-up for the backend).
- Logs: Control Panel > website > Logs. Start/stop/restart: website > Overview.

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
2. Credential: prefer a certificate or a federated credential; if a client secret is used,
   store it in Key Vault as `AzureAd--ClientSecret` and set a renewal reminder.
3. Token configuration: add the `groups` claim only if group count per user is small;
   otherwise the app reads memberships from Graph.
4. App roles (assign to groups under Enterprise applications): `Reader`, `ContractOwner`,
   `Approver`, `Admin`. Set "Assignment required" to Yes so only assigned users can sign in.
5. Apply the normal Conditional Access policies (MFA, compliant device) to the app.

### 5.3 Microsoft Graph permissions

Least privilege; an administrator grants consent. Confirm the exact list against the backend
implementation before granting.

| Permission | Type | Used for |
|---|---|---|
| `openid`, `profile`, `User.Read` | Delegated | Sign-in |
| `Sites.Selected` | Delegated and Application | SharePoint files, limited to the nine contract sites. Delegated for user actions (on behalf of the user); application for background work such as writing the approval log. |
| `GroupMember.Read.All`, `User.ReadBasic.All` | Application (or delegated) | Resolve department/entity membership and people pickers |
| `Mail.Send` | Application | Reminders, from one shared mailbox. Restrict the app to that mailbox (Exchange RBAC for Applications). |
| `TeamsActivity.Send` | Application | Teams notifications (optional) |
| Power BI `Dataset.Read.All` | Delegated or service principal | Supplier master via `executeQueries`; needs the tenant setting that allows service principals to use Power BI APIs and workspace access. |

`Sites.Selected` grants nothing by itself. For each of the nine sites, a SharePoint
administrator grants the app `write` on that site (`POST /sites/{site-id}/permissions` in
Graph, or `Grant-PnPAzureADAppSitePermission`). Do not grant `Sites.ReadWrite.All`.

### 5.4 SharePoint structure

- One site per legal entity (nine sites): DAFA Group A/S, DAFA A/S, DAFA Building Solutions A/S,
  DAFA Sverige AB, DAFA Deutschland GmbH, DAFA Sealing Technology (Tianjin) Co., Ltd.,
  DAFA US Inc., DAFA Polska sp. z o.o., DAFA Italia S.r.l.
- In each site, one document library per department (13: Management, Finance, Purchasing,
  Sales, Production, Supply chain and logistics, Quality, R&D and engineering, Maintenance and
  facilities, HSE, IT, HR, Marketing).
- In each library, one folder per contract, named by contract number (`C-YYYY-NNNN`).
- Library permissions mirror the access model: break inheritance per library and grant the
  department group and the entity management group; group management on all. The app enforces
  the same rules itself, and because user actions go to SharePoint on behalf of the user,
  SharePoint permissions are a second line of defence.
- Turn on versioning in the libraries. Retention is driven by the app's retention rules;
  agree with IT whether Purview retention labels should back them up.

### 5.5 Dynamic groups

Membership follows HR master data in Entra, so nobody maintains access lists by hand.

- One dynamic security group per department, across all entities, for example
  `(user.department -eq "Finance") and (user.accountEnabled -eq true)`.
- One group per legal entity if needed for site membership, for example
  `(user.companyName -eq "DAFA Sverige AB") and (user.accountEnabled -eq true)`.
- Entity management and group management are small, sensitive groups: use assigned (not
  dynamic) membership with a named owner and a periodic access review.

Prerequisite: `companyName` and `department` must be filled in consistently for every user
and match the names in the app exactly. Dynamic groups require Entra ID P1.

### 5.6 Cut-over checklist

- [ ] `Auth:Mode=Entra`, `Seed:DemoData=false`, `Storage:Mode=SharePoint`, `Notifications:Mode=Graph`
- [ ] Database is empty of demo data (new database, not a copy of test)
- [ ] Migrations introduced before the first production schema change (replace `EnsureCreated`)
- [ ] Approval rules replaced with the real authority matrix; reminder settings reviewed
- [ ] Access model tested with real users from at least two entities and two departments
- [ ] Restore test of the database done once
- [ ] Alerts configured (section 7); test site stopped or deleted

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
