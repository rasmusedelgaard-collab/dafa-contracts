# Backend review (branch build/v1, commit e486d28)

Independent desk review of `src/api` and `tests`, read against `docs/BUILD_BRIEF.md`. Nothing was compiled or run here (no .NET SDK); every finding below was traced through the code path named. Items I could not settle by reading are in "Needs verification" at the end.

Paths are relative to `src/api/` unless they start with `tests/` or `.github/`.

## Summary

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 3 |
| Medium | 10 |
| Low | 11 |

**Access control verdict.** I found no IDOR. Every read and write of contract data resolves the contract and goes through `ContractAccessService` (`GetReadableAsync` / `GetEditableAsync` / `FilterAsync`):

- Documents by id, obligations by id and links by id load the parent contract and check it first.
- Approval steps by id check `step.ApproverId == current user` (delegate also allows Admin).
- Search, list, CSV export, home, renewals and overview all start from `FilterAsync`.
- Linked-contract titles are filtered per viewer, and a link target must be readable.
- Intake `uploadId` is bound to `CreatedById` (`Core/Services/ContractCommandService.cs:263`).
- Audit, outbox and template content are Admin-only; lookups carry no contract data.
- Current-approver read access is derived from `Pending` steps only, so it ends on approve, reject, delegate or reroute.

The serious problems are in the approval control itself, reminder correctness, and how the sign-in mode fails.

---

## High

### H1. An approved contract can be changed without re-approval
`Core/Services/ContractCommandService.cs:290-302` (`UpdateAsync`), `Core/Services/ContractQueryService.cs:495`

- **What is wrong:** `UpdateAsync` blocks edits only while the status is `InApproval`. In `Approved`, `Active` and every later status the owner can change any field (value, counterparty, type, entity, term) and the status and approval stay as they are. The audit entry is just "Updated contract details", with no field diff.
- **Scenario:** An owner submits a contract with total value DKK 900,000, which matches "Under DKK 1m" (department head only). After approval the owner sends `PUT contracts/{id}` with `totalValue: 50000000` and a different counterparty, then `POST sign`. The contract is Active at DKK 50m with an approval log that says it was fully approved, and nothing in the audit trail shows the change.
- **Fix:** After approval, either lock the approval-relevant fields (value, currency, counterparty, type, entity, department, term), or send the contract back to `Draft` and close the approval when one of them changes. Record old and new values in the audit event. Apply the same rule to document uploads (see M6).

### H2. Editing the end date or notice period silently kills reminders for the new term
`Core/Services/ContractCommandService.cs:203-231` (`Apply`), `:290-302`; `Core/Services/JobService.cs:169`, `:185`, `:213`

- **What is wrong:** Reminder idempotency is one `Reminder` row per contract and kind, and reminders stop once `RenewalDecision` is set. Only the two roll-forward paths (the `Renew` decision on an auto-renew contract, and the job) clear the rows and the decision. A manual edit of `endDate`, `noticePeriodDays` or `autoRenew` clears neither.
- **Scenario:** A contract ends 2027-03-31. Notice120/60/30 go out and the owner records "Renegotiate". The renegotiated contract runs to 2028-03-31, so the owner edits the end date. `RenewalDecision` is still "Renegotiate", so `IsRenewalOpen` is false and no reminder or escalation is ever sent for the 2028 deadline. Even with the decision cleared, the old `Notice*` and `Escalation` rows make `sent.Add` return false. The notice deadline passes unannounced, which is the product's core promise.
- **Fix:** In `UpdateAsync`, when the computed notice deadline changes, delete the contract's notice and escalation reminder rows and clear the renewal decision fields (with an audit line). Alternatively key reminder rows on (contract, kind, notice deadline).

### H3. The sign-in mode fails open to Dev
`Api/Auth/AuthSupport.cs:22-23` (`AuthModes.Parse`), `Api/appsettings.json:12-19`, `Api/appsettings.Production.json`, `Infrastructure/Seed/DatabaseInitializer.cs:46-47`

- **What is wrong:** Any `Auth:Mode` value other than the exact word "Entra" (including a missing value) becomes Dev mode. The defaults are Dev mode, an empty access code and `Seed:DemoData=true`, and `appsettings.Production.json` overrides none of them.
- **Scenario:** The Azure production app is configured with `Auth__Mode=EntraID`, or the setting is lost in a slot swap. The app starts without error, `GET api/dev/users` lists every user anonymously, and `POST api/dev/login` signs anyone on the internet in as an administrator over real contracts. The seeder also inserts the demo users and 46 demo contracts into the production database.
- **Related:** The current test site has the same exposure by design: `.github/workflows/deploy-test.yml:157` sets Dev mode and no access code, so anyone who finds the URL is Admin and can upload files.
- **Fix:**
  - Throw at startup on an unknown `Auth:Mode`.
  - Outside Development, require the mode to be set explicitly, and refuse Dev mode unless a non-empty `Auth:Dev:AccessCode` is configured (or an explicit `Auth:Dev:AllowWithoutCode=true`).
  - Make `Seed:DemoData` default to false and opt in from the test deployment.
  - Set an access code secret in the test deploy workflow.

---

## Medium

### M1. Approval decisions have no concurrency control
`Core/Services/ApprovalService.cs:359-447` (`DecideAsync`), `Core/Services/ContractCommandService.cs:304-327` (`SubmitAsync`), `Core/Domain/Entities.cs` (no row version on any entity)

Each decision reads the steps, changes its own step and saves, with no transaction isolation and no concurrency token. Three verified failures:

- **Parallel stage stuck:** Two approvers in the same stage approve at the same moment. Each request sees the other step as `Pending`, so `stageDone` is false in both. Both steps end `Approved`, the stage never advances, and nobody has a pending step. There is no endpoint to re-evaluate the instance.
- **Double final approval:** A double-click or retry on the last step makes both requests pass the `Pending` check and run `CompleteAsync`, giving two approval-log documents and two notifications.
- **Double submit:** Two `POST submit` calls both pass the `Draft` check and create two instances. When one completes, the other keeps a `Pending` step forever, so that approver keeps read access after the approval is closed and has a permanent item in `approvals/mine`.

**Fix:** Add a concurrency token (`rowversion` on SQL Server, a version column on Sqlite) to `Contract`, `ApprovalInstance` and `ApprovalStep`, and turn `DbUpdateConcurrencyException` into a 409. Alternatively run decide and submit in a serializable transaction. Add a filtered unique index for one pending instance per contract.

### M2. No separation of duties in approvals
`Core/Services/ApprovalService.cs:101-123` (`Resolve`), `:297`, `:509-533` (`DelegateAsync`)

- **Self-approval:** `Resolve` never excludes the contract owner or the submitter. Under the seeded "Under DKK 1m" rule a department head who owns a contract in their own department is its only approver. The `FallbackAdmin` path does the same for an admin's own contract.
- **Delegation to anyone:** `DelegateAsync` accepts any active user as target, including the contract owner, and does not require the `Approver` role. Nothing in the engine checks that role at all. Delegation also gives the target read access to the contract and its documents.
- **Admin takeover:** An Admin can delegate any open step to themselves and approve it.

**Fix:** Exclude the owner and submitter when resolving and when delegating (fall through to the next candidate or the entity manager). Require the `Approver` role on the delegate. Record admin-initiated delegation distinctly in the approval log.

### M3. The submitter can steer the approval route
`Core/Services/ApprovalService.cs:43-47` (`ValueDkk`), `:15-19` (`Specificity`); seeded rules at `Infrastructure/Seed/DatabaseInitializer.cs:145-161`

- **Value basis:** The approval value is `totalValue ?? annualValue`. A contract with `annualValue` 50,000,000 and `totalValue` 1 is routed as DKK 1.
- **Type beats value:** A type-only rule (seeded "NDA", Legal only) has the same specificity as a value band and a higher priority, so labelling any contract "NDA" skips the value bands.

**Fix:** Use the larger of total value and annual value (or annual value times term), and reject a total below the annual value. Give value bands their own precedence, or require type-specific rules to carry a maximum value.

### M4. Renewal decisions: repeatable roll-forward and misleading outcomes
`Core/Domain/ContractCalculations.cs:260-275` (`RenewalRules.Apply`), `Core/Services/ContractCommandService.cs:348-389`, `Core/Services/JobService.cs:117-143`

- **Repeatable Renew:** `Renew` on an auto-renew contract adds `renewalMonths` to the end date and clears the decision, at any time and any number of times. A double-click, or a Renew recorded two years before the deadline, extends the contract by one term per call.
- **Renew on a fixed term:** On a contract without auto-renew, `Renew` is stored as a decision, which stops all reminders, and the job then sets the contract to `Expired` at the end date.
- **LetExpire on auto-renew:** The API accepts it and the job then expires the contract without notice ever being given, although legally such a contract renews. (This is STATUS mismatch 4.)

**Fix:** Allow the roll-forward only once per term (for example only when today is within the reminder window, or by storing the term the decision applies to). For non-auto-renew `Renew`, require a new end date or keep reminding. Reject `LetExpire` on auto-renew contracts.

### M5. Open-ended contracts have no notice handling at all
`Core/Domain/ContractCalculations.cs:16-28`, `Core/Services/ContractCommandService.cs:356-359`

- **What is wrong:** With `endDate` null the notice deadline is null. The contract gets no reminders, is absent from renewals and home tasks, and `DecideRenewalAsync` returns 409, so `GiveNotice` cannot be recorded.
- **Scenario:** An evergreen agreement with six months' notice can never reach `NoticeGiven` or `Terminated` through the API. The only route is to edit an end date in first.
- **Fix:** Allow `GiveNotice` on open-ended contracts (set the end date to the notice date plus the notice period), and add a periodic review reminder for them.

### M6. Contract documents: any file type, and replaceable after approval
`Core/Services/ContractPartsServices.cs:246-277` (`UploadAsync`), `:299`, `:334`

- **File types:** `POST contracts/{id}/documents` has no extension or content allowlist, so `.exe`, `.html`, `.hta` and macro-enabled Office files are stored and served back to colleagues from a trusted internal site. Intake checks the extension only, never the file signature.
- **After approval:** Uploads are allowed in every status. After approval the owner can upload a new version of the main document under the same name, and the approval log still stands.
- **What is fine:** Download is safe against inline XSS. `File(stream, type, name)` forces `Content-Disposition: attachment` and unknown types go out as `application/octet-stream`. Local store keys cannot traverse (`SafeFileName` plus the root check in `LocalDocumentStore.Resolve`).

**Fix:** Allowlist extensions (pdf, docx, xlsx, msg, png, jpg, txt) and verify magic bytes for pdf and docx. Block `Main` uploads while `InApproval`, and treat a new `Main` version after approval like a field change in H1. Add `X-Content-Type-Options: nosniff`.

### M7. Text extraction has no resource limits
`Infrastructure/Providers/TextExtractor.cs:38-74`, `Core/Services/ContractPartsServices.cs:348`

- **What is wrong:** A 25 MB upload is parsed synchronously on the request thread with no page cap, no timeout and no cancellation. `WordprocessingDocument.Open` is called without `OpenSettings.MaxCharactersInPart`, and no zip entry sizes are checked.
- **Scenario:** A user with the ContractOwner role uploads a `.docx` whose `document.xml` inflates to several GB, or a PDF with a pathological page tree. The test site is published `win-x86` (32-bit), so the worker runs out of memory or pins a core, and the site goes down for everyone. `catch (Exception)` does not reliably survive an out-of-memory condition.
- **Fix:** Reject archives whose total uncompressed size exceeds about 100 MB. Set `MaxCharactersInPart`. Cap PDF pages (about 300) and stop once 400k characters are collected (the analyzer truncates there anyway). Run extraction with a timeout.

### M8. Reminder idempotency holds only for one sequential runner
`Core/Services/JobService.cs:153-235`, `:238-292`, `:77-92`; `Infrastructure/Data/AppDbContext.cs:144-149` (no unique index); `Infrastructure/Providers/Notifiers.cs:129-133`

- **Concurrent runs:** The "already sent" check is an in-memory set read at the start. The hourly `BackgroundService` and `POST admin/jobs/run` (or two app instances) can overlap and both send the same reminder, and no unique index stops the duplicate row.
- **Graph mode loses reminders:** `GraphMailNotifier` swallows send failures, yet the job records the `Reminder` row with `SentAt = now`. A Graph outage or throttling therefore loses that reminder permanently. Only the outbox row shows `DeliveredAt = null`, and nothing retries it.
- **Failure spreads:** All job parts share one `DbContext`. If one part fails in `SaveChanges`, its tracked entities stay and every later part's `SaveChanges` fails with it.

**Fix:** Add unique indexes on `(ContractId, Kind)` where `ObligationId` is null and on `(ObligationId, DueDate)`. Serialize `RunAllAsync` with a process-wide lock. Use a fresh scope per part. Retry undelivered outbox rows on each run instead of treating them as sent.

### M9. Entra-mode provisioning has races, weak linking and no first admin
`Api/Auth/AuthSupport.cs:130-178`, `Infrastructure/Data/AppDbContext.cs:61-69`

This code path has never run. Reading it:

- **Duplicate users:** Provisioning happens inside the claims transformation, which runs on every request. The SPA's first parallel calls (`me`, `lookups`, `home`) all find no user and each insert one. There is no unique index on `ExternalId` or `Email`, so the person ends up with several `AppUser` rows. Memberships granted to one row may not be the row a later request resolves.
- **Email linking:** When no row matches the object id, the user is matched by email even if that row already has a different `ExternalId` (`:146-148`). A reused UPN therefore inherits the previous holder's roles and memberships.
- **No first admin:** With a fresh database in Entra mode nobody is Admin and no setting or endpoint creates one. Auto-provisioning and Graph sync only create Readers. Approval fallbacks (`FallbackAdmin`) also return null, so submits fail with 409.

**Fix:** Provision in `OnTokenValidated` (once per sign-in). Add a unique filtered index on `ExternalId`. Link by email only when the row's `ExternalId` is empty. Add an `Auth:Entra:InitialAdmins` setting (list of UPNs).

### M10. Signed-in principals without an app user still reach some endpoints
`Api/Controllers/ApiControllerBase.cs:8`, `Core/Services/ContractQueryService.cs:906`, `Core/Services/ApprovalService.cs:215`, `Api/Controllers/GeneralControllers.cs:141`

- **What is wrong:** `[Authorize]` only requires an authenticated cookie. A deactivated user, or a tenant user who is not provisioned (`AutoProvision=false`), is authenticated but has no `dafa:uid` identity. Most services then throw 401 through `GetProfileAsync`, but `GET api/suppliers` and `GET api/approvals/preview` never call it.
- **Scenario:** A deactivated user can still read the supplier master (names, VAT numbers) and the approval rule names with approver names. In Dev mode the same stale cookie satisfies `HasAccess`, so a deactivated demo user can sign in as anyone without the access code.
- **Fix:** Add a fallback authorization policy that requires the `dafa:uid` claim, and use the same check in `DevController.HasAccess`.

---

## Low

- **L1. CSV formula guard is incomplete.** `Core/Services/ContractQueryService.cs:325-338`. Cells starting with `=`, `+`, `-` or `@` are prefixed, but not cells starting with a tab or carriage return, which Excel also evaluates. Fix: add `\t` and `\r` to the set.
- **L2. Enums accept integers.** `Api/Program.cs:44`. `JsonStringEnumConverter` allows numeric values by default, so `{"decision": 7}` on an approval step is not `Reject` and is processed as an approval, and undefined values of `RenewalDecision`, `LinkKind` or `ObligationStatus` can be stored. Fix: `new JsonStringEnumConverter(allowIntegerValues: false)` and `Enum.IsDefined` checks.
- **L3. "Today" is the UTC date.** `Core/Abstractions/Interfaces.cs:14`. Between 00:00 and 02:00 Danish time (longer for Tianjin, the other way for the US) `daysToNotice`, "Today" and "Overdue" pills, expiry and reminder dates are a day off. Fix: use a configured business time zone (Europe/Copenhagen) in `IClock.Today`.
- **L4. Over-long input gives 500 on SQL Server only.** `Infrastructure/Data/AppDbContext.cs:98`, `:140`. `counterpartyName` (max 300) is not length-validated, and `RoleLabel` (max 300) grows with every delegation. Sqlite, which the tests use, does not enforce lengths. In `CreateAsync` the failure is also retried as if it were a number clash (L5). Fix: validate lengths; truncate the label.
- **L5. Contract number retry is too broad.** `Core/Services/ContractCommandService.cs:277-286`. The unique index is correct, but any `DbUpdateException` triggers one blind retry, a third concurrent create gets a 500, and the audit text keeps the first number. In SharePoint mode the uploaded file was already written to the folder of the first number (`Infrastructure/Providers/DocumentStores.cs:164`), which now belongs to another contract. Fix: retry in a loop only on unique violations, and store the file after the number is final.
- **L6. No throttling on the dev access code.** `Api/Controllers/GeneralControllers.cs:148-161`, `:207-219`. The comparison is constant-time and the code is not logged, but guesses are unlimited, and a session issued before a code change stays valid (12-hour sliding expiry). Fix: add a rate limiter on `api/dev/*`, and put a hash of the code in the cookie and compare it.
- **L7. Concurrent uploads of the same file name overwrite each other.** `Core/Services/DocumentWriter.cs:74-81`, `Infrastructure/Providers/DocumentStores.cs:33-37`. Both requests compute the same version and the second `FileMode.Create` replaces the first file, leaving two rows pointing at one file. Fix: unique index on `(ContractId, Name, Version)` and `FileMode.CreateNew` with a retry.
- **L8. Audit text shows a linked contract's number to people who cannot see it.** `Core/Services/ContractPartsServices.cs:204`. The link itself is filtered per viewer, but "Linked to C-2026-0012 (Amendment)" appears in the activity list of every reader of the source contract. Fix: log only the kind, or filter the activity text.
- **L9. Pending upload cleanup loads the files into memory.** `Core/Services/JobService.cs:350-359`. `ToListAsync` materializes every expired upload (up to 25 MB each) just to delete it, and there is no per-user quota on pending uploads stored in the database. Fix: `ExecuteDeleteAsync`, and cap pending uploads per user.
- **L10. The "due in 90 days" list and the home tile disagree.** `Core/Services/ContractQueryService.cs:117-123` vs `:678-686`. The list view includes contracts that already have a renewal decision; the tile and overview count exclude them. Fix: use `IsDueWithin` in the view.
- **L11. Obligation and approval dead ends.**
  - An obligation's responsible who cannot read the contract gets the reminder but cannot see or complete the obligation (`Core/Services/ContractPartsServices.cs:39-51`, `Core/Services/ContractQueryService.cs:776`).
  - A delegated or rerouted step is never rerouted again by holiday cover (`Core/Services/ApprovalService.cs:581`).
  - A later-stage approver who was deactivated after submit still receives the step when the stage opens.
  - Fix: validate that the obligation responsible can read the contract; re-resolve approvers when a stage opens.

---

## Contract parity with the committed frontend

Checked against `git show HEAD:src/web/src/api/{types.ts,api.ts,client.ts}`. Routes, HTTP verbs, multipart field names (`file`, `kind`), sort keys, enum strings, the 204 responses for decide, delegate and delete, and the 201-with-body on create all match. I found no mismatch that breaks the committed UI beyond the items below.

`docs/STATUS.md` lists nine differences (the task mentioned seven). Disposition of each:

| # | Difference | Change needed |
|---|---|---|
| 1 | `changeOfControl` can be `null` | **Frontend:** type it `boolean \| null` and render "not set". The backend is right; an unknown value must be representable. |
| 2 | Extra `missingData` labels | **Frontend:** render unknown labels as they are; do not switch on a closed list. |
| 3 | Preview strings "Role · not assigned", optional `currency` | None. |
| 4 | `LetExpire` accepted on auto-renew contracts | **Backend:** reject with 400 (see M4). |
| 5 | `renewals` returns overdue rows with negative `daysToNotice` | **Frontend:** show them as overdue. Keep the backend behaviour; these are the rows that matter most. |
| 6 | `POST admin/templates` takes JSON or multipart | None. The committed frontend sends JSON. |
| 7 | An existing session skips the access code | None as a contract matter; see M10 and L6. |
| 8 | Extra response fields | None. |
| 9 | `search` returns nothing under 2 characters | None. |

Two further points:

- The committed frontend has no access-code input and no Entra redirect. If `Auth:Dev:AccessCode` is set (as H3 recommends), the committed `getDevUsers()` gets 401 and nobody can sign in. The working tree is said to handle this, so it must ship together with the code being set.
- `RenewalItem.noticeDeadline` and `daysToNotice` are non-null in the frontend and nullable in `RenewalDto`. In practice the backend never returns null there. Make the DTO properties non-nullable so it stays that way.

---

## Test gaps

The 69 tests cover the access matrix, the SQL filter, calculations, validation, rule matching and the happy paths of approvals and reminders. Nothing below has a test:

- **Access by id:** document content, obligation update/delete/complete, link delete, and step decision called by a user without access (only contract list and detail have 403 tests).
- **Scoped reads:** search, CSV export, home, renewals and overview for a restricted user.
- **Intake and linking:** user B consuming user A's `uploadId`; linking to an unreadable contract.
- **Role and write gates:** overview gating; renewal decision and sign by someone not on the contract.
- **Auth:** `dev/users` and `dev/login` returning 404 in Entra mode over HTTP (only `AuthSettings` is unit-tested); the access code over HTTP (header, body, wrong code, existing session); unknown `Auth:Mode`; demo seeding skipped in Entra mode; logout; deactivated user with a live cookie.
- **Approvals:** edit after approval (H1); double decision on one step; deciding on a closed instance; double submit; owner as own approver; delegating to the owner; value-basis gaming.
- **Reminders and terms:** end-date edit after reminders were sent (H2); repeated `Renew`; `Renew` on a fixed-term contract; open-ended contracts; overlapping job runs; notifier failure.
- **Files:** upload size limits; disallowed types; hostile file names through `SafeFileName` and `LocalDocumentStore.Resolve`; `Content-Disposition` on download; `CsvCell`.
- **Database:** nothing runs on SQL Server, so length limits, the `StartsWith` number query and the contract-number unique clash are untested; no concurrency test for contract numbers.

---

## Needs verification

- **CSRF from sibling sites on the test host.** The session cookie is `SameSite=Lax` with no antiforgery token. That is adequate against other sites, but the test site lives at `*.runasp.net`. If `runasp.net` is not on the Public Suffix List, other customers' sites there count as same-site, and a page on one could post to the multipart endpoints (`contracts/{id}/documents`, `intake/extract`), which need no CORS preflight, with the victim's cookie. I could not fetch the list from this machine. If it is not listed, add an antiforgery header check or require a custom request header on all unsafe methods.
- **Multi-tenant sign-in.** With `Auth:Entra:AutoProvision=true` (the default), whether users of other tenants or guests become Readers depends on `AzureAd:TenantId` (a specific tenant vs `common`/`organizations`) and the app registration. This is not decidable from the code. Readers without memberships see no contracts, but do see lookups (all user names), suppliers and templates.
- **Stale Dev cookie after a switch to Entra mode.** Same cookie name and Data Protection keys. By reading, such a principal gets no app identity and falls under M10 (suppliers and preview only). Confirm at runtime that it is not treated as a tenant user.
- **Claims transformation frequency.** M9's duplicate-user race assumes the transformation runs once per request on parallel first requests, which is my reading of ASP.NET Core. Confirm with a parallel-request test in Entra mode.
- **IIS request limit.** STATUS notes uploads above about 28 MB are rejected by IIS in-process hosting, although the API advertises 50 MB (documents) and 25 MB (intake). Confirm the effective limit on monsterasp.net and align the messages.
- **PdfPig behaviour on hostile PDFs** (M7): the missing limits are verified in our code; the actual memory and CPU ceiling inside PdfPig 0.1.10 is not.
- **SharePoint mode.** The app-only Graph token means SharePoint's own permissions do not follow the access model in section 3, and `storeUrl` links users into folders they may or may not be able to open. This is documented as a stub and was not reviewed in depth.
