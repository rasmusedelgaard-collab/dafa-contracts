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
