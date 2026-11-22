# Integration review: frontend client vs backend API (releases 3 and 4)

Date: 2026-11-22. Branch `build/v1`. Reviewed by reading both sides; nothing was compiled or run
(no .NET SDK in the review environment).

Scope: every method in `src/web/src/api/client.ts` traced to its controller action in
`src/api/Dafa.Contracts.Api/Controllers`, the DTOs in `src/api/Dafa.Contracts.Core/Dtos`, the
JSON options in `Program.cs`, and the screens that call each method.

## Verdict

**Safe to deploy as is to the test site.** No Blocker was found: sign-in, lists, open contract,
create contract, approve, upload/download and every settings page load match on route, verb,
parameters, body and response shape.

Two High findings should be fixed before SharePoint mode is switched on or real users depend on
the app. Both are small frontend changes.

| Severity | Count |
|---|---|
| Blocker | 0 |
| High | 2 |
| Medium | 7 |
| Low | 6 |
| Needs a live check | 4 |

## Findings

Paths are relative to `src/web/src` (frontend) and `src/api` (backend).

### High

| # | Client method | Backend | Difference | What the user sees | Smallest fix |
|---|---|---|---|---|---|
| H1 | `admin.updateDepartment` / `admin.updateDepartmentV3` (`api/client.ts:118`, `:259`), called from `pages/settings/NamedLists.tsx:80` (rename, body `{ name }`) and `:85` (activate/deactivate, body `{ name, isActive }`) | `AdminController.UpdateDepartment` (`Dafa.Contracts.Api/Controllers/AdminController.cs:92`) -> `AdminService.UpdateDepartmentAsync` (`Dafa.Contracts.Core/Services/AdminService.cs:327-348`), `OrgLinkRules.Apply` (`Dafa.Contracts.Core/Services/OrgLinkService.cs:124-147`) | The backend treats `documentLibrary` and `groupId` as "set as given, null clears". The Departments page does not send them, so they arrive as null. | Renaming or deactivating a department in Settings > Departments silently removes its document library and Entra group and resets the link to "Not linked". The Access mapping page then shows warnings, and in SharePoint mode the Entra group mapping for that department is gone. | Frontend: in `NamedLists.tsx:80` and `:85` send the item's existing `documentLibrary` and `groupId` along (the list already returns them). Alternative on the backend: treat omitted fields as unchanged. |
| H2 | `updateContract` (`api/client.ts:143`), called from `pages/record/dialogs.tsx:38` after `validateDraft` (`lib/validation.ts:119`) | `ContractsController.Update` (`Controllers/ContractsControllers.cs:62`) -> `ContractCommandService.ValidateAsync` (`Services/ContractCommandService.cs:469-478`) and the lock check (`:680`) | The backend lets `PUT contracts/{id}` keep `supplierId: null` on older and imported contracts (CONTRACT_V4 section 3). The frontend requires a counterparty from the list on every save. On a contract that has left Draft, `counterpartyName` is in `lockedFields`, so the picker is disabled for non-administrators (`features/ContractForm.tsx:186`). | An owner opens Edit details on a submitted, approved or active contract that has no master counterparty (demo seed rows, contracts created before release 4, some imports). The counterparty field is locked and marked red ("Pick the counterparty from the list"), and nothing can be saved, not even missing data. Administrators and drafts are forced to pick a counterparty before any other change. | Frontend: in `validateDraft` require `supplierId` only on create, or when the contract already has one. Pass a flag from `EditDetailsDialog` when `contract.supplierId === null`. |

### Medium

| # | Client method | Backend | Difference | What the user sees | Smallest fix |
|---|---|---|---|---|---|
| M1 | Every mutation; message built in `lib/errors.ts:40-50` (`errorMessage` returns `problem.detail`) | `InputValidationException` (`Dafa.Contracts.Core/Common/Exceptions.cs:166`), written by `ApiExceptionMiddleware` (`Dafa.Contracts.Api/Hosting/HostingSupport.cs:31-41`) | Every backend 400 has `detail: "One or more fields are invalid."`; the real sentence is only in `errors[field][0]`. The mock puts the real sentence in `detail`. | Wherever a form does not map the field, the toast reads "One or more fields are invalid." with no reason. Examples: a refused logo (unsafe SVG, wrong content), a refused document or template file, reject without comment, obligation, link, tag, comment, bulk, holiday cover, job schedule. The sign and renewal dialogs show the same generic sentence at the date field (`pages/record/dialogs.tsx:145`, `features/RenewalDialog.tsx:51`). | Frontend, one place: in `errorMessage`, when `problem.errors` has entries, show the first message of each instead of `detail`. |
| M2 | `createSignatureRequest` (`api/client.ts:216`), errors read in `features/Signature.tsx:99`, `:194-203` | `SignatureService.CreateAndSendAsync` (`Services/SignatureService.cs:598`) | Per-signer error keys are `signers[0].userId` on the backend and `signers.0.userId` on the frontend. | A server-side signer problem (inactive colleague, colleague without a valid e-mail, wrong e-mail) is not marked on the row; only the generic toast from M1 appears. | Frontend: normalise `signers[<i>].` to `signers.<i>.` when reading the errors. |
| M3 | `extract` (`api/client.ts:161`), pre-check in `pages/newcontract/NewContract.tsx:101`, `:343` using `lib/uploads.ts:3` | `IntakeController.Extract` (`Controllers/ContractsControllers.cs:192`) -> `IntakeService.ExtractAsync` (`Services/ContractPartsServices.cs:448`, `:486`) | The frontend accepts 12 file types for the New contract upload; `intake/extract` accepts only `.pdf` and `.docx`. | Dropping a `.doc`, `.xlsx`, `.msg` or image on New contract passes the client check and is then refused with "One or more fields are invalid." | Frontend: use a PDF/DOCX-only list for the New contract upload (keep the wide list for Documents). |
| M4 | `signContract` (`api/client.ts:145`), offered by `canRecordManualSignature` (`pages/record/Record.tsx:104`, `:786`) | `ContractCommandService.SignAsync` (`Services/ContractCommandService.cs:812-819`), `ContractAccessRules.CanEdit` (`Domain/AccessProfile.cs:52-56`) | The frontend offers "Record signature" to the owner, the delegate or an Admin. The backend also requires edit rights (role ContractOwner and on the contract). | An owner or delegate without the ContractOwner role gets the button and then a 403 "Only the contract owner, the delegate or an administrator can change this contract." | Frontend: add `&& c.canEdit` to `canRecordManualSignature`. |
| M5 | `createSignatureRequest`; gating in `features/Signature.tsx:20` and `pages/record/Record.tsx:155` | `SignatureRules.StatusAllowsSending` (`Services/SignatureService.cs:61-68`) | With "Only approved contracts can be sent" switched off, the backend accepts Draft and Active contracts. The frontend only offers "Send for signature" on Approved. | The setting in Settings > E-signature has no visible effect. | Frontend: read `requireApproved` (add it to `AppConfig.eSign`) and offer the action on Draft/Active, or remove the checkbox until then. |
| M6 | `getMe` (`api/client.ts:132`), handled in `App.tsx:244-250` | Default policy in `Program.cs:41-50`, `AppUserGateMiddleware` (`Hosting/HostingSupport.cs:314-335`) | In Entra mode a signed-in account that is unknown or deactivated gets 403 on `me`. The frontend treats only 401 as "not signed in". | "DAFA Contracts could not start. The server did not answer. Check your connection and try again." for a person who simply has no access. Entra mode only. | Frontend: show "You do not have access to DAFA Contracts. Ask an administrator." on 403. |
| M7 | All methods (`api/client.ts:51-65`); no 401 handling in `app/queryClient.ts` | Cookie session, `CookieSetup.Configure` (`Dafa.Contracts.Api/Auth/AuthSupport.cs`), lifetime from `sessionTimeoutMinutes` at Dev sign-in (`Controllers/GeneralControllers.cs:271-279`) | After the session ends, every call answers 401. Only the `me` query (asked once, never refetched) leads to the sign-in screen. | After a timeout the pages show "could not be loaded" errors and saves fail until the user reloads the page. | Frontend: in the query client, on a 401 from any call, reset the `me` query. |

### Low

| # | Client method | Backend | Difference | What the user sees | Smallest fix |
|---|---|---|---|---|---|
| L1 | `resendSignatureRequest`, `cancelSignatureRequest`, `simulateSignature` (`api/client.ts:217-219`), buttons in `features/Signature.tsx:268-306` | `SignatureService.LoadManageableAsync` (`Services/SignatureService.cs:803-816`) | The buttons are shown to everyone who can see the record; the backend allows the sender, colleagues with signing rights for the entity and administrators. | Others get a 403 with a readable sentence. | Frontend: show the buttons only when `c.canSendForSignature`, the user is Admin, or the user created the request. |
| L2 | `admin.updateAppSettings` (`api/client.ts:321`), checkbox in `pages/settings/System.tsx:375-378` | `AppSettingsStore.LoadAsync` (`Services/SettingsService.cs:58`) | `features.eSign` is overwritten on every read with "provider is not None". | The "E-signature" checkbox under General can be ticked and saved, then shows its old value again. | Frontend: remove the checkbox (Settings > E-signature decides). |
| L3 | `admin.createTemplate` (`api/client.ts:239`), check in `pages/settings/Templates.tsx:37` | `AdminService.CreateTemplateAsync` (`Services/AdminService.cs:486`) | The frontend accepts `.docx` and `.dotx`; the backend accepts `.docx` only. | A `.dotx` upload is refused with the generic message from M1. | Frontend: accept `.docx` only. |
| L4 | `getDistinct` (`api/client.ts:207`), called with `filter={parsed}` from `pages/Register.tsx:243` | `Release4Controller.Distinct` (`Controllers/Release4Controllers.cs:45-61`, `filter.Q = null` at `:59`) | The value lists of the column filters are counted without the register's tab (`view`) and without the free-text search. | Counts in a column filter can be higher than the rows the list shows on tabs such as "Mine" or with a search text. | Frontend: pass `view` with the filter. Leave `q` as is (the contract uses `q` for the value search). |
| L5 | `getCounterparties`, `createCounterpartyQuick` (`api/client.ts:178`, `:209`), `features/Pickers.tsx:277`, `:326-330` | `SearchService.FindCounterpartiesAsync` (`Services/SearchService.cs:584-603`), `PickerService.CreateCounterpartyAsync` (`Services/PickerServices.cs:163`) | (a) The backend also matches VAT number and external id; the picker filters the answer again by name only. (b) A duplicate by VAT number returns `existingId`; the picker selects it but labels it with the name the user typed. | (a) Searching by VAT number finds nothing. (b) The form shows the typed name until the contract is saved; the saved contract has the master name. | Frontend: (a) drop the second name filter when the server already filtered; (b) refetch the counterparty by id for its name. |
| L6 | `createSignatureRequest`, document list in `features/Signature.tsx:172-177` | `SignatureService.CreateAndSendAsync` (`Services/SignatureService.cs:575-576`) | The dialog lists the approval log as a document that can be sent; the backend refuses it. | A 400 shown at the documents field ("The approval log cannot be sent for signature."). | Frontend: leave documents of kind `ApprovalLog` out of the list. |

## Needs a live check

Not settled by reading; check these on the test site.

1. **Save a user without a home legal entity.** The user dialog always sends `entityId: null`
   (`pages/settings/Users.tsx:28-31`); the backend field is a non-nullable `JsonElement`
   (`Dtos/DtosRelease3.cs:727`, read in `AdminDirectoryService.cs:273-310`). This should arrive as
   a JSON null and clear the field. If it is refused, every user save without an entity fails with 400.
2. **Export selected with many rows.** `features/Bulk.tsx:27` sends each id as a query parameter.
   About 45 selected contracts exceed the IIS default query-string limit of 2048 characters; no
   `web.config` in the repository raises it.
3. **Template upload.** Two `POST admin/templates` actions are told apart by `[Consumes]`
   (`AdminController.cs:140-151`); the frontend always uses the multipart one.
4. **First request after sign-in on the hosted site** (cookie `Secure` behind the HTTPS front end);
   already listed in STATUS.

## Backend-listed mismatches (docs/STATUS.md): does the frontend cope?

### Release 4

| Backend note | Frontend copes? |
|---|---|
| Unsafe SVG logos are refused with 400 `file` | Partly: the upload is refused cleanly, but the reason is lost (M1). |
| `provider` can only be one that is configured on the server | Yes: the message is shown at the provider field (`pages/settings/ESign.tsx:53`). |
| Partially signed is reported as `Sent`; extra `signedCount` | Yes. |
| Completion moves the contract to Active only from Approved | Yes (the frontend only sends Approved contracts, see M5). |
| `POST contracts/{id}/sign` refused while a request is open; still needs ContractOwner or Admin | First part yes (button hidden while a request is `Sent`). Second part no (M4). |
| Test link in test mode returns `Linked` | Yes. |
| Reconciliation always synchronous, never `Running` | Yes. |
| `counterpartyCreation` also accepts `Admins` / `Everyone` | Yes (the frontend sends the contract's values). |
| Value ranges: a contract without the value never matches | Yes. |
| `GET watching` is not entity-scoped | Yes. |
| `StorageProblem` notifications outside the catalogue | Yes: unknown events get the default icon (`shell/NotificationBell.tsx:92`); the link `/settings/storage` exists. |
| Extra fields and routes (`createdById`, `statusMessage`, `AppConfig.theme`, `PersonOption.departmentNames`, `GET signature-requests/{id}`, template filters) | Yes, ignored. |

### Release 3

All 16 notes are extras or stricter validation that the frontend ignores or already handles:
400 on malformed filter values (the UI never produces them), `cf.<key>.min/.max` (used by the
release 4 header filters), data room answers 202 with `Ready` (the dialog handles it), synchronous
sync and job runs, extra sync connections in demo mode, `syncedFields`, import rules, extra audit
actions, no Home tasks for watched contracts.

### Releases 1 and 2

Still as listed (nullable `changeOfControl`, extra `missingData` labels, extra fields). The
frontend types already allow them.

## Coverage

- Client methods in `api/client.ts`: **177** (176 HTTP calls and the `assetUrl` helper).
- Traced to a controller action and compared on verb, route, query/body names, response type and
  status code: **177 of 177**.
- Call sites read in the screens: all methods that a screen uses. Five have no caller:
  `getSuppliers`, `getOverview`, `searchPeople`, `getSignatureRequests`, top-level `getTags`.
- Not covered: behaviour that needs a running system (EF query translation, real SharePoint,
  DocuSign/Penneo, Entra sign-in).

## Flows verified as matching

- **Sign-in:** `dev/config`, `branding` and `branding/logo` are anonymous; `dev/users` with the
  `X-Dev-Access-Code` header; `dev/login` returns the `me` shape; 401 instead of redirects; logout 204.
- **CSRF header:** the client sends `X-Requested-With: dafa-contracts` on every call, multipart and
  DELETE included; the only exempt route is `POST /api/esign/webhooks/{provider}`.
- **Cache headers:** `no-store` on `/api`, `no-cache` on the SPA shell; a missing hashed chunk
  answers 404 (not `index.html`), which triggers the reload-once logic in `lib/lazyPage.ts`.
- **JSON conventions:** camelCase, enums as strings in both directions, `DateOnly` as `yyyy-MM-dd`,
  `DateTime` as UTC, `Paged<T>` as `{ items, total, page, pageSize }`.
- **Lists and filters:** `GET contracts`, export, `overview/analytics` with repeated parameters,
  `cf.<key>`, `cf.<key>.min/.max`, the release 4 header-filter parameters, sort keys, views.
- **Entity scope:** repeated `entityId` on `home`, `renewals`, `approvals/mine`, `overview`,
  `search` and `search/full`.
- **Contract record:** `GET contracts/{id}` with `peek`, all detail fields, tags, watch, comments,
  versions and compare, obligations, links.
- **Create contract:** upload path and template path both end in `POST contracts` with
  `supplierId` (validated before sending) and `uploadId`; answer 201 with the detail. Imports do
  not use `POST contracts`.
- **Approvals:** preview, submit, decide, delegate (204).
- **Documents:** multipart field names `file` and `kind`; downloads with `Content-Disposition`;
  storage problem types (`storage-unavailable`, `file-missing`, `storage-not-configured`).
- **Pickers:** counterparties (`GET counterparties?q=`), quick create with `existingId` on a
  duplicate, people from lookups (with `entityId`, `roles`, `isActive`).
- **Templates:** categories (list, CRUD, order), template upload with `categoryId`, repeated
  `typeIds` and `language`, preview, `intake/from-template`.
- **E-signature:** permissions, send, resend, cancel (no body), simulate; settings; `signature` and
  `canSendForSignature` on the contract.
- **Personal settings:** `theme` (light, dark, system), digest, events, default entity, landing page.
- **Admin:** users (`canSendForSignature`, `signatureEntityIds`, `entityId`), effective access,
  reassign, entities with site and group, test link, access mapping, storage health,
  reconciliation (JSON `null` when never run), jobs, notification settings, general settings,
  logo upload and delete by `variant`, currencies, tags, custom fields, field rules, counterparties,
  imports, sync sources, approval rules, retention, reminders, holiday cover, audit, outbox.
- **Problem types:** `stale`, the storage types, `not-permitted`, `signature-state`, `duplicate`,
  `in-use`, `feature-off` have the same strings on both sides.
