# Decisions (append-only, one line per decision)
- DevOps: test deployment uses Web Deploy (msdeploy) from a Windows runner, the method monsterasp.net documents for GitHub Actions; msdeploy.exe is called directly with the vendor's documented arguments instead of the third-party action in their sample.
- DevOps: publish target for monsterasp.net is framework-dependent `win-x86` (host default bitness is 32-bit; vendor sample uses win-x86); overridable with repository variables `PUBLISH_RUNTIME` and `PUBLISH_SELF_CONTAINED`.
- DevOps: test-mode settings are written to `appsettings.Production.json` at deploy time from the secret `MONSTERASP_DB_CONNECTION_STRING`; the file is never committed and the connection string is written under three common key names until the backend key is confirmed.
- DevOps: deploy secrets are named `MONSTERASP_WEBDEPLOY_URL`, `_SITE`, `_USERNAME`, `_PASSWORD` and `MONSTERASP_DB_CONNECTION_STRING`; a missing secret makes the deploy skip (green) with a message, not fail.
- DevOps: Deploy test never rebuilds; it deploys the `api-publish` artifact of a successful CI run (triggering run, latest on the branch, or a given run ID for rollback).
- DevOps: Web Deploy sync skips `App_Data` so locally stored test files survive deployments, and uses `-allowUntrusted` as in the vendor's command (variable `MONSTERASP_ALLOW_UNTRUSTED_CERT=false` turns it off).
- DevOps: NuGet caching uses actions/cache keyed on project files, because setup-dotnet's built-in cache requires `packages.lock.json`.
- DevOps: Azure skeleton targets Linux App Service (`DOTNETCORE|10.0`) in `swedencentral`, system-assigned identity, Azure SQL Entra-only auth with public endpoint plus Allow-Azure-services rule, Key Vault with RBAC; private endpoints deferred; no storage account because files live in SharePoint.
- DevOps: CI adds a public `wwwroot/build-info.json` (commit, run, build time) to the published app so a deployment can be traced to a commit.

## Frontend phase 1 (2026-11-22)

- FE: Payload shapes the brief only names (suppliers, search, intake, templates, approvals preview, renewals, overview, all admin resources, dev users) are defined in `src/web/src/api/types.ts` under "Shapes the brief leaves open"; backend should match them or record a change here.
- FE: `ContractDetail` carries the section 4 fields flat, plus `renewalDecidedBy` (display name), `renewalDecidedAt`, `renewalComment`, `nextReminderDate`. `noticePeriodDays` is `number | null` so imported contracts with a missing notice period can be represented.
- FE: `contracts?sort=` is a field name with `-` prefix for descending: `title|entity|owner|notice|status|value`; default `notice` ascending, contracts without a deadline last.
- FE: `view=all` returns every contract the user can see (all statuses); the register labels it "All contracts", not "All active" as in the design. A `status` filter chip and a "Missing data" view were added.
- FE: `noticeDeadline` and `daysToNotice` are null for NoticeGiven, Expired and Terminated contracts (the mock does this; otherwise ended contracts sort first as "overdue").
- FE: `missingData` values used by the mock: "Notice period", "Contract value", "Signed date".
- FE: `Suggestion.field` is a contract field name; for `*Id` fields the value is the lookup id (the UI also accepts the lookup name); booleans are "true"/"false".
- FE: `approvals/preview` `value` is the contract value in DKK (total value if set, else annual), converted in the UI with indicative rates in `src/web/src/lib/format.ts`. Approver strings are "Role · Name".
- FE: Reminder `kind` values: `Notice<days>` (e.g. `Notice120`) and `Escalation`.
- FE: `PUT admin/holiday-cover` replaces the whole list. `GET admin/audit` pages 50 per page.
- FE: Renewal decisions GiveNotice and LetExpire require a comment; LetExpire is not offered on auto-renew contracts. Edit is blocked (409) while a contract is InApproval. Submit is rejected while `missingData` is not empty.
- FE: Edit rights in the mock: Admin, or role ContractOwner AND owner/delegate/responsible on the contract. Renewal decision: people on the contract or Admin, Active contracts with an end date.
- FE: A 401 from `GET me` shows a demo-user picker from `dev/users`; no Entra sign-in redirect is implemented because the brief defines no login endpoint.
- FE: The top bar (search, entity scope, New contract) is shown on every shell page, not only Home; the entity scope select and the register's Entity chip are the same state and are sent as `entityId`. Scope does not affect Home (the `home` endpoint has no entity parameter).
- FE: Sidebar group labels and phone tab labels are 12px (design: 11px) and group labels use #5b6672 (design: #8a94a0) to meet the brief's "never below 12" and 4.5:1 rules.
- FE: Mock `intake/from-template` produces an .rtf file (no OpenXML in the browser); the real API returns .docx.
- FE: `VITE_API_MODE` defaults to `mock` for `npm run dev`/tests and `real` for `npm run build`; `VITE_API_MODE=mock npm run build` gives a backend-free demo build.

## Backend phase 1 (2026-11-22)

- BE: Connection string is read from `ConnectionStrings:Default` only; `Database:Provider` selects `Sqlite` (default) or `SqlServer`.
- BE: Application services live in Core and use `IAppDbContext` (EF Core `DbSet`s), so Core references `Microsoft.EntityFrameworkCore`; no repository layer.
- BE: Entities have plain Guid foreign-key columns, no navigation properties and no database foreign keys or cascades, to keep `EnsureCreated` identical on Sqlite and SQL Server (no multiple-cascade-path errors).
- BE: The access filter runs in SQL; contract filtering, calculated fields, sorting and paging run in memory afterwards (about 800 contracts), because Sqlite cannot sort or sum decimals.
- BE: All timestamps are UTC `DateTime` (not `DateTimeOffset`); "today" is the UTC date.
- BE: Approval rule specificity counts entity, type, department and the value band (min and/or max together count as one); ties go to the higher `priority`, then the name. Bands are min inclusive, max exclusive, in DKK.
- BE: Approval value is total value when set, otherwise annual value, converted to DKK with fixed placeholder rates (`CurrencyRates`), matching the frontend preview.
- BE: A role resolver that finds nobody falls back to the first administrator (label says so); submit fails with 409 when no rule matches. Rule stages are stored as JSON on the rule.
- BE: Delegating marks the original step `Delegated` and adds a new `Pending` step for the delegate in the same stage; delegated steps do not count towards stage completion. Holiday cover reroutes a step at most once, when its stage opens or by the hourly job.
- BE: On rejection the other open steps go back to `Waiting`, so a `Pending` step always belongs to an open approval. The current approver's read access is derived from `Pending` steps.
- BE: Edit rights: Admin, or role ContractOwner and owner/delegate/responsible on the contract. 403 for a contract that exists but is not visible, 404 for one that does not exist.
- BE: Notice reminders: when several offsets are already due, only the one closest to the deadline is sent (a contract registered late gets one reminder, not three). Escalation goes to the owner from `escalationDays` before the deadline until the end date.
- BE: Auto-renew contracts roll forward automatically when the end date passes without notice or a LetExpire decision; rolling forward deletes the contract's notice reminder rows so the next term gets a new cycle. NoticeGiven contracts become Terminated, others Expired.
- BE: `endDate: null` in a contract body means open-ended; `openEnded: false` with no end date is a validation error.
- BE: Renewal decisions: Active contracts with an end date only; GiveNotice and LetExpire require a comment. `noticeDeadline`/`daysToNotice` are null in lists for NoticeGiven, Expired and Terminated.
- BE: `missingData` holds display labels ("Notice period", "Contract value", "Signed date", ...), as in the frontend mock.
- BE: Intake uploads are kept as rows (`PendingUploads`, bytes in the database) until the contract is created; templates are stored in the database too. Only contract documents go to the document store.
- BE: The approval log is a UTF-8 text file. Seeded documents are text files marked as demo documents.
- BE: Demo users, suppliers and counterparties are fictional names; demo data is never seeded when `Auth:Mode=Entra`. `appsettings.Production.json` does not turn demo data off, because the first production-environment deployment is the test site.
- BE: Dev access code (`Auth:Dev:AccessCode`): when set, `GET api/dev/users` needs header `X-Dev-Access-Code` and `POST api/dev/login` needs `accessCode` (constant-time compare of SHA-256 hashes); a caller with an existing session is not asked again. `GET api/dev/config` is anonymous and returns `{ authMode, accessCodeRequired }`. Empty setting = no change.
- BE: Data Protection keys are persisted to `DataProtection:KeysPath` (default `App_Data/keys`); failure to write is a logged warning, not a startup error.
- BE: Roles are read from the database on every request (claims transformation), not stored in the cookie. `POST api/dev/login` returns `Me`. `GET api/auth/login?returnUrl=` challenges in Entra mode and is 404 in Dev mode.
- BE: `POST contracts/{id}/documents` and `POST contracts/{id}/links` return the updated `ContractDetail`, as the frontend `Api` interface expects. `POST admin/templates` accepts JSON (generates a starter document) or multipart (uploaded .docx).
- BE: No HTTPS redirection middleware in the app (the host terminates TLS); the session cookie is marked Secure outside Development unless `Auth:Cookie:AllowInsecure=true`.
- BE: Package versions pinned: EF Core 10.0.0 (Sqlite, SqlServer), Microsoft.Identity.Web 3.14.1, DocumentFormat.OpenXml 3.3.0, PdfPig 0.1.10; tests: Microsoft.NET.Test.Sdk 17.12.0, xunit 2.9.2, xunit.runner.visualstudio 2.8.2, Microsoft.AspNetCore.Mvc.Testing 10.0.0.

## Frontend phase 2 (2026-11-22)

### Contract additions for backend
- FE: `GET dev/config` (anonymous) -> `{ authMode: 'Dev'|'Entra', accessCodeRequired: boolean }`. If it fails the app assumes Dev without a code.
- FE: Access code is sent as header `X-Dev-Access-Code` on `GET dev/users` and as `accessCode` in the `POST dev/login` body; a missing or wrong code is expected as 401 (403 is treated the same). The user switcher sends the stored code too.
- FE: Entra mode: a 401 from `GET me` sends the browser to `GET /api/auth/login?returnUrl=<path + query + hash, URL-encoded>`.
- FE: `GET reminder-settings` (any signed-in user) -> `ReminderSettings`, read-only; `admin/reminder-settings` is unchanged.
- FE: `GET contracts/{id}?peek=true` returns the same detail without recording "recently opened"; used by the register quick look and the renewal dialog.
- FE: `GET approvals/preview` response gains optional `ruleId` and `ruleName` (the matched rule), shown in Settings.
- FE: `POST admin/templates` is sent as multipart: `file` (.docx/.dotx), `name`, `description`, optional `typeId`. `PUT admin/templates/{id}` stays JSON metadata. New `GET admin/templates/{id}/content` downloads the file.
- FE: Every request carries `X-Requested-With: dafa-contracts`.
- FE: `ContractDetail.changeOfControl` is `boolean | null`; `ContractDetail.lockedFields: string[]` (camelCase field names; `endDate` also locks the open-ended checkbox, `counterpartyName` the supplier pick); a change to a locked field is expected as 409.
- FE: `POST contracts/{id}/renewal-decision` body gains `newEndDate` (ISO date), sent only for `Renew` on a contract that is not auto-renew.
- FE: `GET renewals` rows may have negative `daysToNotice`. The page asks for `months=13` so the last month column of the axis is complete.
- FE (requested, not yet in the backend DTOs): `roles` on `Lookups.users[]`, so the delegate picker can offer only Approver/Admin users; `submittedById` on `ContractDetail`, so it can exclude the submitter. Both are optional in `types.ts`.
- FE: `GET admin/audit?contractId=&page=` is linked as `/settings/audit?contractId=<id>`; the contract filter uses `GET search`.

### Other decisions and deviations from the designs
- FE: The API mode follows the Vite mode (`--mode real`, `--mode demo`) instead of env files, because the root `.gitignore` ignores `.env.*`; `VITE_API_MODE` overrides. The mock is a dynamic import behind the build-time constant.
- FE: Renewals axis starts on the 1st of the current month (the design starts at the next month) so deadlines later this month are visible; a line marks today. Overdue deadlines sit at the left edge.
- FE: Renewals adds a list of all deadlines under the timeline and a "Record decision" button on the selected card (the design has only "Open contract"); under 720 px the timeline is not shown.
- FE: Approval route cards show the approver's name with the role and state below (the design shows the role only), and group parallel approvers under one stage label. The header shows the owner, because the API does not say who submitted.
- FE: Phone approval view keeps a comment field and Delegate in the page above the sticky Reject/Approve bar (PhoneApprove.dc.html has no comment field, but Reject requires one).
- FE: Queue dot and wait text turn red from 7 waiting days (the design has blue and amber only).
- FE: Overview drops v1's "Upcoming notice deadlines" table and "Your tasks" (they live on Renewals and My work) and adds contracts by type, change of control, expiring without renewal and missing data, as the brief lists.
- FE: Settings uses a card side navigation grouped Organization / Rules / Logs with one route per section; Retention is a row per contract type with one "Save changes" for all changed rows.
- FE: After a 409 on approve or submit the toast is "This was just updated by someone else." plus the server's detail when it has one.
- FE: Dev sign-in sets `me` from the `dev/login` response instead of refetching it.

## Backend phase 2 - review fixes (2026-11-22)

### Contract additions for frontend
- BE: Every `/api` request that is not GET or HEAD must send `X-Requested-With: dafa-contracts` (login and logout included, no exceptions); otherwise 400 problem "Missing request header".
- BE: `ContractDetail.lockedFields: string[]` - camelCase request field names the current user may not change. Empty in Draft and for administrators. For others on a contract that has left Draft: the locked fields that currently have a value, out of `title, typeId, supplierId, counterpartyName, entityId, departmentId, startDate, endDate, openEnded, noticePeriodDays, autoRenew, renewalMonths, annualValue, totalValue, currency, liabilityCap, exclusivity, changeOfControl, governingLaw, priceAdjustment, priceAdjustmentDate`. A locked field that is still empty is not listed and may be filled in once by anyone with edit rights (missing data); after that it is locked. `annualValue` and `totalValue` go together: both are listed as soon as either has a value. Never locked: `ownerId, delegateId, responsibleId, reminderRecipient, paymentTerms, summary`, obligations, links, non-Main documents.
- BE: `PUT contracts/{id}` that changes a listed field returns 409, `type` `urn:dafa:problem:locked-fields`, `detail` "Approved terms are locked. Register an amendment as a linked contract, or ask an administrator. Locked: <labels>." Unchanged values may be sent as they are.
- BE: 409 problem `type` values: `urn:dafa:problem:stale` (changed by someone else at the same time - reload and retry; the only type for which "This was just updated by someone else" is right), `urn:dafa:problem:no-approval-rule` (no rule matches, or the rule has no stages), `urn:dafa:problem:no-approver` (no approver independent of owner and submitter could be found), `urn:dafa:problem:locked-fields` (approved term or main document locked), `urn:dafa:problem:duplicate` (the link already exists), `urn:dafa:problem:invalid-state` (every other 409: not a draft, in approval, step not open, approval closed, term already renewed, too early to renew, open-ended contract, ...). Every 409 has a `detail` meant to be shown to the user.
- BE: `ContractDetail.submittedById: Id | null` (who submitted the current or most recent approval) and `Lookups.users[].roles: string[]`.
- BE: `POST contracts/{id}/renewal-decision` takes optional `newEndDate`. `Renew` needs it unless the contract is auto-renew with a renewal period (400 on `newEndDate` otherwise); it must be after the current end date. `Renew` is 409 before the first reminder date of the term (largest notice offset before the notice deadline) and when the term was already renewed. `LetExpire` on an auto-renew contract is 400 on `decision`.
- BE: Open-ended contracts: `canDecideRenewal` is true for the people on an Active one; only `GiveNotice` (with comment) is accepted (other decisions 409). It sets status NoticeGiven and `endDate` = today + notice period. A reminder of kind `AnnualReview` is sent 60 days before each anniversary of the start date and appears in `reminders`.
- BE: Approvals: the owner and the submitter are never approvers; `roleLabel` then ends with "(instead of <name>, who owns or submitted the contract)". `POST approvals/steps/{id}/delegate` returns 400 on `userId` when the target is the owner, the submitter, the current approver, the caller, inactive, without the Approver or Admin role, or already an approver in that stage. A second decision on a step, or a decision on a closed approval, is 409 `invalid-state`. `approvals/preview` applies the substitution for the calling user; `value` should be the larger of total and annual value.
- BE: Documents: allowed types pdf, docx, doc, xlsx, xls, pptx, msg, eml, txt, png, jpg, jpeg, checked by extension and content (400 on `file`); maximum 25 MB. From submission on, a `Main` document (or a new version of an existing Main document's name) is 409 `locked-fields` for everyone but administrators. `intake/extract` result has optional `note: string | null` when the file was too large or slow to read (then no suggestions).
- BE: Dev sign-in: wrong access codes are limited to 10 per client address per 10 minutes, then 429 for `dev/users` and `dev/login` (also with the right code). A session of a deactivated user, or one issued under another access code, is 401 everywhere and does not replace the code. Enum values must be strings; numbers are 400.
- BE: `RenewalItem.noticeDeadline` and `daysToNotice` are never null. `JobRunResult` gains `annualReviewReminders`. `contracts?view=due90` now leaves out contracts with a recorded decision, like the home tile.

### Decisions
- BE: Optimistic concurrency is a Guid column `Version` on Contracts, ApprovalInstances and ApprovalSteps, replaced on every save by the DbContext (works on Sqlite and SQL Server; no rowversion). Every approval decision also rewrites the instance row, so two simultaneous decisions in one stage collide; the losing request is replayed from fresh rows (up to 3 times), which re-evaluates stage completion. Unhandled concurrency conflicts are 409 `stale`.
- BE: Filtered unique index: one Pending approval instance per contract (`[Status] = 0`, valid on both providers). Unique indexes: Users.ExternalId, Documents (ContractId, Name, Version), Reminders (ContractId, Kind, TermKey).
- BE: The schema changed (new columns and indexes). There are still no migrations, so existing databases must be recreated (see docs/DEPLOY_TEST.md).
- BE: H1 lock starts at submission (every status except Draft), not at final approval. Filling an empty locked field is allowed once for editors and audited as `contract.missing-data-completed`; administrator changes to filled locked fields are audited as `contract.approved-terms-changed`; every update audit lists old and new value per field (values cut at 80 characters).
- BE: Deviation from the project manager's wording on fill-once: when one of annual/total value is set, the empty one is NOT fillable by owners, because the approval value is the larger of the two and filling the other could raise an approved contract into a higher band. Only when both are empty can they be filled in.
- BE: H2: a changed notice deadline (edit, Renew, automatic roll-forward) deletes the contract's Notice* and Escalation reminder rows and, on an Active contract, clears the renewal decision (audit `reminders.reset`). A recorded notice (status NoticeGiven) is kept. Reminder rows also carry the term (`TermKey`).
- BE: H3: `Auth:Mode` has no default outside `appsettings.Development.json`; anything but Dev/Entra stops the app. Dev mode outside Development requires `Auth:Dev:AccessCode` of at least 12 characters. `Seed:DemoData` defaults to false and seeds only when `Auth:Mode=Dev`. The dev cookie carries a fingerprint of the access code.
- BE: M2 substitution order: entity manager of the contract's entity, Group CFO, Group CEO, then an administrator (added as last resort); if nobody is left, submit is 409 `no-approver`. Holiday cover never routes to the owner or submitter. An approver who is inactive when their stage opens is replaced the same way. Admin reassignment is labelled and audited separately (`approval.reassigned`); an administrator cannot reassign a step to themselves.
- BE: M3: approval value = max(total, annual). The seeded NDA rule has max DKK 1m. No validation that total >= annual.
- BE: M4: "once per term" = `Contract.LastRenewedDeadline` (the deadline of the renewed term) + the reminder-window rule + the contract's concurrency token. Renew with `newEndDate` is also accepted on auto-renew contracts and then overrides the renewal period.
- BE: M5: the annual review reminder goes to the contract's reminder recipient; it is not shown as a planned reminder before it is sent.
- BE: M8: one job run at a time per process (the second waits up to 5 minutes); a failed job part discards its changes. `INotifier.SendAsync` returns false on a delivery failure; the reminder row is kept unsent and retried on later runs, at most 5 attempts, then reported in the job result.
- BE: M9: the Entra object id is the identity; e-mail linking only onto rows without an object id; `Auth:BootstrapAdmins` creates/promotes administrators at sign-in. Provisioning stays in the claims transformation (not moved to OnTokenValidated); the unique index plus a retry makes parallel first requests safe.
- BE: M10: default authorization policy requires the app-user claim. In Dev mode a principal without an app user is treated as anonymous (401, so the user picker shows); in Entra mode it stays authenticated and gets 403 (avoids a sign-in redirect loop).
- BE: "Today" is the date in Europe/Copenhagen (falls back to UTC if the host does not know the zone); timestamps stay UTC.
- BE: Local document store keys contain a random part and files are created with CreateNew, so nothing is overwritten. Upload limit lowered from 50 MB to 25 MB (request limit 30 MB).
