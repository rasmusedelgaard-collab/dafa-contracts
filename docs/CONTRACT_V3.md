# DAFA Contracts — API contract, release 3

Status: **agreed frontend proposal, written before implementation.** The frontend codes against
exactly this file and `src/web/src/api/types.ts` (section "Release 3"). The TypeScript types are
the normative JSON shapes; this file adds routes, parameters, rules and computations. The backend
lists any mismatch in `docs/STATUS.md` and records a deviation in `docs/DECISIONS.md`.

Conventions from release 1–2 still apply: JSON camelCase, dates `yyyy-MM-dd`, timestamps ISO 8601
UTC, enums as strings (numbers are 400), errors RFC 7807, cookie session, every non-GET/HEAD request
carries `X-Requested-With: dafa-contracts`, 409 problems carry a `type`.

## 0. Ground rules

1. **Access model.** Every endpoint that returns contract data — lists, counts, sums, charts,
   search hits, document hits, comments, versions, person and counterparty pages, exports, data
   room — goes through the existing access filter (`IContractAccessService`): a user sees a contract
   if they are in its department, manage its entity, are group management, are owner/delegate/
   responsible, are Admin, or are the current approver of an open step. Aggregates are computed
   **only** over visible contracts. A contract that exists but is not visible is 404 on by-id
   endpoints (as today: 403 is also accepted by the frontend).
2. **Backward compatibility.** No existing route, parameter or field changes meaning, with two
   intentional exceptions: (a) `GET overview` is now allowed for every signed-in user (scoped),
   no more 403; (b) `GET contracts` accepts repeated filter parameters and the new ones in §2.
   New fields on existing payloads are additions.
3. **Roles.** "Any user" = any signed-in, active app user. "Admin" = role `Admin`. Admin
   endpoints are under `/api/admin/*` and are 403 for everybody else.
4. **Paging.** `page` is 1-based (default 1), `pageSize` default 25, max 200 (larger values are
   clamped, not rejected). Responses are `Paged<T>` = `{ items, total, page, pageSize }`. A page
   past the end returns an empty `items` with the real `total`.
5. **Validation errors** are 400 with `errors` keyed by camelCase field path:
   `customFields.<key>`, `mapping.<fieldKey>`, `events.<Event>.subject`, `rows`, `tagIds`,
   `mentionIds`, etc. `detail` is a sentence that can be shown as is.
6. **New 409 problem types** (in addition to release 2's `stale`, `no-approval-rule`,
   `no-approver`, `locked-fields`, `duplicate`, `invalid-state`):
   - `urn:dafa:problem:in-use` — cannot delete/change because something uses it (custom field
     with values, currency used by contracts, entity with contracts, counterparty with contracts).
   - `urn:dafa:problem:synced-field` — the field comes from Entra ID or a sync source.
   - `urn:dafa:problem:import-state` — import session expired, unknown, or already run.
   - 400 with `type` `urn:dafa:problem:feature-off` — the feature is turned off in General settings.
7. **Money.** All `*Dkk` values are converted with the admin-editable currency rates (§9.4),
   rounded to whole DKK. `annualValueDkk` = annualValue in DKK (null → 0); `totalValueDkk` =
   totalValue in DKK (null → 0). "Contract value" for bands and thresholds = max(total, annual) in
   DKK, as for approvals.
8. **Audit.** Every write in this release writes an `AuditEvent` (action names in §14).

## 1. Additions to existing payloads

### 1.1 `ContractListItem` (every list: register, overview drill-down, person, counterparty, search)
Adds the fields of `ContractListItemV3`: `entityId, departmentId, typeId, ownerId, delegateName,
responsibleName, counterpartyId (= supplierId), startDate, endDate, totalValue, annualValueDkk,
totalValueDkk, autoRenew, changeOfControl (bool|null), exclusivity, createdAt (date), signedDate,
hasSignedDocument, tags: TagRef[], watching (caller watches it), customFields`.

`hasSignedDocument` = the contract has at least one document of kind `Main`.

### 1.2 `ContractDetail`
Adds `watching, watcherCount, commentCount, tags, customFields, canTag (= canEdit)` plus
`annualValueDkk, totalValueDkk, delegateName, responsibleName, counterpartyId, createdAt,
hasSignedDocument`.

### 1.3 `POST contracts`, `PUT contracts/{id}` body
Adds optional `customFields: Record<key, CustomFieldValue>` and `tagIds: Id[]`. On update an
omitted property means "unchanged"; `customFields` given = the full set of values (keys not given
are cleared). Custom-field values are not part of `lockedFields` (they stay editable after
approval) unless a later decision says otherwise.

Validation (400): unknown key, inactive field, field not valid for the contract's type, wrong value
type (see `CustomFieldValue`: Text/Choice/Date/Person = string, Date = `yyyy-MM-dd`, Person = an
existing user id, Number = number, Money = `{amount:number, currency: active code}`, YesNo =
boolean, MultiChoice = string[] of defined options, Choice = one defined option).
**Required** custom fields and the per-type required standard fields (§8.2) are enforced on
`POST contracts`, on `PUT` while the contract is `Draft`, and on `submit`; not on later edits and
not on import (import turns them into warnings).

### 1.4 `Lookups`
`entities[]` gain `country, currency, isActive`; `departments[]` and `contractTypes[]` gain
`isActive`; `users[]` gain `isActive, email, title`. Inactive rows are **included** (so names still
resolve); pickers hide them.

### 1.5 `GET search?q=` (Ctrl+K palette)
May now return `kind: 'Document'` (`id` = document id, `contractId` set, `title` = file name,
`subtitle` = contract title) and `kind: 'Person'` (`id` = user id). Max 8 results: up to 4
contracts, 2 documents, 2 people, then suppliers. Same matching as §3.

### 1.6 `GET overview`
Unchanged shape; now any user, computed over visible contracts.

### 1.7 `missingData`
Built from the data-quality fields in General settings (§12). Labels:
`noticePeriodDays` "Notice period", `contractValue` "Contract value", `signedDate` "Signed date"
(Active/NoticeGiven only), `changeOfControl` "Change of control", `signedDocument` "Signed
document" (Active/NoticeGiven only), `endDate` "End date" (open-ended contracts count as missing),
`responsibleId` "Responsible", `paymentTerms` "Payment terms", `governingLaw` "Governing law",
`cf.<key>` = the custom field's label (only for contracts whose type the field applies to). The
backend's always-on checks from release 2 ("Title", "Counterparty", "Currency", "Renewal period")
stay. Default list: `noticePeriodDays, contractValue, signedDate, changeOfControl` (= release 2).

## 2. Shared contract filter

Used by `GET contracts`, `GET contracts/export`, `GET overview/analytics`, and as the `filter`
object of `POST data-room/exports`. Type `ContractFilter`.

| Parameter | Type | Meaning |
|---|---|---|
| `q` | string | Substring (case-insensitive) in title, counterparty, contract no., type, entity, department, owner (as today) |
| `id` | Id, repeatable | Only these contracts |
| `entityId`, `departmentId`, `typeId` | Id, repeatable | Any of |
| `status` | ContractStatus, repeatable | Any of |
| `counterpartyId` | Id, repeatable | `supplierId` any of |
| `ownerId` | Id, repeatable | Owner any of |
| `personId` | Id | Owner, delegate or responsible |
| `valueBand` | `lt1m` \| `1to5m` \| `5to20m` \| `gte20m`, repeatable | max(total, annual) in DKK; min inclusive, max exclusive (0–1m, 1–5m, 5–20m, ≥20m) |
| `minValueDkk` | number | max(total, annual) in DKK ≥ value |
| `dateField` + `from` + `to` | `notice` \| `end` \| `start` \| `created` \| `signed`; ISO dates, inclusive, either bound optional | Contracts whose date is in the range; contracts without that date never match. Default `dateField` = `end` when `from`/`to` are given |
| `noticeWithinDays` | int | Status Active, no renewal decision, 0 ≤ daysToNotice ≤ N |
| `decided` | bool | Renewal decision recorded (true) / not (false) |
| `tagId` | Id, repeatable | Has any of the tags |
| `changeOfControl` | bool | `changeOfControl === value` (null never matches) |
| `exclusivity` | bool | true: exclusivity is not empty and not "None" (case-insensitive); false: the opposite |
| `autoRenew` | bool | |
| `missingData` | bool | `missingData` non-empty (true) / empty (false) |
| `missingField` | data-quality key | Contracts missing that one field (same rule as `missingData`) |
| `missingSignedDocument` | bool | true: status Active or NoticeGiven and `hasSignedDocument` false |
| `watching` | bool | The caller watches it |
| `includeEnded` | bool | Include Expired and Terminated. Default **true** for `contracts`/export (unchanged), **false** for `overview/analytics`. An explicit `status` overrides it |
| `cf.<key>` | string | Custom field equals value (Text: case-insensitive contains; Choice: equals; MultiChoice: contains the option; YesNo: "true"/"false"; Number/Money: equals the number; Date: equals; Person: user id) |

Repeated parameter = OR within the parameter; different parameters = AND. Unknown ids match
nothing (no 400). The existing `view` parameter keeps working and combines with AND.

`sort` adds: `contractNo, counterparty, type, department, start, end, totalValue, created`
(prefix `-` for descending); unknown sort = 400.

`GET contracts/export` adds optional repeatable `columns` = register column keys
(`REGISTER_COLUMNS` in types.ts or `cf.<key>`). Without `columns` the release 2 CSV columns are
used. CSV, UTF-8 with BOM, comma separated, formula guard as today.

## 3. Search

### `GET search/full` — any user
Query: `q` (required, ≥ 2 characters after trim, else empty groups), `group` (`contracts` |
`documents` | `people` | `counterparties`; omitted = all), `personId`, `entityId`, `departmentId`,
`typeId`, `documentKind`, `uploadedFrom`, `uploadedTo`, `page`, `pageSize`.
Response `FullSearchResult` = one `SearchGroupResult<T>` = `{ total, items }` per group.
Without `group`, each group returns its first `pageSize` (default 10, max 50) hits; with `group`,
paging applies to that group only and the other groups are returned empty with `total` still set.

Matching: `q` is split on whitespace into terms; a hit must contain **every** term
(case-insensitive, diacritics-sensitive) somewhere in its searchable text.

- **Contracts** (visible only): title, contract no., counterparty, owner/delegate/responsible
  names, summary, obligation descriptions, tag names, Text/Choice custom-field values.
  `matchedIn` lists the fields that contain a term. `snippet` is set when the match is in
  summary or an obligation (otherwise null). Filters: `personId` (any role), `entityId`,
  `departmentId`, `typeId`. Order: title matches first, then contract no., then others; then title.
- **Documents** (documents of visible contracts, latest version of each name unless an older
  version matches on its own): file name and extracted document text. Text extraction uses the
  existing analyzer text extractor at upload time (stored, max 400k characters); documents whose
  text cannot be read match on name only. `snippet` (≤ 200 characters around the first match, on
  word boundaries, with `…` when cut) for text matches; `highlights` are `[start, end)` offsets of
  every term occurrence inside `snippet.text`. Filters: `personId` (= uploaded by, or a person on
  the contract), `entityId`, `departmentId`, `typeId`, `documentKind`, `uploadedFrom/To`.
  Order: name matches first, then newest upload.
- **People**: active users whose name, e-mail or title matches. `contractCount` = visible
  contracts where the person has a role. Not filtered by `personId`.
- **Counterparties**: active master records whose name, VAT no. or external id matches.
  `contractCount` = visible contracts.

Every document hit returned is downloadable by the caller through `GET documents/{id}/content`
(same access rule). Opening is audited as today.

## 4. Person and counterparty pages

### `GET people/{userId}` — any user
`PersonPage`. 404 for an unknown user (inactive users are allowed). `contracts` = visible contracts
where the person is owner, delegate or responsible, each with `roles`; sorted nearest notice
deadline first (no deadline last), then title. `counts` count those contracts per role
(`total` = distinct contracts, `active` = status Active). `annualValueDkk` = sum over contracts with
status Active or NoticeGiven.

### `GET counterparties/{id}` — any user
`CounterpartyPage`. The master record itself is visible to every user; `contracts`, `summary`,
`renewals`, `documents` only cover visible contracts with `supplierId = id`. `summary.live` =
Active + NoticeGiven; `nextNoticeDeadline` = earliest future notice deadline of an undecided Active
contract; `renewals` = like `GET renewals` rows for these contracts (no month limit);
`documents` = latest version of each document, newest first, max 200.

### `GET counterparties?q=` — any user
Active master records, name/VAT/external-id contains `q`, max 20, `contractCount` = visible.
Same id space as `suppliers`; `GET suppliers` stays.

## 5. Overview analytics

### `GET overview/analytics?<ContractFilter>&ddThresholdDkk=` — any user
Response `OverviewAnalytics`. The **base set** = visible contracts that match the filter; when
`status` is not given and `includeEnded` is not true, Expired and Terminated are excluded.

| Field | Computation over the base set |
|---|---|
| `scope.visibleContracts` | All visible contracts, no filter, all statuses |
| `scope.label` | "All contracts" for group management/admin; else e.g. "Purchasing", "DAFA Sverige AB", "Your contracts" (free text, shown as is) |
| `kpis.count` | Number of contracts |
| `kpis.annualValueDkk`, `totalValueDkk` | Sums |
| `kpis.notice30`, `notice90` | Status Active, no decision, 0 ≤ daysToNotice ≤ 30 / 90 |
| `kpis.inApproval` | Status InApproval |
| `kpis.missingData` | `missingData` non-empty |
| `kpis.autoRenewCount`, `autoRenewSharePct` | autoRenew true; share of `count` × 100, one decimal |
| `byEntity`, `byDepartment`, `byType` | One `Bucket` per group with ≥ 1 contract; `key` = id; sorted by annualValueDkk desc, then count desc, then label |
| `topCounterparties` | Group by `supplierId` (contracts without one: key `''`, label = counterparty name, grouped by name); top 10 by annualValueDkk desc |
| `top10SharePct` | Σ annual of the top 10 / `kpis.annualValueDkk` × 100 (0 when the total is 0) |
| `noticeByMonth` | Exactly 24 rows, `yyyy-MM`, from the current month. Status Active contracts whose notice deadline is in the month. Segments: `decided` if a decision is recorded, else `high` ≤ 30 days, `medium` 31–90, `low` > 90. Undecided Active contracts whose deadline has passed while `endDate ≥ today` count in the **current** month as `high`. `annualValueDkk` = sum of the month's contracts |
| `expiringByQuarter` | Exactly 8 rows from the current quarter, `yyyy-Qn`, `from`/`to` = quarter's first/last day; contracts whose `endDate` is in the quarter (open-ended never) |
| `statusFunnel` | 7 buckets in order Draft, InApproval, Approved, Active, NoticeGiven, Expired, Terminated (zero rows included); `key` = status, `label` = "Draft", "In approval", "Approved", "Active", "Notice given", "Expired", "Terminated" |
| `dataQuality` | One row per entity with ≥ 1 contract; `complete` = contracts with empty `missingData`; `score` = round(complete / contracts × 100); `missing[fieldKey]` = contracts missing that field (only keys with ≥ 1). Sorted by score asc, then name |
| `dueDiligence.thresholdDkk` | Query `ddThresholdDkk`, else General settings `ddThresholdDkk` (default 5,000,000) |
| `dueDiligence.changeOfControl` | `changeOfControl === true`; `note` null |
| `dueDiligence.exclusivity` | Exclusivity not empty and not "None"; `note` = the exclusivity text |
| `dueDiligence.aboveThreshold` | max(total, annual) in DKK ≥ threshold; `note` null |
| `dueDiligence.missingSignedDocument` | Status Active/NoticeGiven and no Main document; `note` "No main document" |

Due-diligence lists are sorted by totalValueDkk desc, then annualValueDkk desc, then title; no limit.

Drill-down mapping the frontend uses (informative): entity bar → `entityId`, department →
`departmentId`, type → `typeId`, counterparty → `counterpartyId` (or `q` for key `''`), notice
month → `dateField=notice&from&to` (+ segment: `decided=true`, or `noticeWithinDays`), quarter →
`dateField=end&from&to`, status → `status`, data-quality row → `entityId&missingData=true`,
KPI tiles → `noticeWithinDays=30|90`, `status=InApproval`, `missingData=true`, `autoRenew=true`.
The drill-down list is `GET contracts` with the same filter (+ `includeEnded`, `sort`, paging),
the export `GET contracts/export` with the same filter.

## 6. Collaboration

### Tags
- `GET tags` — any user → `TagRef[]` (sorted by name).
- `PUT contracts/{id}/tags { tagIds }` — users with edit rights on the contract (`canEdit`) →
  `ContractDetail`. Replaces the contract's tags. Unknown tag id → 400 `tagIds`. Allowed in every
  status (tags are not approved terms).

### Watch
- `PUT contracts/{id}/watch` (idempotent, 204) and `DELETE contracts/{id}/watch` (204) — any user
  who can see the contract.
- `GET watching` — any user → `ContractListItem[]` of watched contracts that are still visible,
  nearest notice first.
- Watchers receive `WatchedContractChanged` for: status change, renewal decision, details
  updated, document uploaded, comment added (not for their own actions).
- Preference `autoWatch` (§11): the user starts watching contracts they create or are added to as
  owner/delegate/responsible.

### Comments
- `GET contracts/{id}/comments` — anyone who can see the contract → `Comment[]`, oldest first.
- `POST contracts/{id}/comments { body, mentionIds }` — anyone who can see the contract → `Comment`.
- `PUT comments/{id}` — the author → `Comment` (sets `editedAt`).
- `DELETE comments/{id}` — the author or an Admin → 204.
- Rules: `body` trimmed 1–4000 characters (400 `body`). Every `mentionIds` user must exist, be
  active and be able to see the contract; otherwise 400 `mentionIds` with "<Name> cannot see this
  contract". Each mentioned user gets a `Mention` notification (in-app always; mail/Teams per
  settings). `canEdit` = caller is author; `canDelete` = author or Admin.

### In-app notifications
- `GET notifications?unreadOnly=&page=&pageSize=` → `NotificationPage` (`Paged<AppNotification>`
  + `unread`), newest first. Only the caller's own.
- `GET notifications/unread-count` → `{ count }` (polled every 60 s by the frontend).
- `POST notifications/{id}/read` (204; own only, else 404), `POST notifications/read-all` (204).
- An in-app notification is created for every event whose settings (§9.6) include channel `InApp`
  and whose recipient has not turned `inApp` off in preferences. `link` is an app path:
  `/contracts/<id>`, `/contracts/<id>#comments`, `/approvals`, `/settings/imports`.
  Notifications about a contract the user can no longer see are not returned.

### Saved views
- `GET views` → `SavedView[]`: the caller's personal views and all shared views, sorted
  personal first, then by name.
- `POST views`, `PUT views/{id}`, `DELETE views/{id}` (204).
- Rules: `name` 1–80, unique per owner and scope (409 `duplicate`). Anyone may create Personal
  views. Shared views: create needs role ContractOwner or Admin (403); edit/delete: the owner, or
  an Admin. `isDefault` only for Personal (400 otherwise); setting it clears the caller's other
  default. `columns` = register column keys (`REGISTER_COLUMNS` or `cf.<key>`; unknown → 400).
  `query` is stored as given (a `ContractQuery`/`ContractFilter` object, sort as a string);
  applying a view always runs through the access filter, so a shared view shows each user only
  what they may see. `canEdit` tells the caller whether they may change it.

### Bulk actions
`POST contracts/bulk` — any user → `BulkResult`. `contractIds` 1–500 (400 otherwise). Each
contract is checked on its own; a contract the caller cannot see or may not change goes to
`failed` with a reason, the rest succeed (no rollback).

| `action` | Needs | Extra body | Effect |
|---|---|---|---|
| `AddTag` / `RemoveTag` | `canEdit` | `tagId` | Adds/removes the tag |
| `Reassign` | `canEdit` | `role` (Owner/Delegate/Responsible), `toUserId` | Sets that role to the user. Owner: target must be active with ContractOwner or Admin. Not allowed while InApproval (failed: "In approval"). If the target already has another role on the contract, that role stays. |
| `RequestDataCompletion` | can see | `message?` | For contracts with non-empty `missingData`: a `DataCompletionRequested` notification to the owner (and delegate) listing the missing fields; others → failed "Nothing is missing" |

Export of a selection = `GET contracts/export?id=a&id=b…`.

### Version comparison
- `GET contracts/{id}/versions` — anyone who can see → `ContractVersion[]`, newest first. First
  entry `id: 'current'`; then one entry per audit event that changed a key term (`id` = audit
  event id; the state of the key terms **right after** that event; the newest such event is
  skipped because it equals `current`); last entry `id: 'created'` (state at creation, rebuilt by
  undoing the audited old/new values). `changedFields` = fields the event changed.
- `GET contracts/{id}/compare?from=<versionId>&to=<versionId>` → `ContractComparison`. Unknown
  version → 404. Key terms, in this order: title, counterparty, type, legal entity, department,
  owner, start date, end date, notice period, automatic renewal, renewal period, annual value, total
  value, currency, payment terms, price adjustment, next price adjustment, governing law, liability
  cap, exclusivity, change of control, status, then active custom fields. Values are display
  strings (names for ids, ISO dates, plain numbers without separators, "Yes"/"No", "Open-ended"
  for a null end date) or null.

### Data room export
- Who: Admin, group management, or a user with entity management (`managedEntityIds` not empty);
  403 for others. Always scoped by access.
- `POST data-room/exports` body `DataRoomInput` → 202 with `DataRoomExport` (status `Queued` or
  `Running`; small exports may already be `Ready`). `name` 1–80 characters.
- `GET data-room/exports` → the caller's exports (Admin: all), newest first.
- `GET data-room/exports/{id}` → one export (poll until `Ready` or `Failed`).
- `GET data-room/exports/{id}/content` → `application/zip`; 409 `invalid-state` when not Ready.
- Zip layout: `index.csv` (one row per contract: the export columns of all register columns and
  all custom fields, plus `documents` = file names separated by `; `), `manifest.json`
  (`{ name, createdAt, createdBy, filter, contracts, documents: [{ path, contractNo, kind,
  version, size, sha256 }] }`), and `documents/<contractNo> – <title>/<file name>` (with
  `versions: 'All'`: `v<version> – <file name>`). `documents: 'MainOnly'` = only kind Main;
  `'None'` = index and manifest only. Limits: 5000 documents or 2 GB → `Failed` with `error`.
  Ready exports expire after 7 days (`expiresAt`). Audited once (`dataroom.exported`).

## 7. Personal and app configuration

### `GET app-config` — any user → `AppConfig`
What every page needs: app name, date/number formats, feature toggles, session timeout, the
caller's resolved landing page (preference, else the role default from General settings, roles
checked in the order Admin, Approver, ContractOwner, Reader), default entity scope, data-quality
fields, due-diligence threshold, **active custom fields** (sorted by `order`), field rules, tags,
DKK rates of active currencies, and `canExportDataRoom`.

### `GET|PUT me/preferences` — any user → `UserPreferences`
`events` lists every `NotificationEvent` with the caller's switches; on `PUT`, a channel the admin
has not enabled for that event is ignored (stored as false). `defaultEntityId` must be an active
entity or null; `landingPage` one of `LandingPage` or null. `digest` Off/Daily/Weekly.

## 8. Custom fields and field rules (Admin unless noted)

### 8.1 Custom fields
- `GET custom-fields` (any user; also inside `app-config`) → active `CustomFieldDef[]` by `order`.
- `GET admin/custom-fields` → all, including inactive.
- `POST admin/custom-fields`, `PUT admin/custom-fields/{id}`, `DELETE admin/custom-fields/{id}`
  (204), `PUT admin/custom-fields/order { ids }` → all definitions in the new order (`order` =
  index; ids not listed keep their relative order after the listed ones).
- Rules: `key` matches `^[a-z][a-zA-Z0-9]{1,39}$`, unique, cannot be changed (400 `key` on PUT
  with another key). `label` 1–80. `type` cannot change once any contract has a value (409
  `in-use`). `options` required (≥ 1, unique, each 1–80) for Choice/MultiChoice and empty for
  others. `contractTypeIds` must exist (empty = all types). Removing an option that is in use →
  409 `in-use`. `DELETE` with stored values → 409 `in-use` (deactivate instead).
- Rendering: New contract, Edit details, the record (key terms card "Additional details"), the
  register as optional columns (`showInRegister`) and the overview/register filters
  (`showInFilters`, Choice/MultiChoice/YesNo/Person only).

### 8.2 Field rules
- `GET field-rules` (any user; also in `app-config`) and `GET|PUT admin/field-rules` → `FieldRules`.
- `PUT` replaces the whole object; `typeId` must exist and appear at most once; `required` only
  from `OPTIONAL_STANDARD_FIELDS`; defaults: `currency` an active code, `noticePeriodDays` 0–3650,
  `renewalMonths` 1–120, or null.
- Enforced as in §1.3. `endDate` in `required` means the contract may not be open-ended.
  `annualValue`/`totalValue` in `required` means that specific value is required.
- Defaults are applied by the UI when the type is chosen in New contract, and by imports for
  empty `currency`, `reminderRecipient`, `autoRenew`, `renewalMonths` cells (never for
  `noticePeriodDays`: a missing notice period stays missing data).

## 9. Admin: reference data, users, notifications, jobs, general

### 9.1 Legal entities
`GET admin/entities` → `LegalEntity[]`; `POST`, `PUT admin/entities/{id}` (`LegalEntityInput`). No
delete (deactivate). `name` 1–200 unique; `country` two uppercase letters; `currency` an active
currency. Deactivating an entity with Draft/InApproval contracts is allowed; inactive entities
are not offered for new contracts.

### 9.2 Departments and contract types
Existing CRUD unchanged; `PUT` also accepts `isActive` (release 2 backend already stores it).
`DELETE` of a used row stays 409 (now with type `in-use`).

### 9.3 Tags
`GET admin/tags` → `Tag[]` (with `usageCount`, `description`); `POST`, `PUT admin/tags/{id}`
(`TagInput`), `DELETE admin/tags/{id}` (204; removes the tag from contracts, audited). `name`
1–40 unique (case-insensitive, 409 `duplicate`), `color` one of `TagColor`.

### 9.4 Currencies
`GET admin/currencies` → `Currency[]`; `PUT admin/currencies` replaces the list. Codes 3 uppercase
letters, unique; `rateToDkk` > 0; DKK must be present, active, rate 1. Removing a code used by a
contract or entity → 409 `in-use` (deactivate instead). The rates replace the fixed
`CurrencyRates` for every DKK computation, including approval value and `approvals/preview`.

### 9.5 Users and access
- `GET admin/users?q=&active=` → `AdminUser[]` sorted by name.
- `POST admin/users` (`AdminUserInput`) → `AdminUser`, `source: 'Manual'`. In Entra mode the row
  is linked at first sign-in by e-mail (release 2 rule: only onto rows without an object id).
  E-mail unique (409 `duplicate`).
- `PUT admin/users/{id}` → `AdminUser`. In Entra mode `name, email, title, isActive` are synced
  (`syncedFields`); a changed synced field → 409 `synced-field`; roles and memberships are always
  editable here. An admin cannot remove their own Admin role or deactivate themselves (409
  `invalid-state`). Unknown department/entity ids → 400.
- `GET admin/users/{id}/effective-access?q=&page=&pageSize=` → `EffectiveAccess`: the contracts
  the user can see, each with every rule that grants it (`AccessReason`, labels e.g. "Member of
  Purchasing", "Manages DAFA Sverige AB", "Group management", "Owner", "Delegate",
  "Responsible", "Administrator", "Current approver"), `rules` = count per rule, `q` filters the
  contract list as `contracts?q=`. Uses the same access service as everything else.
- `POST admin/reassign/preview` and `POST admin/reassign` (`ReassignInput`): moves the given
  roles from `fromUserId` to `toUserId` on all (or the listed) contracts. Skipped with a reason:
  InApproval contracts; Expired/Terminated unless `includeEnded`; Owner when the target lacks
  ContractOwner/Admin; target inactive (400 for the whole request). `includeObligations` moves open
  obligations of those contracts where `fromUserId` is responsible. The result counts are the same
  as the preview when nothing changed in between. Audited per contract (`contract.reassigned`).

### 9.6 Notifications
- `GET|PUT admin/notification-settings` → `NotificationSettings`. `PUT` must list every
  `NotificationEvent` exactly once (400 `events`). `subject` 1–200, `body` 1–4000; placeholders
  are `{{name}}`; a placeholder not in the event's `placeholders` → 400
  `events.<Event>.subject|body`. `channels` ⊆ Email/Teams/InApp; `recipients` ⊆ `RecipientRule`;
  `extraRecipientIds` existing users. `reminderSchedules`: `typeId` unique; `noticeDays` 1–5
  distinct positive ints (sorted desc on save); `escalationDays` ≥ 0. A type with a row uses it
  instead of the global reminder settings for notice reminders and escalation.
- Placeholders per event (the backend may add more; the frontend shows `placeholders` as given):
  common `contract.title`, `contract.no`, `contract.url`, `contract.entity`, `contract.counterparty`,
  `recipient.name`, `actor.name`, `app.name`; reminders add `deadline`, `days`; approvals add
  `step.role`, `comment`; mention/comment add `comment`; data completion adds `missing`, `message`;
  import/sync add `import.file`, `import.created`, `import.failed`, `source.name`.
- `POST admin/notification-settings/preview` (`TemplatePreviewInput`) → `TemplatePreview` filled
  with sample values; `unknown` lists unknown placeholders (no 400 here).
- Mail goes through `INotifier` as today; Teams is still a stub (outbox rows with channel Teams).

### 9.7 Jobs
- `GET admin/jobs` → `JobInfo[]` for keys `Reminders` (notice reminders + escalation),
  `Obligations`, `ContractEnd` (expiry, auto-renewal), `Digest`, `CounterpartySync` (runs the due
  sync sources), `DirectorySync`, `UploadCleanup`, `DataRoomCleanup`.
- `PUT admin/jobs/{key} { schedule }` → `JobInfo`. `Hourly` ignores `time`; `Daily` needs `time`
  `HH:mm`; `Weekly` needs `time` and `dayOfWeek` 1–7; `Off` disables it. Times are
  Europe/Copenhagen. The hosted service checks every 5 minutes which jobs are due.
- `POST admin/jobs/{key}/run` → `JobInfo` with the new `lastRun` (synchronous, at most 60 s).
  `POST admin/jobs/run` (all) stays.

### 9.8 General settings
`GET|PUT admin/app-settings` → `AppSettings`. `appName` 1–60; `sessionTimeoutMinutes` 15–1440
(sliding cookie expiry); `dataQualityFields` from `DATA_QUALITY_FIELDS` or `cf.<active key>`;
`ddThresholdDkk` ≥ 0; `landingPageByRole` must have all four roles. Feature toggles:
`aiSuggestions` off → `POST intake/extract` stores the file but returns no suggestions/summary/
deviations and `note` "AI suggestions are turned off"; `templates` off → `GET templates` returns
`[]` and `POST intake/from-template` is 400 `feature-off`; `eSign` is a placeholder (UI only shows a
disabled "Send for e-signature" button when on).

Holiday cover, approval rules, retention, templates, audit log, outbox: unchanged.

## 10. Counterparty master data (Admin)

- `GET admin/counterparties?q=&active=&kind=&page=&pageSize=` → `Paged<Counterparty>`, sorted by
  name; `contractCount` unscoped.
- `POST admin/counterparties`, `PUT admin/counterparties/{id}` (`CounterpartyInput`) →
  `Counterparty`. `name` 1–300; `country` two letters or null; e-mail format checked. Same VAT
  no. or external id as another active record → 409 `duplicate`. Deactivate = `PUT` with
  `isActive: false` (contracts keep the reference; inactive records are not offered in pickers).
  Renaming updates `counterpartyName` on all its contracts (audited).
- `GET admin/counterparties/duplicates` → `DuplicateGroup[]`: active records sharing a VAT no.,
  an external id, or a normalised name (lowercase, letters and digits only, legal suffixes
  `a/s, aps, ab, gmbh, ltd, llc, inc, s.r.l., sp. z o.o., b.v., n.v., oü, s.a., s.p.a.` removed).
- `POST admin/counterparties/merge { targetId, sourceIds }` → `MergeResult`: contracts of the
  sources move to the target (`supplierId` and `counterpartyName`), sources are deactivated.
  Target must be active and not among sources (400). Audited.

## 11. Imports (Admin) — counterparties, users, contracts

Flow: fields → upload → (mapping in the UI) → preview → run → result/history.

- `GET admin/imports/{kind}/fields` → `ImportFieldDef[]` (kind = `counterparties` | `users` |
  `contracts`; contracts also list active custom fields as `cf.<key>`).
- `GET admin/imports/{kind}/template` → CSV with the header row (field labels) and one example row.
- `POST admin/imports/{kind}/upload` multipart `file` → `ImportSession`. Accepts `.csv` (UTF-8,
  BOM allowed; separator auto-detected among `,` `;` tab; RFC 4180 quotes) and `.xlsx` (first
  worksheet; formatted cell text; dates as `yyyy-MM-dd`). First row = headers (trimmed; empty or
  duplicate headers → 400 `file`). Max 10 MB and 5000 data rows (400). `suggestedMapping` matches
  headers to field keys or labels case-insensitively, ignoring spaces, `-` and `_`. Sessions
  live 24 hours.
- `POST admin/imports/sessions/{id}/preview` (`ImportRunInput`) → `ImportPreview` with **all**
  rows. Nothing is written. A mapped column that does not exist → 400 `mapping.<key>`. A required
  field not mapped → listed in `mappingErrors` (not 400) and every row is `Error`.
- `POST admin/imports/sessions/{id}/run` (same body) → `ImportResult`. Re-validates; rows with
  errors are not imported, the others are (each row on its own). A session runs once (second run
  → 409 `import-state`). Audited per created/updated record with action `import.*` and once per run.
- `GET admin/imports/history?kind=` → `ImportResult[]` newest first (file imports and sync runs),
  max 100.

Row actions: a row whose key matches an existing record is a **duplicate**: `CreateOnly` → Error
"Already exists: <record>"; `CreateAndUpdate` → Update (only mapped, non-empty cells change);
`SkipDuplicates` → Skip. Two rows in the same file with the same key → the second is Error
"Duplicate of row n". Empty rows are ignored (not counted).

Value parsing: booleans `true/false, yes/no, ja/nej, 1/0, x/(empty)`; numbers with the chosen
decimal separator, thousands separators (space, `.` or `,` — the other one) removed; dates per
`dateFormat` (`auto` tries `yyyy-MM-dd`, `dd-MM-yyyy`, `dd.MM.yyyy`, `dd/MM/yyyy`; ambiguous →
row error); references (entity, department, type) by exact name, case-insensitive; people by
e-mail or exact name; counterparty by external id, VAT no., or name; multiple values separated by
`;`.

### Fields
**counterparties** — key order externalId, vatNo, name.
`name*` (key), `externalId` (key), `vatNo` (key), `country`, `city`, `kind` (choice: CounterpartyKind,
default Supplier), `email`, `phone`, `isActive` (bool, default true).

**users** — key `email`.
`email*`, `name*`, `title`, `roles` (`;`-separated Role names, default Reader), `departments`
(names), `managedEntities` (names), `groupManagement` (bool), `isActive` (bool, default true).
In Entra mode a Create row makes a pre-provisioned user (linked at first sign-in) and synced
fields of existing users are not updated (warning).

**contracts** — key `contractNo` (empty = new number generated; then never a duplicate).
`contractNo`, `title*`, `counterparty*`, `type*`, `entity*`, `department*`, `owner*`, `delegate`,
`responsible`, `reminderRecipient` (Owner|Delegate|Responsible), `startDate*`, `endDate` (empty =
open-ended), `noticePeriodDays`, `autoRenew`, `renewalMonths`, `annualValue`, `totalValue`,
`currency` (default: type default, then the entity's currency), `paymentTerms`, `governingLaw`,
`liabilityCap`, `exclusivity`, `changeOfControl` (empty → null = missing data), `signedDate`,
`summary`, `tags` (names), `cf.<key>`.
Rules: imported contracts **skip approval**. `options.contractStatus` `Draft` or `Active`
(Active with an end date in the past → Expired, with a warning). Missing notice period, value,
change of control, signed date and required custom fields are **warnings** (they become missing
data), not errors. Unknown counterparty → error unless `createMissingCounterparties` (then a
Create warning "New counterparty"). Owner must have ContractOwner or Admin (error otherwise).
Audit text "Contract imported from <file name>". Reminders for imported Active contracts start with
the next due reminder only (release 2 rule).

## 12. Sync sources (Admin)

- `GET admin/sync-connections` → `SyncConnection[]`: connection references configured on the
  server (`Sync:Connections:<name>:Kind = Sql|PowerBi`, `...:ConnectionString` / Power BI uses the
  app registration). Secrets are never returned or accepted by the API.
- `GET admin/sync-sources` → `SyncSource[]`; `POST`, `PUT admin/sync-sources/{id}`
  (`SyncSourceInput`), `DELETE` (204; history kept).
- `POST admin/sync-sources/test` (`SyncSourceInput`, may be unsaved) → `SyncTestResult`: connects,
  runs the query, returns columns and up to 5 rows, writes nothing. A failed connection is
  `ok: false` with `message` (200, not 4xx).
- `POST admin/sync-sources/{id}/run` → `SyncRun` (synchronous up to 120 s; else `status: Running`
  and the run finishes in the background).
- `GET admin/sync-sources/{id}/runs` → last 50 `SyncRun`, newest first.
- Rules: `kind: PowerBi` requires `powerBi` with `workspaceId`, `datasetId` and exactly one of
  `daxQuery` / `table` (the backend runs `EVALUATE '<table>'`); `kind: Sql` requires `sql` with a
  `connectionName` from `sync-connections` of kind Sql and a `query` starting with `SELECT` or
  `WITH` (400 otherwise; executed read-only with a 60 s timeout). `mapping` and `options` as in
  imports (same validation and row rules); result columns play the role of headers. `schedule`
  as for jobs. `nextRunAt` null when Off or inactive. A failed run sends `SyncFailed`.

## 13. Endpoint and role summary

| Route | Method | Who |
|---|---|---|
| `app-config` | GET | any |
| `overview`, `overview/analytics` | GET | any (scoped) |
| `search`, `search/full` | GET | any (scoped) |
| `people/{id}`, `counterparties`, `counterparties/{id}` | GET | any (scoped) |
| `tags`, `custom-fields`, `field-rules` | GET | any |
| `contracts/{id}/tags` | PUT | canEdit |
| `contracts/{id}/watch` | PUT, DELETE | can see |
| `watching` | GET | any |
| `contracts/{id}/comments` | GET, POST | can see |
| `comments/{id}` | PUT | author |
| `comments/{id}` | DELETE | author, Admin |
| `notifications`, `notifications/unread-count`, `notifications/{id}/read`, `notifications/read-all` | GET/POST | own |
| `me/preferences` | GET, PUT | own |
| `contracts/{id}/versions`, `contracts/{id}/compare` | GET | can see |
| `contracts/bulk` | POST | any (per contract rights) |
| `views`, `views/{id}` | GET, POST, PUT, DELETE | any (shared: ContractOwner/Admin create; owner/Admin change) |
| `data-room/exports`, `data-room/exports/{id}`, `.../content` | POST, GET | Admin, group management, entity management |
| `admin/counterparties[/{id}]`, `admin/counterparties/duplicates`, `admin/counterparties/merge` | * | Admin |
| `admin/imports/{kind}/fields`, `/template`, `/upload`, `admin/imports/sessions/{id}/preview`, `/run`, `admin/imports/history` | * | Admin |
| `admin/sync-connections`, `admin/sync-sources[/{id}]`, `/test`, `/{id}/run`, `/{id}/runs` | * | Admin |
| `admin/custom-fields[/{id}]`, `admin/custom-fields/order`, `admin/field-rules` | * | Admin |
| `admin/entities[/{id}]`, `admin/currencies`, `admin/tags[/{id}]` | * | Admin |
| `admin/users[/{id}]`, `admin/users/{id}/effective-access`, `admin/reassign/preview`, `admin/reassign` | * | Admin |
| `admin/notification-settings`, `admin/notification-settings/preview` | * | Admin |
| `admin/jobs`, `admin/jobs/{key}`, `admin/jobs/{key}/run` | * | Admin |
| `admin/app-settings` | GET, PUT | Admin |

## 14. Audit actions added

`contract.tags-changed`, `contract.watch` (not shown in activity), `comment.added`,
`comment.edited`, `comment.deleted`, `contract.reassigned`, `contract.bulk`,
`contract.data-completion-requested`, `dataroom.exported`, `view.shared-changed`,
`settings.custom-fields`, `settings.field-rules`, `settings.entities`, `settings.currencies`,
`settings.tags`, `settings.users`, `settings.notifications`, `settings.jobs`, `settings.general`,
`counterparty.created|updated|merged|deactivated`, `import.run`, `import.contract-created`,
`import.contract-updated`, `sync.run`. The frontend shows `action` as given.

## 15. Mock

`src/web/src/api/mock` implements every route above with the same rules and access filter, so the
frontend is demoable without the backend. Where the mock is simpler it says so in
`docs/DECISIONS.md` (e.g. document text comes from generated placeholder text; sync sources return
generated rows; the data-room zip is built in the browser).
