# Review: how the app integrates with SharePoint

Date: 2026-11-22. Branch `build/v1`. Reviewer: independent architecture review, read-only.

Scope: the path between the app and SharePoint Online. Method: read the docs and the code
(`Infrastructure/Providers/DocumentStores.cs`, `AccessTokenProvider.cs`, `DirectoryAndSignature.cs`,
`DependencyInjection.cs`, `Core/Services/DocumentWriter.cs`, `ContractPartsServices.cs`,
`DocumentTextIndexer.cs`, `ContractQueryService.cs`, `ContractCommandService.cs`,
`ApprovalService.cs`, `DataRoomService.cs`, `Api/Hosting/HostingSupport.cs`), then checked the
platform facts against Microsoft documentation (links in section 5).

Three labels are used throughout:

- **Verified in code** - I read it.
- **Verified in Microsoft docs** - a link is given.
- **Inferred** - my conclusion; nothing was run. SharePoint mode has never run against a tenant,
  so every statement about its runtime behaviour is a desk check.

Release 4 (entity -> site and department -> library/group mapping) is being written right now.
What I saw of it is described as "in progress" and not judged.

## Summary

The design is sound and already simple: SQL is the system of record, SharePoint only holds the
files, search runs on text kept in SQL, the access check never calls SharePoint, and documents
are stored and fetched by SharePoint ID (not by path), so renames and moves inside a library do
not break downloads.

The SharePoint side itself is a first draft of about 150 lines. It has no retry, no timeout
tuning and no handling of "SharePoint is busy". It can overwrite a file in two rare races. It
silently falls back to another library or site when the mapping is wrong. And the documentation
describes a different security model (on behalf of the user) from what the code does (app-only).

None of this needs a big design change. About a week of focused work, plus one decision on the
access model, makes it production-ready for 800-2000 contracts.

## 1. Architecture as built

### 1.1 What lives where

| Thing | Where | Status |
|---|---|---|
| Contract data, workflow, audit trail, users, memberships | Azure SQL | Implemented |
| Extracted document text for search (max 400,000 characters per document) | Azure SQL, table `DocumentTexts` | Implemented |
| Intake uploads before the contract exists, templates, branding | Azure SQL (bytes in rows) | Implemented |
| Data room zip files | Local disk of the web app (`DataRoom:LocalPath`), in every mode | Implemented |
| Contract documents | Test: local disk. Production: SharePoint, one site per entity, one library per department, one folder per contract number | Local: implemented and tested. SharePoint: written, never run |
| Pointer from SQL to the file | `ContractDocument.StoreKey`, one string: `sp:{driveId}:{itemId}` | Implemented |

What is **not** stored about a document: site id, web URL, eTag/cTag, content hash, SharePoint
version, folder id. The contract row stores no folder id either.

### 1.2 The pieces (verified in code)

- **`IDocumentStore`** has four methods: save, open for reading, delete, get folder URL.
  `DeleteAsync` exists but nothing calls it. The app never deletes or moves a file in SharePoint.
- **`SharePointDocumentStore`** calls Microsoft Graph v1.0 directly with `HttpClient`.
  - Site: looked up by entity *name* in configuration (`SharePoint:Sites`), else
    `SharePoint:DefaultSiteId`.
  - Library: lists the site's libraries on every call and picks the one whose name equals the
    department name. If none matches it logs a warning and uses the first library returned.
  - Upload: one `PUT .../root:/{contractNo}/{fileName}:/content` (simple upload, path-based). The
    folder is never created explicitly. Version 2 and later of the same name are stored as
    `name v2.ext`.
  - Download and delete: by ID, `/drives/{driveId}/items/{itemId}`.
  - Folder link ("Open in SharePoint"): path-based lookup of the folder's `webUrl`.
- **Auth to Graph**: app-only (client credentials with a client secret), token cached in memory.
  No on-behalf-of-user flow, no managed identity or certificate.
- **HTTP client**: `services.AddHttpClient()` with no configuration. The named clients `graph`
  and `token` have no retry, no `Retry-After` handling, no circuit breaker, no `User-Agent`, and
  the default 100-second timeout.
- **Permissions**: the app sets no permissions in SharePoint at all. Not on sites, libraries,
  folders or files. Access is decided only in the app, from SQL.
- **Directory**: `GraphDirectoryService` imports the first 999 enabled member users as Readers.
  It does not read groups. Department and entity-management memberships are rows in SQL,
  maintained by hand in the admin screens. There is no Graph call in the access check and so
  nothing to cache.
- **Sync / reconciliation between SQL and SharePoint**: none. No delta query, no change
  notifications, no nightly check.
- **Release 4 mapping (in progress)**: columns `SharePointSiteUrl`, `SharePointSiteId`,
  `ManagementGroupId` on legal entities and `LibraryName`, `GroupId` on departments exist, with
  DTOs. The "test link" and "access mapping" endpoints from `CONTRACT_V4.md` section 7 were not
  in the API yet when I read it, and the document store still reads its sites from configuration,
  not from these columns.

### 1.3 Request flows

**Upload** (`POST contracts/{id}/documents`)

1. App checks access in SQL, reads the whole file into memory (max 25 MB), checks type and
   content.
2. Works out the version number from SQL.
3. Graph call 1: list the site's libraries. Graph call 2: PUT the file.
4. Writes the `Document` row and the audit row to SQL.
5. Extracts text from the bytes it already holds and saves it to SQL.
6. Returns the contract detail, which triggers the two folder-link calls below.

The file is written to SharePoint **before** the SQL row. If step 4 fails, the file stays in
SharePoint with no row (an orphan). The reverse (row without file) cannot happen on upload.

**Open / download** (`GET documents/{id}/content`)

1. App checks access to the contract in SQL.
2. Graph: GET the content by ID. The whole file is buffered in memory, then sent to the browser.
3. Audit row "document.opened".

The file always travels through the API. The user never talks to SharePoint, so this works even
if the user has no SharePoint permission.

**Search**: SQL only (`DocumentTexts`). No SharePoint call. Implemented.

**Access check**: SQL only. No SharePoint or Graph call. Implemented.

**Contract page** (`GET contracts/{id}`): two Graph calls on every load (list libraries, get
folder `webUrl`) to build the "Open in SharePoint" link. Errors are swallowed and the button is
hidden. Nothing is cached.

**Background**: the hourly job indexes up to 100 documents that have no text yet. It downloads
each from SharePoint. The approval log is written to SharePoint inside the final approval step.
The data room export downloads every selected document, one after the other, inside the request.

### 1.4 Implemented / never run / missing

| Implemented and tested (Local mode) | Written, never run (SharePoint mode) | Missing |
|---|---|---|
| Access model in SQL | Graph upload, download, folder link | Retry, `Retry-After`, circuit breaker, timeouts |
| Search on SQL text | App-only token | "SharePoint is busy" message (today: generic 500) |
| Versioning by name (`v2`, `v3`) | User import (first 999) | Explicit, idempotent folder creation |
| File type and size checks | | Moving files when entity/department changes |
| Audit of upload and open | | Reconciliation job, health indicator |
| ID-based store key | | Group-to-membership sync |
| | | Any SharePoint permission management |
| | | Upload session for large files |

## 2. Risks

Likelihood is judged for this scale: 9 sites, 13 libraries each, up to about 2,000 folders and a
few thousand files in total.

### 2.1 Broken links

**a. Users rename, move or delete in SharePoint.**
Rename or move inside the same library: downloads keep working, because the app addresses the
file by ID and the ID survives rename and move (Microsoft docs [5]). Move to another library or
site, or delete: the download fails. The ID is only unique within one library (docs [6]) and
Graph cannot move items between libraries (docs [7]); that a cross-library move produces a new
ID is my inference. Likelihood: high if users have edit rights in the libraries, near zero if
they do not. Current behaviour: a generic "Something went wrong" (HTTP 500), because a Graph 404
is not mapped; only the Local store gives the friendly "file is missing" 404. Fix: remove users'
edit rights (see 2.4), map 404 to "This file is no longer in SharePoint", and let the nightly
check list missing files. Effort S.

**b. "Open in SharePoint" is path-based.**
The folder link is found by contract number every time. If someone renames the folder, or the
contract moved to another department, the link silently disappears or points to a new, half-empty
folder. Likelihood: medium. Fix: create the folder once, store its `driveId` and folder item ID
on the contract, and resolve the link by ID. Effort S-M.

**c. Contract moved to another entity or department.**
Old files stay where they are (`STATUS.md` says so) and still download, because the key holds
the old library. New uploads go to the new site/library. The contract's files are then split
over two folders, and the old folder keeps the old department's SharePoint permissions.
Likelihood: a few times a year. Fix for now: show a warning in admin ("files in another
library") via the reconciliation report and move by hand. Automating the move means copy, verify,
update keys, delete: only worth it if it turns out to be frequent. Effort S for the report, M-L
for automation.

**d. Site address change.**
Not a problem for downloads: no URL is stored, only IDs. The release 4 `SharePointSiteUrl` would
go stale, but the stored site ID should keep working (inferred, not verified in docs). Low.

**e. Department renamed in the app.**
The library is matched by department *name*. After a rename no library matches and uploads
silently go to the site's first library (see 2.4 c). Release 4's `LibraryName` column addresses
this once the store uses it. Fix: resolve and store the library's drive ID at "test link" time
and use that. Effort S, part of release 4.

### 2.2 Throttling and outages

**What Microsoft does (docs [1]).** SharePoint answers HTTP 429 or 503 with a `Retry-After`
header. Honouring it is required; ignoring it leads to more throttling and, in the extreme, a
block of the app. For a tenant with up to 1,000 licences one app gets 1,250 resource units per
minute and 1.2 million per day; a download costs 1 unit, an upload or a list call 2, any
permission call 5. Traffic without a `User-Agent` is given lower priority. Throttling is more
likely in business hours.

**Likelihood.** Normal use is tens of calls per minute, far below the limit. Three things can
still hit it: the data room export (hundreds to thousands of downloads back to back), a first
migration of existing contracts, and short tenant-wide 503 incidents, which happen a few times a
year regardless of what this app does.

**Current behaviour (verified in code).**

- No retry at all. Any 429/503 becomes a generic 500 "Something went wrong".
- The default timeout is 100 seconds per call. The contract page makes two Graph calls and
  swallows errors, so when SharePoint hangs, every contract page can take minutes even though
  none of its data comes from SharePoint.
- The final approval step writes the approval log to SharePoint first. During an outage the last
  approver cannot approve.
- The data room export fails entirely on the first failed download.
- The hourly text backfill marks a document as "Failed" on any download error and never retries
  it. A short outage can leave documents permanently unsearchable. This mostly hits main
  documents created through the intake flow and approval logs, because those are only indexed
  by the backfill.

**Smallest fix.**

- Configure the `graph` client once: timeout 15-30 s for metadata calls and longer for content,
  retry 3 times on 429/502/503/504 honouring `Retry-After`, a circuit breaker, and a
  `User-Agent` of the form `NONISV|DAFA|Contracts/1.0`. The .NET standard resilience handler
  does this in a few lines. Effort S.
- Map store failures to HTTP 503 with a plain message: "SharePoint is busy. Your contract data
  is safe; try the file again in a few minutes." Effort S.
- Stop calling Graph on the contract page (store the folder link, 2.1 b). Effort S.
- Make the backfill retry failed downloads a few times (keep "Failed" only for unreadable
  files). Effort S.
- Write the approval log after the approval is saved, with retry by the hourly job. Effort S-M.

**Proxy or redirect downloads?** Keep the proxy. It is what lets users have no SharePoint
access, keeps the audit complete and gives one place for errors. A redirect to SharePoint's
pre-authenticated download URL is faster but that URL needs no sign-in and lives for about an
hour (docs [6]); it could be forwarded. Not worth it for 25 MB files.

### 2.3 Speed

- **Listing, search, dashboards**: no SharePoint calls. Nothing to do.
- **Contract page**: two Graph calls per load, roughly 0.3-1 s extra (inferred). Fix as in
  2.1 b; then the page makes none.
- **Upload**: one avoidable call (listing libraries) each time. Cache the library ID per
  site + department, or store it (2.1 e). Effort S.
- **Download**: the file is fully buffered in the API before the first byte reaches the browser,
  so a 25 MB file feels slow and uses 25 MB of memory per download. Stream it instead
  (`ResponseHeadersRead`, pass the stream through). Effort S.
- **Upload memory**: the file is copied about four times in memory (roughly 100 MB for a 25 MB
  file). Harmless for a handful of users; removing the extra copy in `SaveAsync` is a one-line
  change.
- **Preview / thumbnails**: not built. Graph offers both, but they add calls and a second access
  path. Not needed now.

### 2.4 Permissions and drift

This is the most important open decision.

**a. The docs and the code disagree on the access model.** `BUILD_BRIEF.md` and `RUNBOOK.md`
sections 5.3-5.4 describe user actions going to SharePoint on behalf of the user, with library
permissions as "a second line of defence". The code is app-only. With app-only, SharePoint
permissions are not a second line of defence for the app: the app can read every file on the
nine sites, and only the app's own access check stands between a user and a file.

**b. Recommendation: make app-only the decision, and give users no direct access.**
With app-only + `Sites.Selected`, the app has access only to the sites it was granted, and
users need no SharePoint permission at all (docs [3]). That gives:

- one access model (the app's), so no drift between two models;
- owner / delegate / responsible / approver exceptions work without item-level permissions;
- no edits, renames, moves or deletes behind the app's back, so far fewer broken links, and the
  "main document is locked after submission" rule cannot be bypassed;
- a complete audit trail (every open goes through the app);
- nothing for Copilot or SharePoint search to surface to the wrong person, because users have no
  access to the sites. This is the key point for contracts.

On-behalf-of-user, by contrast, intersects the app's and the user's rights (docs [3]). An
approver from another department would need a SharePoint permission on that one contract folder.
That means breaking inheritance per folder, each permission call costing 5 units, and a library
limit of 50,000 unique permissions with 5,000 recommended (docs [4]). It is exactly the sprawl to
avoid.

If the business wants some users to browse in SharePoint, give the department groups **read**
on their library and nothing more, accept that the per-contract exceptions then exist only in the
app, and hide "Open in SharePoint" for users who would get "access denied". Never grant edit.

**c. Silent fallbacks put files in the wrong place.** No library named like the department ->
first library on the site. No site for the entity -> `DefaultSiteId`. If libraries have different
readers, an HR contract can land in a widely readable library. Likelihood: high on day one (one
typo in 117 library names). Fix: fail the upload with a clear admin-facing error instead of
falling back. Effort S.

**d. External sharing and sync.** Not handled anywhere (it is tenant configuration). Turn
external sharing off on the nine sites, and disable the OneDrive sync client for the libraries.
Sync is the usual source of mass moves and deletes.

**e. Group membership.** The runbook says membership follows Entra dynamic groups. The code
imports users only, as Readers, first 999, and memberships are edited by hand in the app. Until
release 4 maps groups, a person who changes department keeps their old access in the app until
someone edits it. Medium.

### 2.5 Consistency between SQL and SharePoint

**a. Orphan files** (file without a row): happens when the SQL save fails after the upload, when
two people upload the same name at once, or when contract creation fails. Harmless but untidy,
and never cleaned up. Low.

**b. Overwrite on a name collision (the one real data-integrity risk).** Uploading by path
replaces an existing file of the same name; the Microsoft page describes the result as a "newly
created or updated file" and names no option to refuse (docs [2]). The Local store protects
against this with a random key; the SharePoint store does not, although a comment in
`DocumentWriter` says "the store's unique keys make sure an existing file is never overwritten".
Two cases (inferred from code, not run):

- Two uploads of the same file name to the same contract at the same moment: both write to the
  same path. One request is refused by SQL, but its bytes may be the ones that remain in
  SharePoint under the other's row.
- Two contracts created at the same moment get the same provisional number. Both main documents
  go to the same folder; if the file names match (for example `Agreement.pdf`), both contracts
  point at the same SharePoint file, holding one party's content. The loser is then renumbered,
  so its file also sits in a folder with the wrong number (`STATUS.md` L5 notes the folder half
  of this).

Likelihood: very low at this scale. Consequence: a contract shows the wrong document. Fix:
upload with `@microsoft.graph.conflictBehavior=fail` and treat 409 as "try again"; upload the
intake file only after the contract number is saved. Effort S.

**c. Missing files** (row without file): only through deletion or cross-library moves in
SharePoint, or a restore of one side only. Today nobody would notice until a user clicks.

**d. No stored fingerprint.** The row has size but no hash or eTag, so a file changed directly in
SharePoint cannot be detected, and the search text silently goes stale.

**Smallest fix for a, c, d**: a nightly job that, per library, lists the items (one delta call
per library, docs [8]) and compares with the rows: files without rows, rows without files,
changed content (store the `cTag` at upload), folders in the wrong library. Report in admin; do
not auto-repair. At 117 libraries this is a few hundred calls per night. Effort M.

### 2.6 Large files

The code comment and `STATUS.md` say simple upload works "up to about 4 MB". That is out of
date: Microsoft documents simple upload for files up to 250 MB, and recommends an upload session
for files over 10 MB because it can resume (docs [2], [9]). So 25 MB files should work with the
current single PUT (to be confirmed on first run). An upload session with 5-10 MB chunks is the
more robust choice on a slow link but is not a blocker. Effort S-M, can wait.

Related, verified in code: the request limit is 30 MB and everything is buffered in memory.
Fine for this scale.

### 2.7 Limits

| Limit (docs [4], [5], [10], [11]) | Here | Verdict |
|---|---|---|
| 30 million items per library; list view threshold 5,000 | At most ~2,000 folders per library, in practice a few hundred | No issue. One folder per contract keeps views small. |
| 50,000 unique permissions per library, 5,000 recommended | Zero if app-only | No issue, provided per-folder permissions are avoided |
| Path max 400 characters | Site + library + `C-YYYY-NNNN` + name (max 120) | No issue |
| Not allowed in names: `" * : < > ? / \ |`, leading/trailing spaces, names starting with `~$` or `~`, `_vti_` anywhere, `.lock`, `CON`, `PRN`, `AUX`, `NUL`, `COM0-9`, `LPT0-9`, `desktop.ini` | `SafeFileName` removes the characters, control characters and leading dots | Small gap: reserved names, `~`/`~$` prefix and `_vti_` are not handled. The upload would fail with a generic 500. Add them to `SafeFileName`. Effort S. |
| `#` and `%` in names | Allowed through; the URL is escaped with `Uri.EscapeDataString` per segment | Should be fine; include in the first-run test |

### 2.8 Retention and records

The app has its own retention rules but never deletes a file in SharePoint, so today nothing
enforces them on the files. If Purview retention labels are used: a normal label still allows
edit, rename, move and (by default) delete; marking items as **records** blocks delete, and a
locked record blocks content edits (docs [12]). The app's "new version = new file" pattern is
compatible with records. A future "delete after retention" feature in the app would be blocked
by record labels, by design. Decide who owns retention (app or Purview), not both. One more
note: replacing the content of a file that has a sensitivity label is not supported with app-only
access (docs [2]); the app never replaces content once the overwrite race in 2.5 b is closed.

### 2.9 Token, consent and secrets

- App-only with a **client secret** (verified in code). Secrets expire; when it does, every file
  operation fails with a generic 500. Prefer a certificate or the web app's managed identity
  (managed identities can be granted `Sites.Selected` too); otherwise set an expiry alert.
  Effort S-M.
- `Sites.Selected` gives nothing until an administrator grants the app a role on each of the nine
  sites, and granting needs `Sites.FullControl.All` for the person or script doing it (docs [3]).
  `write` is enough for the app.
- The Microsoft pages for upload and delta do not list `Sites.Selected` in their permission
  tables (docs [2], [8]), while the Selected overview says access follows the grant on the target
  resource (docs [3]). I expect it to work; confirm on first run.
- The runbook asks for `GroupMember.Read.All` and `User.ReadBasic.All`. The code calls `/users`
  with `accountEnabled` and `userType`, which is likely to need `User.Read.All` (inferred).
- The same app registration is used for sign-in and for Graph. That is fine; just make sure no
  *delegated* SharePoint scope is consented, so a user token can never be used against the sites.

### 2.10 Multi-geo

Nothing in the code depends on geography; one Graph endpoint, IDs only. If the tenant is not
multi-geo (the normal case for this size) there is nothing to do. The Tianjin and US entities
make it worth one question to IT. If multi-geo is ever introduced, cross-geo moves are limited
to 15 GB per file (docs [4]) and site moves may change IDs; revisit then.

### 2.11 Backup and restore

SQL has point-in-time restore; SharePoint has versioning and a recycle bin. The two are restored
independently (runbook section 6). After a SQL restore to an earlier point, files uploaded since
are orphans; after a SharePoint restore, rows may point to missing or recreated files. Restoring
a deleted file from the recycle bin keeps the app's link working (inferred: the item comes back
with its ID; verify once). The nightly reconciliation report is the tool that tells you what is
out of step after any restore. No further work recommended.

### 2.12 Tenant move at exit or carve-out

Every `StoreKey` contains tenant-specific IDs. In a tenant-to-tenant migration all drive and
item IDs change, so every document link breaks unless the keys are remapped. What makes the
remap possible is a tenant-independent address stored next to the key: entity, library name and
relative path (`C-2026-0012/Agreement v2.pdf`), plus a content hash to verify. Storing these
three fields at upload costs almost nothing now and turns a migration into a script. Effort S
now, L later without it.

The data room export already gives a tenant-independent copy of files plus a manifest, which is
a workable fallback for a carve-out of one entity.

## 3. Recommendations

"Already done" marks what the code does today.

### Already done (keep)

- Address files by `driveId` + `itemId` rather than by path (download and delete).
- App-only access to Graph (but undocumented as a decision, and with a client secret).
- Downloads proxied through the API with an audit row; no direct links to files.
- Search and the access check run on SQL only.
- A SharePoint failure does not block reading a contract (the link is simply hidden).
- New versions are new files; nothing is edited in place.

### Do now (small, high value)

| # | What | Effort |
|---|---|---|
| 1 | Decide and write down the access model: app-only + `Sites.Selected`, users have no direct (or read-only) access to the nine sites. Correct `BUILD_BRIEF.md` and `RUNBOOK.md` 5.3-5.4. | S |
| 2 | Configure the `graph` HTTP client: timeouts, retry honouring `Retry-After`, circuit breaker, `User-Agent`. | S |
| 3 | Map store errors to a friendly "SharePoint is busy" (503) and "file no longer in SharePoint" (404) instead of a generic 500. | S |
| 4 | Close the overwrite race: `conflictBehavior=fail` on upload; upload the intake file after the contract number is final. | S |
| 5 | Remove the silent fallbacks (first library, default site): fail with a clear message. | S |
| 6 | Create the contract folder once (idempotent: "create, or use the existing one"), store folder `driveId` + item ID on the contract, build "Open in SharePoint" from that. Removes both Graph calls from the contract page. | S-M |
| 7 | Store with each document: site ID, relative path, `cTag` and a SHA-256 hash. | S |
| 8 | Backfill: retry failed downloads instead of marking them failed forever. | S |

### Do before production

| # | What | Effort |
|---|---|---|
| 9 | Run the first-run checklist (section 4) against a test site in the real tenant. | M |
| 10 | Nightly reconciliation report: orphans, missing files, changed files, files in the wrong library; shown in admin. Report only, no auto-repair. | M |
| 11 | Health indicator in admin: last successful Graph call, circuit state, secret expiry date, per-entity link status (fits release 4 "test link"). | S |
| 12 | Release 4: have the store use the saved site ID and library drive ID instead of names and configuration. | S-M |
| 13 | Replace the client secret with a certificate or managed identity, or at least alert before expiry. | S-M |
| 14 | Stream downloads; write the approval log after the approval is saved; throttle the data room export (a short pause per file, retry per file). | S-M |
| 15 | Complete `SafeFileName` for SharePoint's reserved names. | S |
| 16 | Tenant settings with IT: external sharing off and sync disabled on the nine sites, versioning on, decision on Purview labels. | S |
| 17 | Upload session (chunked) for files over 10 MB. | S-M |

### Not worth it at this scale

- On-behalf-of-user access with per-folder SharePoint permissions that mirror the app.
- Change notifications (webhooks) from SharePoint: a nightly delta check is enough.
- Automatic moving of files when a contract changes entity or department: report and handle by
  hand until it proves frequent.
- Redirecting browsers to pre-authenticated SharePoint download URLs, a CDN, or a file cache.
- Thumbnails and in-app Office preview through Graph.
- Using SharePoint search or SharePoint metadata columns for contract data.
- Switching to the Graph SDK: the handful of REST calls is easier to read and control.
- A message queue / outbox for uploads: upload-then-save plus the nightly report is enough.

## 4. First run against a real tenant

Use one test site with two libraries before touching the nine real sites.

1. App registration: `Sites.Selected` (application) consented; no delegated SharePoint scopes.
2. Grant the app `write` on the test site; confirm a call to another site is refused.
3. Confirm a normal user with **no** SharePoint permission can upload and download through the
   app, and gets "access denied" when opening the site directly.
4. Upload a small PDF to a contract with no folder yet: is the folder created? (The code relies
   on Graph creating it implicitly, which the docs do not promise.)
5. Upload 5 MB, 12 MB and 25 MB files; note the time for each.
6. Upload the same file name twice (expect `name v2.ext`), then twice at the same moment.
7. File names with `#`, `%`, `&`, `'`, Danish, Polish and Chinese characters, a 120-character
   name, and `~$test.docx`.
8. Download; then rename the file in SharePoint and download again (must work); move it to
   another folder in the same library (must work); move it to the other library (expected to
   fail: check the message); delete it (check the message); restore it from the recycle bin
   (check that it works again).
9. Department with no matching library, entity with no site: check that the upload is refused,
   not redirected.
10. "Open in SharePoint" as a user with and without site access.
11. Approve a contract to the end; check the approval log file arrives.
12. Create a contract through intake and check the main document becomes searchable.
13. Data room export of 200+ documents; watch the log for 429/503.
14. Force a failure (wrong site ID, expired secret): check what the user sees and what is logged.
15. Run the directory sync; check the Graph permission it really needs and that more than 999
    users are handled or the limit is acceptable.
16. Check the App Service log shows the Graph request ID on errors (needed for Microsoft support).

## 5. Sources (Microsoft documentation, read 2026-11-22)

1. Throttling, `Retry-After`, resource units, `User-Agent`:
   https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online
2. Simple upload (250 MB limit, "created or updated", sensitivity-label note):
   https://learn.microsoft.com/en-us/graph/api/driveitem-put-content
3. `Sites.Selected` and the other "Selected" permissions:
   https://learn.microsoft.com/graph/permissions-selected-overview
4. SharePoint limits (items, unique permissions, path length, cross-geo):
   https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits
5. Addressing items by ID or path; reserved characters:
   https://learn.microsoft.com/en-us/graph/onedrive-addressing-driveitems
6. driveItem resource (`id` unique within the drive, download URL lifetime, `cTag`):
   https://learn.microsoft.com/graph/api/resources/driveitem
7. Move a driveItem (not between drives):
   https://learn.microsoft.com/en-us/graph/api/driveitem-move
8. Delta query for a drive:
   https://learn.microsoft.com/en-us/graph/api/driveitem-delta
9. Upload session (chunk rules, over 10 MB, conflict behaviour):
   https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession
10. Invalid file names and characters:
    https://support.microsoft.com/office/restrictions-and-limitations-in-onedrive-and-sharepoint-64883a5d-228e-48f5-b3d2-eb39e07630fa
11. List view threshold:
    https://support.microsoft.com/office/B8588DAE-9387-48C2-9248-C24122F07C59
12. Retention labels and records, allowed and blocked actions:
    https://learn.microsoft.com/en-us/purview/records-management

Not found in the documentation and therefore to be tested: implicit folder creation on
path upload, whether a cross-library move issues a new item ID, whether a site address change
keeps the site ID, whether a recycle-bin restore keeps the item ID, and `Sites.Selected` on the
upload and delta endpoints. The page for downloading content could not be retrieved; the
download-URL facts come from source 6.
