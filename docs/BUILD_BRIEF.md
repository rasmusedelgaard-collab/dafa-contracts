# DAFA Contracts — build brief (source of truth for all build agents)

Internal contract management web app for DAFA Group (Danish manufacturer, 9 legal entities).
English UI only. ~800 active contracts, internal users only. Files will live in SharePoint in
production; the first deployment is a TEST environment on monsterasp.net (plain ASP.NET Core
hosting + MSSQL), with test data only. Production later moves to Azure.

Read this whole file before writing code. Do not change the API contract or repo layout
without recording the change in `docs/DECISIONS.md` (append-only, one line per decision).

## 1. Repo layout (each agent stays inside its own folders)

```
/src/web                      React 19 + TypeScript + Vite SPA            (frontend agent)
/src/api/Dafa.Contracts.Api   ASP.NET Core .NET 10 host: API + serves SPA  (backend agent)
/src/api/Dafa.Contracts.Core  domain, application services, interfaces     (backend agent)
/src/api/Dafa.Contracts.Infrastructure  EF Core, providers                 (backend agent)
/tests/Dafa.Contracts.Tests   xUnit tests                                  (backend agent)
/infra                        Bicep for Azure (later production)           (devops agent)
/.github/workflows            CI + deploy                                  (devops agent)
/docs                         brief, decisions, runbook, design reference
Dafa.Contracts.sln at repo root (backend agent)
```

IMPORTANT environment limits: this workspace has Node 22 + npm but NO .NET SDK and no NuGet
access. Backend code cannot be compiled here; GitHub Actions compiles and tests it. Backend
agents must therefore write conservative, idiomatic C# (no exotic APIs, no preview features),
double-check usings/namespaces/signatures by reading their own files back, and keep packages
to well-known ones with explicit versions. Frontend must `npm run build`, `npm run lint` and
`npm test` cleanly here. Do NOT `git commit` or `git push`; the project manager does that.

## 2. Architecture

- Backend-for-frontend: browser talks only to the .NET API (cookie session). SPA build output
  is copied to `Dafa.Contracts.Api/wwwroot` at publish and served with SPA fallback.
- Every external dependency sits behind an interface in Core with TWO implementations chosen
  by configuration, so the test site runs with no Microsoft tenant:

| Concern | Interface | Test mode (default) | Production mode |
|---|---|---|---|
| Sign-in | (auth scheme) | `Auth:Mode=Dev`: pick a seeded demo user, cookie | `Entra`: Microsoft.Identity.Web OIDC + cookie |
| Directory/groups | `IDirectoryService` | seeded users/groups in DB | Microsoft Graph |
| Files | `IDocumentStore` | `Local`: disk under `App_Data/files` | `SharePoint`: Graph, on behalf of user; site per entity, library per department, folder per contract |
| AI extraction/summary | `IContractAnalyzer` | `Heuristic`: regex/keyword extraction from text | `AzureOpenAI` |
| Supplier master | `ISupplierSource` | `Seed` | `PowerBi` executeQueries (later SQL DW) |
| Notifications | `INotifier` | `Outbox`: rows in DB shown in admin | `Graph` mail + Teams |
| E-sign | `ISignatureProvider` | `None` (mark as signed manually) | DocuSign later (stub only) |

- Database: EF Core. `Database:Provider` = `Sqlite` (local/dev default) or `SqlServer`
  (monsterasp MSSQL / Azure SQL). Migrations-free for now: `EnsureCreated` + idempotent seeder
  guarded by `Seed:DemoData=true`. Keep the model portable across both providers.
- Background work (reminders, escalations, obligation reminders, supplier sync) = a
  `BackgroundService` in the API host running hourly + an admin "run now" endpoint. (Azure
  Functions is deferred; keep job logic in Core services so it can move.)
- Audit: every create/update/decision/approval/access grant/file open writes an `AuditEvent`.

## 3. Access model (enforced in ONE place: `IContractAccessService` + query filter)

A user sees a contract if ANY is true:
1. member of the contract's department (departments cut across all legal entities);
2. entity management for the contract's legal entity;
3. group management (sees all);
4. owner, delegate or responsible on the contract;
5. admin.
Current approver of a pending step also gets read access while the step is open.
Functional roles: `Reader`, `ContractOwner` (create/edit own), `Approver`, `Admin`.
Every list/detail/document endpoint must go through this filter; never filter in the frontend.
Dev mode has a user switcher so the model can be tested with different demo users.

## 4. Domain

Legal entities (seed, exact names): DAFA Group A/S, DAFA A/S, DAFA Building Solutions A/S,
DAFA Sverige AB, DAFA Deutschland GmbH, DAFA Sealing Technology (Tianjin) Co., Ltd., DAFA US Inc.,
DAFA Polska sp. z o.o., DAFA Italia S.r.l.
Departments (seed): Management, Finance, Purchasing, Sales, Production, Supply chain and
logistics, Quality, R&D and engineering, Maintenance and facilities, HSE, IT, HR, Marketing.
Contract types (seed): Supplier – direct materials; Supplier – indirect and services; Logistics;
IT and SaaS; Lease and property; Finance and insurance; NDA; R&D and partnership;
Corporate (shareholder, M&A); Other. (Customer and HR contracts are out of scope.)
Status: Draft, InApproval, Approved, Active, NoticeGiven, Expired, Terminated.

Contract fields: id, contractNo (auto `C-YYYY-NNNN`), title, typeId, supplierId/counterpartyName,
entityId, departmentId, ownerId, delegateId?, responsibleId?, reminderRecipient
(Owner|Delegate|Responsible), startDate, endDate? (null = open-ended), noticePeriodDays,
autoRenew, renewalMonths?, annualValue?, totalValue?, currency, paymentTerms?,
priceAdjustment?, priceAdjustmentDate?, governingLaw?, liabilityCap?, exclusivity?,
changeOfControl (required bool), summary?, status, signedDate?, signingMethod?,
renewalDecision? (Renew|Renegotiate|GiveNotice|LetExpire) + decidedBy/At/comment.
Calculated: noticeDeadline = endDate − noticePeriodDays; daysToNotice; nextReminderDate.
Required to save: title, type, counterparty, entity, department, owner, reminderRecipient,
startDate, endDate-or-openEnded, noticePeriodDays, autoRenew, currency, annualValue or
totalValue, changeOfControl.

Other tables: ContractRole (derived from the three role fields is fine), ContractLink
(fromId, toId, kind: Amendment|Appendix|Framework|Related), Obligation (description, dueDate?,
frequencyMonths?, responsibleId, status Open|Done, lastCompletedAt), Document (contractId, name,
kind Main|Appendix|Amendment|ApprovalLog|Other, storeKey, version, size, uploadedBy/At),
ApprovalRule, ApprovalInstance, ApprovalStep, Reminder (kind, dueDate, sentAt, recipientId),
Supplier, RetentionRule (typeId, years?, action ReviewThenDelete|DeleteAutomatically|Keep,
reviewerDepartmentId?), Template, AuditEvent, OutboxMessage, AppUser, UserDepartment,
UserEntityManagement, HolidayCover (userId, deputyId, from, to), ReminderSettings.

### Approvals
Rules are admin-configured: match on entityId?, typeId?, departmentId?, minValue?, maxValue?
(null = any); most specific match wins (count of non-null criteria, then priority). A rule has
ordered stages; each stage has 1..n approvers (a specific user, or a role resolver:
DepartmentHead of the contract's department, EntityManager, GroupCFO, GroupCEO, Legal) who act in
PARALLEL; the stage completes when all approve; any reject returns the contract to Draft with the
comment. Approvers can delegate a step to another user; active HolidayCover reroutes to the deputy
automatically. On final approval: status Approved, an approval log document is generated (plain
PDF or text file is fine) into the contract's documents, then the owner marks it signed
(signedDate, method) -> Active. Seed sensible default rules with placeholder value bands
(<1m, 1–5m, 5–20m, >20m DKK) — the real authority matrix comes later via admin UI.

### Reminders
Defaults (admin-editable): 120, 60, 30 days before noticeDeadline to the chosen recipient;
escalate to owner at 14 days if no renewalDecision. Obligations: remind 14 days before due.
Idempotent (one Reminder row per contract+kind). Recording a decision stops further reminders;
`Renew` on an auto-renew contract rolls endDate forward by renewalMonths and clears the decision.

### Intake
(a) Upload a PDF/DOCX -> `IContractAnalyzer` returns suggested fields + short summary + deviations
from DAFA standard (list of {severity High|Medium|Low, text, standard}); user accepts each field.
(b) Start from a Word template: fill content controls / `{{placeholders}}` from the form, save the
draft .docx to the contract's documents (DocumentFormat.OpenXml). Ship 2 simple seeded templates
(Supply agreement, Mutual NDA) generated in code or as embedded resources.

## 5. API contract (JSON camelCase, dates ISO `yyyy-MM-dd`, enums as strings, errors RFC 7807)

```ts
type Id = string; // GUID
interface Me { id: Id; name: string; email: string; initials: string; roles: ('Reader'|'ContractOwner'|'Approver'|'Admin')[];
  departmentIds: Id[]; managedEntityIds: Id[]; isGroupManagement: boolean; authMode: 'Dev'|'Entra' }
interface Lookups { entities: {id:Id;name:string}[]; departments: {id:Id;name:string}[];
  contractTypes: {id:Id;name:string}[]; users: {id:Id;name:string;initials:string;departmentIds:Id[]}[]; currencies: string[] }
interface ContractListItem { id:Id; contractNo:string; title:string; counterparty:string; typeName:string; entityName:string;
  departmentName:string; ownerName:string; ownerInitials:string; noticeDeadline:string|null; daysToNotice:number|null;
  annualValue:number|null; currency:string; status:string; missingData:string[] }
interface Paged<T> { items:T[]; total:number; page:number; pageSize:number }
interface ContractDetail extends ContractListItem { /* all contract fields from section 4 by the same names */
  summary:string|null; lifecycle:{name:string;date:string|null;state:'done'|'current'|'upcoming'|'alert'}[];
  people:{role:'Owner'|'Delegate'|'Responsible';userId:Id;name:string;initials:string;getsReminders:boolean}[];
  documents:{id:Id;name:string;kind:string;version:number;size:number;uploadedAt:string;uploadedBy:string}[];
  obligations:Obligation[]; links:{id:Id;contractId:Id;title:string;kind:string;direction:'out'|'in'}[];
  approval:ApprovalInstance|null; reminders:{kind:string;dueDate:string;sentAt:string|null}[];
  activity:{at:string;who:string;text:string}[]; access:string[]; deviations:Deviation[];
  canEdit:boolean; canDecideRenewal:boolean; storeUrl:string|null }
interface Obligation { id:Id; description:string; dueDate:string|null; frequencyMonths:number|null; responsibleId:Id; responsibleName:string; status:'Open'|'Done' }
interface Deviation { severity:'High'|'Medium'|'Low'|'None'; text:string; standard:string }
interface ApprovalInstance { id:Id; contractId:Id; contractTitle:string; status:'Pending'|'Approved'|'Rejected'; currentStage:number;
  stages:{order:number; steps:{id:Id;approverId:Id;approverName:string;initials:string;roleLabel:string;
    status:'Waiting'|'Pending'|'Approved'|'Rejected'|'Delegated';decidedAt:string|null;comment:string|null}[]}[] }
interface HomeData { decisionsThisWeek:number; renewalsThisWeek:number; approvalsWaiting:number;
  renewingIn90:{count:number;valueDkk:number}; myContracts:number;
  tasks:{kind:'Renewal'|'Approval'|'Obligation'|'MissingData'; title:string; meta:string; pill:string;
    urgency:'high'|'medium'|'low'|'info'; contractId:Id|null; approvalStepId:Id|null; obligationId:Id|null}[];
  noticeByMonth:{month:string;count:number}[]; recent:{id:Id;title:string}[] }
```

Endpoints (all under `/api`, all require auth except dev login):
- `GET me`, `GET lookups`, `GET suppliers?q=`, `GET home`, `GET search?q=` (contracts + suppliers, top 8)
- `GET contracts?view=all|due90|approval|mine|missing&q=&entityId=&departmentId=&typeId=&status=&sort=&page=&pageSize=` -> Paged<ContractListItem>
- `GET contracts/export?…same filters` -> CSV download
- `GET contracts/{id}` -> ContractDetail; `POST contracts` (draft from form; body = contract fields + optional `uploadId`); `PUT contracts/{id}`
- `POST contracts/{id}/submit` (starts approval); `POST contracts/{id}/sign {signedDate, method}`
- `POST contracts/{id}/renewal-decision {decision, comment}`
- `POST contracts/{id}/documents` (multipart `file`, `kind`); `GET documents/{id}/content`
- `GET|POST contracts/{id}/obligations`, `PUT|DELETE obligations/{id}`, `POST obligations/{id}/complete`
- `POST contracts/{id}/links {toContractId, kind}`, `DELETE links/{id}`
- `POST intake/extract` (multipart `file`) -> `{ uploadId, fileName, size, pages, suggestions:{field:string;value:string;confidence:number}[], summary, deviations:Deviation[] }`
- `GET templates`; `POST intake/from-template {templateId, fields}` -> `{ uploadId, fileName }`
- `GET approvals/mine` -> `{ instance:ApprovalInstance; stepId:Id; waitingDays:number; contract:ContractDetail }[]`
- `POST approvals/steps/{stepId}/decision {decision:'Approve'|'Reject', comment}`; `POST approvals/steps/{stepId}/delegate {userId}`
- `GET approvals/preview?entityId=&typeId=&departmentId=&value=` -> `{ stages:{order:number; approvers:string[]}[] }`
- `GET renewals?months=12` -> `{ id, title, entityId, entityName, departmentName, noticeDeadline, daysToNotice, autoRenew, annualValue, currency }[]`
- `GET overview` (entity/group management + admin) -> `{ activeContracts, annualValueDkk, dueIn90, inApproval, missingData, valueByEntity:{name,valueDkk}[], countByType:{name,count}[], changeOfControl:{id,title,entityName}[], expiringNoRenewal:ContractListItem[] }`
- Admin (role Admin): CRUD `admin/approval-rules`, `admin/retention-rules`, `admin/departments`, `admin/contract-types`,
  `admin/templates`, `GET|PUT admin/reminder-settings`, `GET admin/audit?contractId=&page=`, `GET admin/outbox`,
  `POST admin/jobs/run`, `GET|PUT admin/holiday-cover`
- Dev mode only: `GET dev/users`, `POST dev/login {userId}`; always: `POST auth/logout`

## 6. Frontend

- React 19, TypeScript strict, Vite, react-router, @tanstack/react-query. NO UI kit: build a small
  component set (Button, Card, Pill, Segmented, Avatar, Field, Select, Table, Modal/Sheet, Toast,
  EmptyState, Skeleton) with plain CSS + CSS variables. vitest + @testing-library/react.
- API layer: `src/api/types.ts` (the types above), `src/api/client.ts` (fetch, credentials
  include, problem+json errors), and a complete in-browser MOCK implementation selected by
  `VITE_API_MODE=mock` (default in `npm run dev` when no backend) with realistic seeded data, so
  the whole UI is demoable and testable without the backend. Mock and real share one interface.
- Visual design is APPROVED and must be followed closely: see `docs/design/*.dc.html` (inline
  styles are the spec; they are design-tool files, do not copy their runtime/markup model).
  Screens: Home (My work), Contracts (register + quick look), Contract record (tabs), New
  contract (4-step focused flow), Approvals, Renewals timeline, Overview (management; restyle
  `v1-Overview` in the v2 look), Settings (admin; restyle `v1-Settings` in the v2 look: legal
  entities, departments, contract types, approval rules, reminders, retention, templates, audit
  log, outbox, holiday cover). Phone layouts: `Phone*.dc.html` (bottom tab bar under ~720px).
- Tokens: primary `#00456c`; page `#f4f6f9`; surface `#ffffff`; text `#1d2733`; secondary text
  `#5b6672`; hairline `#eef1f5`; control border `#d5dde6`; tint `#e6f0f8`; segmented track
  `#e9eef4`. Status pairs (bg/fg): red `#fbe9e7/#9b2c1f`, amber `#fdf1d6/#7a5300`, green
  `#e3f4ea/#1e6b3a`, blue `#e6f0f8/#00456c`, gray `#eef1f5/#3f4a56`. Card shadow
  `0 0 0 1px rgba(16,24,40,.05), 0 1px 2px rgba(16,24,40,.04), 0 8px 24px rgba(16,24,40,.05)`.
  Radii: cards 18–20, controls 10–12, pills 999. Font stack `'Avenir Next','Inter',-apple-system,
  'Helvetica Neue',Arial,sans-serif` (self-host Inter via @fontsource/inter). Type scale (dense):
  h1 30/700, h2 19/700, card title 15/700, body 14, secondary 13, caption 12 (never below 12).
  Light sidebar 224px with "Work" / "Manage" groups and the user card at the bottom.
- Motion: 160ms ease on hover/press, tiles lift 2px, sections rise in on load; all under
  `prefers-reduced-motion: no-preference`.
- Quality bar: keyboard accessible, visible focus rings, real buttons/links/labels, 4.5:1 text
  contrast, loading skeletons, empty states, error toasts, responsive down to 360px. Sentence
  case, no emoji, US English, "DAFA" always in capitals.
- Dev mode: user switcher in the sidebar user card (from `dev/users`) to test the access model.

## 7. Definition of done for any agent

Code complete for the assigned scope, self-reviewed, frontend builds/lints/tests green,
backend carefully desk-checked, a short section appended to `docs/STATUS.md` listing: what was
built, what is stubbed, known gaps, and any decision recorded in `docs/DECISIONS.md`.
