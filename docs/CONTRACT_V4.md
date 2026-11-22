# DAFA Contracts — API contract, release 4

Status: **frontend proposal, written before implementation**, for the backend engineer working in
parallel. The TypeScript types in `src/web/src/api/types.ts` (section "Release 4") are the
normative JSON shapes; the `Api` interface (`api.ts`) and the fetch client (`client.ts`) show the
exact routes and parameter names the frontend sends. This file adds rules. Mismatches go to
`docs/STATUS.md`, deviations to `docs/DECISIONS.md`.

Everything in `CONTRACT_V3.md` stays valid (conventions, access model, paging, problem types,
audit). Release 4 only **adds** routes, parameters and fields, with the exceptions marked
**(change)**.

New problem types: `urn:dafa:problem:not-permitted` (403 with a reason the UI can show, e.g. no
signing right for that entity), `urn:dafa:problem:signature-state` (409, the signature request is
not in a state that allows the action), `urn:dafa:problem:link-failed` (200 body of a link test is
used instead; this type is only for 502 when the external call itself crashed).

---

## 1. Global entity scope (owner bug A3)

The top-bar entity scope is one model for every page: the URL parameter `entity` (repeatable) in
the browser, sent to the API as `entityId` (repeatable, like the release 3 contract filter). Empty
or absent = all entities the user can see. Unknown well-formed ids match nothing; malformed → 400
`entityId`. **Every route below must accept `entityId` (repeatable) and apply it after the access
filter:**

| Route | Today | Release 4 |
|---|---|---|
| `GET contracts`, `contracts/export`, `overview/analytics` | `entityId` repeatable | unchanged |
| `GET home` | no parameters | `?entityId=` scopes tasks, KPIs, month chart, renewals this week, "Watching" |
| `GET renewals?months=` | no entity | `&entityId=` |
| `GET approvals/mine` | no entity | `?entityId=` (the contract's entity); approval step counts in the nav badge stay unscoped |
| `GET search/full` | `entityId` single | repeatable |
| `GET search?q=` (palette) | none | `&entityId=` for contract and document hits (people/counterparties unaffected) |
| `GET overview` (legacy) | none | `?entityId=` |

The user's persisted default scope stays `UserPreferences.defaultEntityId` (release 3); the
frontend applies it once per browser session. No new backend field.

Empty values: for **every** filter parameter in this file and `ContractFilter`, an empty string is
"no filter" (already true for ids/dates/numbers/booleans). Please also treat an empty or null
custom-field value (`cf.<key>=`, `"cf": {"key": null}` / `""` in JSON) as "no filter" — today it
filters on `""` and matches nothing (see STATUS, fix section).

## 2. Contract filter additions (register header filters, B)

Added to `ContractFilter` (query string of `GET contracts`, `contracts/export`,
`overview/analytics`, the JSON `filter` of the data room and the `query` of saved views):

| Parameter | Type | Meaning |
|---|---|---|
| `title` | string | title contains (case-insensitive) |
| `contractNo` | string | contract number contains |
| `counterpartyName` | string | counterparty name contains (master name or the free-text name on older/imported contracts) |
| `delegateId`, `responsibleId` | Id, repeatable | any of |
| `currency` | string, repeatable | contract currency any of |
| `annualValueMin`, `annualValueMax` | number | **annual value in DKK** (`annualValueDkk`), inclusive |
| `totalValueMin`, `totalValueMax` | number | **total value in DKK**, inclusive |
| `noticeFrom`, `noticeTo`, `endFrom`, `endTo`, `startFrom`, `startTo`, `signedFrom`, `signedTo`, `createdFrom`, `createdTo` | `yyyy-MM-dd` | inclusive range per date column; several columns can be combined (AND), and combined with the release 3 `dateField/from/to` |
| `cf.<key>.min`, `cf.<key>.max` | number or date | already supported by the backend engine; now part of the contract for Number, Money (amount) and Date fields |
| `cf.<key>` repeated | string | for Choice/MultiChoice/Person: any of (MultiChoice: contract has any of the values) **(change: repeatable)** |

Malformed number/date → 400 on that parameter, as in release 3. Min > max is not an error (matches
nothing).

### `GET contracts/distinct?column=<key>&q=&limit=&<ContractFilter>` — any user (scoped)
Values for a column filter's list. `column` is a register column key: `counterparty`, `type`,
`entity`, `department`, `owner`, `delegate`, `responsible`, `status`, `currency`, `tags`, or
`cf.<key>` (Choice, MultiChoice, Person, Text). `q` filters values (contains); `limit` default 50,
max 200. The filter is applied **except the column's own filter** (so the list does not collapse
to the selected values). Response `DistinctValues`:
`{ column, values: [{ value, label, count }], truncated }` sorted by `count` desc then `label`.
`value` is what goes into the filter parameter (an id, a status key, a currency code, a text);
`count` = visible contracts with that value under the filter. Unknown column → 400 `column`.

Column → parameter used by the frontend: counterparty → `counterpartyId` (value = supplier id;
contracts without a master record are listed with `value` `""` excluded — use `counterpartyName`
for those), type → `typeId`, entity → `entityId`, department → `departmentId`, owner →
`ownerId`, delegate → `delegateId`, responsible → `responsibleId`, status → `status`, currency →
`currency`, tags → `tagId`, `cf.<key>` → `cf.<key>`.

## 3. Person and counterparty pickers (C)

### `GET people?q=&limit=&role=&includeInactive=` — any user
Type-ahead over app users. `q` matches name, e-mail and initials (contains, case-insensitive);
`limit` default 20, max 50; `role` optional (`ContractOwner`, `Approver`, `Admin`) restricts to
users with that role (Admin counts for every role); inactive users are excluded unless
`includeInactive=true`. Response `PersonOption[]` sorted by name:
`{ id, name, initials, email, title, departmentName, entityName, isActive }`.
`departmentName` = first department (or `null`), `entityName` = the user's home legal entity
(new field below, or `null`).

`AdminUser`, `AdminUserInput` and `LookupUser` get `entityId: Id | null` (home legal entity; an
active entity or null; 400 `entityId` otherwise).

### `POST counterparties` — any user allowed by the setting **(new, non-admin)**
Body `CounterpartyCreateInput` `{ name, country, vatNo, kind, city, email, phone }`
(`kind` default `Supplier`). Allowed per `AppSettings.counterpartyCreation`:
`AdminOnly` (default) / `ContractOwners` (ContractOwner or Admin) / `AllUsers`; otherwise 403
`not-permitted`. Duplicate name (case-insensitive, trimmed) or same `vatNo` among active records →
409 `duplicate` with extension `existingId`. Returns `Counterparty` (`source: 'Manual'`). Audited
`counterparty.created`. `AppConfig.canCreateCounterparty` tells the caller.

### Contracts **(change)**
`POST contracts` requires `supplierId` (an active counterparty; 400 `supplierId` "Choose a
counterparty from the list"); `counterpartyName` is then ignored and taken from the master record.
`PUT contracts/{id}` may keep an existing `supplierId: null` (old/imported rows) but cannot set a
non-null one back to null (400). Imports are unchanged (they may still create by name).
Owner/delegate/responsible ids must be **active** users (400 on the field) — already validated for
existence; inactivity is new.

## 4. Number and date formats (D)

No API change. `AppSettings.numberFormat`/`dateFormat` (release 3) are now applied by the
frontend everywhere; numbers are still sent as JSON numbers. **(change)** default for new
installations: `numberFormat: 'da-DK'`, `dateFormat: 'dd-MM-yyyy'` — existing stored settings are
kept.

## 5. Template categories (E)

- `TemplateCategory` `{ id, name, description, order, isActive, templateCount }`.
- `GET admin/template-categories` → all; `POST`, `PUT admin/template-categories/{id}`
  (`TemplateCategoryInput` `{ name, description, isActive }`), `DELETE` (204; 409 `in-use` when it
  has templates), `PUT admin/template-categories/order` `{ ids: Id[] }` (all ids, 400 otherwise).
  Name 1–80 unique (409 `duplicate`).
- `Template` gains `categoryId: Id | null`, `categoryName: string | null`,
  `typeIds: Id[]` (empty = all types; `typeId` stays as the first of them for older clients),
  `language: 'da' | 'en' | 'sv' | 'de' | 'pl' | 'it' | 'zh' | null`, `isActive`, `updatedAt`.
- `POST admin/templates` (multipart) accepts `categoryId`, `typeIds` (repeated form field),
  `language`; `PUT admin/templates/{id}` body `TemplateInput` gains the same fields.
- `GET templates` (any user) returns **active** templates in active categories (and templates
  without a category), sorted by category `order`, then name; `GET template-categories` (any
  user) returns active categories with `templateCount` of active templates.
- `GET templates/{id}/preview` (any user) → `TemplatePreviewInfo`
  `{ id, name, description, placeholders: [{ name, label, sample }], excerpt }` where `excerpt` is
  the first ~1 000 characters of the template text with `{{placeholders}}` kept.

## 6. Logo (F)

- `POST admin/branding/logo?variant=full|compact` (multipart `file`) — PNG, SVG, JPEG or WebP,
  max 1 MB (400 `file`); SVG is stored as is but **always served with**
  `Content-Type: image/svg+xml`, `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'`
  and `X-Content-Type-Options: nosniff` (the frontend only uses it in `<img>`). Returns `Branding`.
- `DELETE admin/branding/logo?variant=full|compact` → `Branding` (back to the default mark).
- `GET branding/logo?variant=full|compact&v=<version>` — **anonymous** (sign-in screen), the file,
  `Cache-Control: public, max-age=31536000, immutable` (the `v` changes on upload); 404 when none.
- `Branding` `{ logoUrl: string | null, compactLogoUrl: string | null, version: string }`, URLs
  relative to the API base, e.g. `branding/logo?variant=full&v=3`.
- `AppConfig.branding` and `DevConfig.branding` (anonymous `GET dev/config`) carry `Branding`;
  in Entra mode the sign-in redirect page uses `GET branding` (anonymous) → `Branding`.

### 6.1 Dark-theme logo (item I)
`variant` also accepts `dark` (an optional logo for the dark theme) on `POST|DELETE admin/branding/logo`
and `GET branding/logo`. `Branding` gains `darkLogoUrl: string | null`. Without a dark logo the
frontend shows the normal one on dark backgrounds too.

## 6a. Theme (item I)
`UserPreferences` gains `theme: 'light' | 'dark' | 'system'` (default **`light`** for existing and
new users, so nothing changes unexpectedly). `PUT me/preferences` accepts it (any other value →
400 `theme`); `GET me/preferences` returns it. The frontend also keeps a copy in localStorage to
paint the right theme before the first request (incl. the sign-in screen).

## 7. Legal entity / SharePoint / Entra link (G)

- `LegalEntity` gains `sharePointSiteUrl: string | null`, `sharePointSiteId: string | null`
  (read-only, resolved by the test), `managementGroupId: string | null`,
  `managementGroupName: string | null`, `link: LinkStatus`.
- `LinkStatus` `{ state: 'NotLinked' | 'Linked' | 'Error', checkedAt: string | null, message: string | null }`.
- `LegalEntityInput` gains `sharePointSiteUrl`, `managementGroupId` (Entra object id, GUID or
  null). URL must be `https://<tenant>.sharepoint.com/sites/<name>` (400 `sharePointSiteUrl`).
  Changing either resets `link` to `NotLinked`.
- Departments (`admin/departments`): `Department` gains `documentLibrary: string | null`,
  `groupId: string | null`, `groupName: string | null`, `link: LinkStatus`; the input gains
  `documentLibrary`, `groupId`.
- `POST admin/entities/{id}/test-link` and `POST admin/departments/{id}/test-link` → `LinkTestResult`
  `{ link: LinkStatus, siteId: string | null, groupName: string | null, details: string[] }`. In
  SharePoint/Entra mode: resolves the site via Graph (`sites/{host}:/sites/{name}`), the library
  by name, the group by id; stores `sharePointSiteId`/`groupName` and the status. In Local
  storage / Dev auth mode: validates the formats only and returns `Linked` with
  `details: ["Test mode: saved, used in SharePoint mode"]`.
- `GET admin/access-mapping` → `AccessMapping` `{ mode: 'Local' | 'SharePoint', entities, departments, cells, warnings }`:
  `cells` = one row per entity × department pair that has contracts or is configured:
  `{ entityId, departmentId, siteUrl, library, entityGroupName, departmentGroupName, contractCount, warnings: string[] }`;
  `warnings` (also top level) e.g. "DAFA Polska: no SharePoint site", "Purchasing: no document
  library", "12 contracts in DAFA US / Sales have no site".
- `AppConfig.storageMode: 'Local' | 'SharePoint'` so the UI can label test mode.

## 8. E-signature (H)

### 8.1 Permission
- `AdminUser`/`AdminUserInput` gain `canSendForSignature: boolean` and
  `signatureEntityIds: Id[]` (empty = all entities the user may see). `Me` gains the same two
  fields. Admins do **not** get it implicitly.
- A user may send a contract for signature when: `canSendForSignature`, the contract's entity is
  in `signatureEntityIds` (or the list is empty), they can see the contract, and the contract's
  status is `Approved` (or `Draft`/`Active` when `ESignSettings.requireApproved` is false).
  Otherwise 403 `not-permitted` with a `detail` sentence.
- `GET contracts/{id}/signature-permissions` → `SignaturePermissions`
  `{ canSend: boolean, reason: string | null, permittedUsers: PersonOption[] }` (users who could
  send this contract; for "Ask a colleague with signing rights").

### 8.2 Settings (Admin)
`GET|PUT admin/esign-settings` → `ESignSettings`:
`{ provider: 'None' | 'Simulated' | 'DocuSign' | 'Penneo', defaultMessage, defaultOrder: 'Sequential' | 'Parallel', reminderDays: number[] (0–5 values, 1–60), expiryDays (1–180, default 30), requireApproved: boolean, authorisedSignatories: [{ entityId, userIds: Id[], externalNames: string[] }], restrictToAuthorised: boolean, providerAccount: string | null }`.
`provider: None` → every send route 400 `feature-off`. Secrets (API keys) are server
configuration, not in this payload. `AppConfig.eSign` `{ provider, enabled }`
(`enabled` = provider ≠ None). `AppSettings.features.eSign` is kept and mirrors `enabled`.

### 8.3 Requests
- `SignatureRequest` `{ id, contractId, status: 'Draft' | 'Sent' | 'Completed' | 'Declined' | 'Expired' | 'Cancelled', provider, order, message, documentIds, createdBy, createdAt, sentAt, completedAt, expiresAt, signers: Signer[], signedDocumentId: Id | null }`.
- `Signer` `{ id, kind: 'Internal' | 'External', userId: Id | null, name, email, order: number, status: 'Waiting' | 'Sent' | 'Viewed' | 'Signed' | 'Declined' | 'Expired', at: string | null, declineReason: string | null }`.
- `GET contracts/{id}/signature-requests` → `SignatureRequest[]` newest first (can see).
- `POST contracts/{id}/signature-requests` (`SignatureRequestInput`
  `{ documentIds: Id[] (≥1, documents of this contract), signers: [{ kind, userId?, name?, email?, order }], order, message }`)
  → `SignatureRequest` with status `Sent` (first signer(s) `Sent`). Internal signers must be
  active users; external need name and a valid e-mail; ≥1 signer. With `restrictToAuthorised`
  the DAFA side must include ≥1 authorised signatory of the contract's entity (400 `signers`).
  Only one open (`Sent`) request per contract: 409 `signature-state`.
- `POST signature-requests/{id}/resend` (sender, permitted users, Admin) → request; reminders
  the signers whose status is `Sent`/`Viewed`.
- `POST signature-requests/{id}/cancel` → request with `Cancelled`; 409 when not `Sent`.
- `POST signature-requests/{id}/simulate` `{ signerId, action: 'View' | 'Sign' | 'Decline', reason? }`
  — **only when provider is `Simulated`** (else 400 `feature-off`); any permitted sender or Admin.
  Sequential order: only the current signer may act (409 `signature-state`).
- Provider webhooks (`POST esign/webhooks/{provider}`) — backend's own design; not used by the UI.
- **Completion:** when every signer has signed, the backend stores the signed PDF as a new
  document (`kind: 'Main'`, name `<original> – signed.pdf`; in Simulated mode a generated PDF/copy
  with a signature page), sets `signedDocumentId`, sets `signedDate` = completion date, and moves
  the contract to `Active` (as `POST contracts/{id}/sign` does today, with method `E-signature`).
  Declined by anyone → request `Declined`, contract status unchanged.
- `ContractDetail` gains `signature: SignatureRequest | null` (latest open or last finished) and
  `canSendForSignature: boolean`.
- `POST contracts/{id}/sign` (manual "Record manual signature") **(change)**: allowed for the
  owner, delegate and Admin only (403 otherwise); unchanged otherwise.
- Notifications: new events `SignatureRequested` (to internal signers), `SignatureCompleted` and
  `SignatureDeclined` (to owner, delegate, sender); placeholders as approvals plus `signer.name`.
- Audit: `signature.sent`, `signature.viewed`, `signature.signed`, `signature.declined`,
  `signature.cancelled`, `signature.completed`, `settings.esign`, `settings.branding`,
  `settings.template-categories`, `entity.link-tested`, `department.link-tested`.

## 9a. Storage ("the app is the only door")

SharePoint is reached with **app-only** access; users have no direct library access. Every file
goes through the API.

### Problem types (every file route: document upload/download, template upload/content,
`intake/extract`, `intake/from-template`, data-room create/content, signature send/complete,
logo upload)

| Type | Status | When | UI text |
|---|---|---|---|
| `urn:dafa:problem:storage-unavailable` | 503 | Graph throttling/timeouts/5xx after the backend's own retries. `Retry-After` header (seconds) when known. | "SharePoint is busy. Try again in a moment." + Retry button (never a generic error) |
| `urn:dafa:problem:file-missing` | 404 | The document row exists but the file is gone from storage. The backend notifies administrators (outbox, event `StorageProblem`). | "This file can no longer be found in storage. An administrator has been notified." |
| `urn:dafa:problem:storage-not-configured` | 409 | The contract's entity/department has no site/library mapping (or storage mode is SharePoint but credentials are missing). `detail` is a sentence for the user. | The server `detail`; admins also get a link to Settings > Access mapping |

`ContractDetail.storeUrl` **(change)**: only filled for Admins (the frontend shows "Open in
SharePoint" to Admins only; everyone else opens files through the app).

### `GET admin/storage/health` — Admin → `StorageHealth`
`{ mode: 'Local' | 'SharePoint', state: 'Healthy' | 'Degraded' | 'Down' | 'NotConfigured', lastSuccessAt: string | null, lastFailureAt: string | null, lastError: string | null, checkedAt: string, recentFailures: number (last 24 h), details: string[] }`.
`checkedAt` = now; the backend does a cheap call (site/drive root) with a short timeout.

### `GET admin/storage/reconciliation` — Admin → `ReconciliationReport | null` (latest, null when never run)
### `POST admin/storage/reconciliation/run` — Admin → `ReconciliationReport` (synchronous, ≤ 60 s; long runs may return `status: 'Running'` and be polled with the GET)
`ReconciliationReport` `{ id, startedAt, finishedAt: string | null, status: 'Running' | 'Completed' | 'Failed', error: string | null, checkedDocuments: number, checkedFolders: number, items: ReconciliationItem[] }`;
`ReconciliationItem` `{ kind: 'MissingFile' | 'Orphan' | 'Mismatch' | 'MisplacedFolder', contractId: Id | null, contractNo: string | null, documentId: Id | null, path: string, detail: string }`:
- `MissingFile`: document row without a file;
- `Orphan`: file in a library with no document row;
- `Mismatch`: size/hash/name differs between row and file;
- `MisplacedFolder`: the contract's folder is not under its entity's site/department library.
Audit `storage.reconciled`.

## 9. Settings robustness (A4)

No API change required. The frontend reloads once after a failed lazy chunk, waits for `me`, and
shows errors with retry. **Deployment request (DevOps/backend):** keep the previous release's
hashed `assets/*` files for at least one release (or serve `index.html` with
`Cache-Control: no-cache`), so a page loaded before a deploy can still fetch its chunks.

## 10. Endpoint and role summary (release 4)

| Route | Method | Who |
|---|---|---|
| `people` | GET | any |
| `counterparties` | POST | per `counterpartyCreation` |
| `contracts/distinct` | GET | any (scoped) |
| `home`, `renewals`, `approvals/mine`, `search`, `overview` + `entityId` | GET | as before |
| `template-categories`, `templates/{id}/preview` | GET | any |
| `admin/template-categories[/{id}]`, `admin/template-categories/order` | * | Admin |
| `branding`, `branding/logo` | GET | anonymous |
| `admin/branding/logo` | POST, DELETE | Admin |
| `admin/entities/{id}/test-link`, `admin/departments/{id}/test-link`, `admin/access-mapping` | POST/GET | Admin |
| `admin/esign-settings` | GET, PUT | Admin |
| `admin/storage/health`, `admin/storage/reconciliation` | GET | Admin |
| `admin/storage/reconciliation/run` | POST | Admin |
| `contracts/{id}/signature-permissions`, `contracts/{id}/signature-requests` | GET | can see |
| `contracts/{id}/signature-requests` | POST | signing permission (§8.1) |
| `signature-requests/{id}/resend`, `/cancel`, `/simulate` | POST | sender, permitted users, Admin |

## 11. Mock

`src/web/src/api/mock` implements every route above with the same rules; Simulated is the only
provider that "sends" anything. The mock builds the signed document as a copy of the selected
document with " – signed" in its name.
