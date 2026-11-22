# Frontend review (independent QA), 2026-11-22

Scope: `src/web` on branch `build/v1` (phase 1 committed, phase 2 in the working tree). No source file was changed and nothing was committed. Everything below was observed in this run unless it says "code reading".

How it was tested: the demo build (`npm run build:demo`, and a second demo build with `VITE_MOCK_ACCESS_CODE=letmein`) served with `vite preview`, driven by Playwright (Python) with `/usr/bin/google-chrome` (Chrome for Testing 153, headless) at 360, 390, 768 and 1440 px. Motion was left ON (the engineers' own check script forces `reducedMotion: 'reduce'`, which hides defect H3). Scripts and screenshots are in `/tmp/qa` (not in the repo). The mock API only; no real backend.

Severity counts: Critical 1, High 3, Medium 7, Low 15.

## Command results

| Command | Result |
|---|---|
| `npm ci` | Not needed; `node_modules` present (175 packages), Node v22.23.5 |
| `npm run lint` | exit 0, no output |
| `npm test` | exit 0. 10 files, 171 tests passed, 14.2 s. Three jsdom notices "Not implemented: Window's scrollTo()" (not failures) |
| `npm run build` | exit 0. Entry `index-*.js` 250.83 kB (77.72 kB gzip), `components` 95.54 kB, Settings 45.24, Record 24.33, NewContract 22.87, dialogs 15.00, Renewals 9.11, Approvals 8.16, Approval 7.99, Overview 6.14 kB, CSS 50.13 kB |
| `VITE_API_MODE=real npm run build` | exit 0, same chunks. `grep -rlE "Nordpolymer|createSeed|rubber compounds|Sample data" dist` returns nothing (exit 1). No `mockApi-*` chunk in `dist/assets` |
| `npm run build:demo` | exit 0. Adds `mockApi-*.js` 74.51 kB (24.22 kB gzip); "Nordpolymer" occurs only in that chunk |
| Real build served with no API | Shows "DAFA Contracts could not start. The server did not answer." with "Try again" (502 on `/api/dev/config`), as expected |

## (a) Verification of the engineers' claims

| Claim (STATUS.md / DECISIONS.md) | Verdict | Evidence |
|---|---|---|
| Lint clean, 171 tests in 10 files pass, both builds succeed | Confirmed | Table above |
| Real build contains no mock code or seed data; mock is its own ~74 kB chunk in the demo build | Confirmed | grep above; `mockApi-iXvKw8mx.js` 74.51 kB only in the demo build |
| Bundle sizes (entry 251 kB / 78 kB gzip, components 95 kB, per-route chunks) | Confirmed | Build output matches to the kB |
| Headless Chrome at 360/768/1440: no console errors, no horizontal page overflow | Confirmed, with a caveat | My crawl of 24 pages x 3 widths: 0 console errors/warnings, 0 failed requests, `scrollWidth == innerWidth` everywhere. Inner scroll only in the register table (768), audit table (360) and timeline (768). Caveat: their script runs with reduced motion, which is why H3 was missed |
| Access model enforced in the mock (department / entity management / group management / owner / admin / current approver) | Confirmed | CSV export per demo user compared row by row with the rules: Rasmus, Karen, Peter 47/47; Mette, Jonas, Camilla 11 (all Purchasing); Søren 5 (IT); Anna 3 (all DAFA Sverige AB); Thomas 7; Henrik 13; Emma 2 (Quality); Louise 1 (the ERP contract she is approving). No missing and no extra rows. Emma opening a Purchasing contract by URL gets "This contract is not available". A contract created by Jonas in department IT with himself as owner is visible to Jonas and Søren, not to Mette. Linked contracts the user cannot see are left out of the record (Mette sees 2 of 3 links). Global search returns only visible contracts |
| Access-code screen before the demo-user picker; code kept in sessionStorage; mock starts signed out on reload | Confirmed | Empty: "Enter the access code"; wrong: "The access code is not correct"; right code shows the picker; `sessionStorage["dafa.dev.accessCode"]`; reload returns to the picker. "Re-asked when the API stops accepting it" was not tested |
| Dev-mode user switcher to test the access model | **False** | H1: after a switch the shell keeps the previous user's name, roles and navigation until a full reload |
| Phone approval: "sticky Reject/Approve bar; the tab bar is hidden" | **False with default motion** | H3: bar is not pinned and covers the Delegate button. Pinned correctly only with `prefers-reduced-motion: reduce` |
| Register: "5 views, search, entity/department/type/status filters, sort, paging, quick look, CSV export, state in the URL" | Partly confirmed | All work. Not in the URL: the entity filter (M1) |
| Home task actions (renewal dialog, approval review with approve/reject/delegate, mark obligation done) | Confirmed | 1440 and 360: Renew, Mark done, Approve; hero text, task count and sidebar badge update (6 -> 5 -> 4 -> 3) |
| Record: tabs, upload/download, obligations CRUD + complete, links add/remove, edit, submit, mark as signed | Confirmed, except the counterparty field (C1) | 1440 and 360 as Mette, Camilla, Anna |
| `lockedFields` disables inputs with a hint; admins are not locked | Confirmed | Mette: 18 inputs disabled, banner "Approved terms are locked…"; Peter: 0 disabled |
| `newEndDate` for Renew on a fixed-term contract; "Give notice" button | Confirmed | Amendment 1: "Enter the new end date", "must be after 28 Feb 2027", then term becomes 29 Aug 2025 – 28 Feb 2029. GiveNotice on an open-ended contract was not tested |
| Upload type check; Main document replacement hidden for non-admins after approval | Confirmed | `.exe` rejected in the record dialog and in New contract; type list is Appendix/Amendment/Other with the explanation. The 25 MB limit was not tested |
| Approvals: queue, waiting time, deviations, route cards per stage "in parallel", comment, Approve / Reject (comment required) / Delegate | Confirmed at 1440 | Louise delegates to Karen; Rasmus approves (stage stays open); Karen approves the delegated step; stage 3 opens; Karen rejects with a comment; contract is Draft again |
| Delegate picker filtered (not yourself, not the owner, approvers only) | Confirmed in the mock | Options for Louise on the ERP contract: Rasmus, Karen, Mette, Anna, Peter, Thomas, Henrik (see L8) |
| Renewals: entity/department lanes, markers by urgency, today line, arrow/Home/End keys, selected card, list, overdue first and at the left edge, list-only under 720 px, top-bar scope filters the page | Confirmed | 29 markers; ArrowRight/Left/Home/End move in date order and select; overdue "Packaging supply" is first; scope "DAFA Sverige AB" reduces the page |
| Overview: KPI tiles, bars, lists; no-access page for others | Confirmed | Rasmus and Anna (Anna: 2 active, "Across 1 legal entity"); Emma gets the no-access page. That the API is not called was not checked |
| Settings: all ten sections, validation, toasts | Confirmed | Every section exercised at 360 and 1440, including the dialogs the engineers had not checked at 360: rule editor 360x718, template upload 360x632, holiday cover 360x532, all without overflow and with the submit button visible |
| New contract uses `GET reminder-settings` | Confirmed | After saving 120/90/60 in Settings, the form hint reads "120, 90 and 60 days before the notice deadline." |
| 409 handling, Entra redirect and loop guard, `X-Requested-With`, `peek=true` | Not confirmed | Needs the real API or a test hook; not reachable from the mock UI |
| Motion (160 ms, tiles lift 2 px) | Not confirmed | Not measured |

## (b) Defects

### Critical

**C1. The Counterparty field cannot be typed into or changed**
- Route: `/contracts/new` (template path and "Continue without a file"), and Edit details on a record. All viewports. Any user who can create or edit (Jonas, Peter).
- Steps: New contract -> "Supply agreement" (or "Continue without a file") -> click Counterparty -> type "Nordpolymer Compounds ApS" or pick from the list.
- Expected: the text stays in the field.
- Actual: the field stays empty (`input.value === ''` after typing and after `fill`). Continue then reports "Enter the counterparty", so the template path and the no-file path cannot get past step 2. In Edit details as administrator, typing " X" after the name changes nothing. The upload path only works because "Accept suggestion" sets the value through another code path.
- Cause (code reading): `features/ContractForm.tsx` lines 162–167 call `onChange('counterpartyName', name)` and then `onChange('supplierId', …)` in the same event; both parents build the next state from the stale `draft` (`pages/newcontract/NewContract.tsx:339`, `pages/record/dialogs.tsx:44`), so the second call overwrites the first.
- File: `src/web/src/features/ContractForm.tsx` (and the two `change` handlers; use a functional `setDraft` or one combined update).

### High

**H1. Switching demo user leaves the previous user's identity and permissions in the shell**
- Route: any. 1440 and 360. Example: Emma -> Peter, Peter -> Emma, Louise -> Rasmus.
- Steps: user card -> pick another user.
- Expected: the whole app is the new user.
- Actual: toast "You are now Peter Aagaard", data is Peter's (register says 47 contracts), but the user card, greeting ("Good morning, Emma"), navigation and permissions stay Emma's for at least 3 s and until a reload: no Settings link, `/settings` says "You do not have access to Settings", no New contract button. In the other direction (Peter -> Emma) the reader keeps the admin navigation and the Settings pages render. On Approvals the stale user is labelled "You" on someone else's step ("LW You · Legal" next to "RH You · Group CFO").
- File: `src/web/src/shell/UserSwitcher.tsx` (`reset()`: `client.clear()` then `invalidateQueries()` on an empty cache never refetches `me`; `login.onSuccess` should set `qk.me` from the response as `App.tsx` does).

**H2. Sign out does not sign out**
- Route: any. 1440. Rasmus.
- Steps: user card -> Sign out.
- Expected: the sign-in screen.
- Actual: the shell stays as "Rasmus Holm" with "Your summary could not be loaded" / "The contracts could not be loaded" error states on every page. Same cause as H1. (In the mock a reload then signs the remembered user back in, see L10.)
- File: `src/web/src/shell/UserSwitcher.tsx`.

**H3. Phone approval view: Reject/Approve bar is not pinned and covers the Delegate button**
- Route: `/approvals`, one approval picked. 360 and 390 px. Rasmus, Louise. Default motion settings.
- Expected (PhoneApprove.dc.html): white bar with Reject/Approve fixed to the bottom of the screen.
- Actual: the bar sits at the end of the page (top at y = 1456 of an 844 px viewport), two screens down, with the tab bar hidden. Scrolled to the end, it overlaps the comment card: comment input at y 589–633, bar at 622–703, Delegate at 645–689 and `elementFromPoint` on Delegate returns the Approve button. Delegate cannot be tapped and the comment field is half covered.
- Cause (measured): `.approvals__body` keeps `transform: matrix(1,0,0,1,0,0)` from the `rise` animation (`animation-fill-mode: both`), which makes it the containing block for `position: fixed`. With `prefers-reduced-motion: reduce` the bar is at 763–844 and Delegate is clickable.
- File: `src/web/src/styles/screens.css` (`.decide__actions` at ~1070, and the `rise` animation: use `animation-fill-mode: backwards` or animate opacity only on this container). Add bottom padding to `.decide` equal to the bar height.

### Medium

**M1. Entity filter is not in the URL and is lost on reload**
- `/contracts`, all widths, Rasmus. Choose Entity "DAFA Sverige AB" + Department "Purchasing": URL is `/contracts?department=…` only. Reload: the entity filter is gone (1 row becomes 11). A shared link shows a different list. File: `src/web/src/app/context.tsx` (ScopeProvider) / `pages/Register.tsx`.

**M2. Register at 768 px: Status column is cut off**
- `/contracts`, 768, Rasmus. The table is 605 px wide in a 504 px card; the Notice pill touches the card edge and Status is only reachable by horizontal scrolling with no visual cue. The top bar also wraps to two rows (132 px high). Expected: Status visible (for example fold it into the Contract cell under 1000 px, as the phone cards do). File: `src/web/src/styles/pages.css`, `pages/Register.tsx`.

**M3. Renewals offers "Record decision" / "Decide" for contracts the user may not decide**
- `/renewals`, all widths, Rasmus. Every row and the selected card have the button; for "Packaging supply" the dialog only says "Only the owner, delegate or responsible person on an active contract can record the renewal decision." with a disabled Save. Expected: no button (or "Open contract") on those rows. File: `src/web/src/pages/renewals/Renewals.tsx` (needs `canDecideRenewal` on the `renewals` rows, or hide after the peek).

**M4. Renewals at 1440x900: selecting a marker shows nothing in the viewport**
- The timeline card is 688 px high (lanes 61–112 px), so the selected-contract card starts at y = 916. Clicking or arrowing through markers changes a card the user cannot see. Expected (Renewals.dc.html): the navy card directly under a compact timeline. Fix: lane height 44 px with 28 px markers (about 420 px for nine lanes), or make `.selected-card` `position: sticky; bottom: 16px`. File: `src/web/src/styles/screens.css`.

**M5. Timeline keyboard: 29 tab stops**
- `/renewals`, 1440. Every marker has `tabindex="0"`, so Tab has to pass all 29 before reaching the selected card, although arrow keys already move between them. Expected: roving tabindex (only the selected marker is 0). File: `src/web/src/pages/renewals/Renewals.tsx`.

**M6. Edit details on a phone opens scrolled past the "terms are locked" notice**
- Record -> Edit details, 360, Mette. Focus goes to the first enabled field (Owner), so the sheet opens at "Legal entity / Department / People" and the amber notice and the whole Contract section are above the fold. Expected: sheet opens at the top (focus the dialog title or the close button when the first fields are locked). File: `src/web/src/components/Modal.tsx` / `pages/record/dialogs.tsx`.

**M7. Register subtitle always says "across 9 legal entities"**
- `/contracts`, any width. Emma: "2 contracts you can see, across 9 legal entities" (both are DAFA A/S); Louise: "1 contract you can see, across 9 legal entities". Overview gets it right for Anna ("Across 1 legal entity"). File: `src/web/src/pages/Register.tsx`.

### Low

- **L1.** `/contracts?page=99` shows "No contracts yet. Contracts you have access to show up here." Expected: clamp to the last page. `pages/Register.tsx`.
- **L2.** Footer captions that do not fit the state: "Select a marker to see the contract." on phone Renewals (no timeline), "Pick a contract in the queue." with an empty queue, "Select a row to preview it." on the phone register (no quick look).
- **L3.** Phone register cards are buttons named "Preview <title>" but open the record. `pages/Register.tsx`.
- **L4.** Phone toasts (bottom 84 px, z-index 80) lie over bottom-sheet content and can stack three high for 4.5 s (seen over the deviations list in the review sheet). `styles/components.css`.
- **L5.** 11 px text: small avatar initials (`.avatar--sm`) and the "PDF" file tile. The brief says never below 12 (the register design itself uses 11 px initials).
- **L6.** Mark as signed with a future date: only a toast ("The signing date cannot be in the future"), no inline error. Renew with a date before the end date: inline error and a toast with the same text.
- **L7.** Validation summary grammar: "One field needs your attention. They are marked in red below."
- **L8.** Delegate picker offers people who already approve the same contract (Rasmus in the same stage, Karen in the next).
- **L9.** Seed: C-2024-0187 shows "Approved 9 Feb 2024", "Approved by all approvers" and an "Approval log.pdf", but its Approvals tab says it was registered without an approval.
- **L10.** Mock: after Sign out a reload signs the remembered user in again.
- **L11.** No "skip to content" link; the first seven Tab stops are the sidebar on every page.
- **L12.** A locked Counterparty field still shows "Pick a supplier from the list or type a new name."
- **L13.** "Submit for approval" on a draft record submits at once, with no confirmation and no route preview (the New contract flow shows the route first).
- **L14.** Timeline track scrolls sideways by 12 px at 1440 (clientWidth 888, scrollWidth 900).
- **L15.** Disabled-input placeholder #6b7682 on #f4f6f9 is 4.27:1.

## (c) Design fidelity

Measured against `docs/design/*.dc.html` (rendered next to the app and read for the inline values). The token work is accurate: sidebar 224 px, #fff with #e6ebf1 border, padding 24/14; nav items 14 px, 8/12 padding, radius 10, active #00456c on #e6f0f8; top bar 14/32 padding; h1 30/700/-0.02em; h2 19/700; hero radius 24, padding 24, 28 px headline, inner tiles rgba(255,255,255,.10) radius 18 padding 20; route tiles radius 16, 16/18 padding, 36 px icon radius 10, exact card shadow; segmented track #e9eef4 radius 10, buttons 34 px radius 8; pills 12/700, 4/10, radius 999; quick look padding 18, radius 18, 30 px number in a #f4f6f9 radius-14 box; record hero radius 20, 88 px ring; lifecycle bars 6 px; Inter loaded, tabular numbers on. Text contrast: every text/background pair on 12 pages at 1440 and 390 is at least 4.5:1 except L15 and disabled buttons (#83888f on #fff, 3.57:1, exempt). Focus rings are 2 px #00456c with 2 px offset on every control tabbed through; dialogs trap focus (25 Tabs stayed inside), close on Escape and return focus to the trigger; record tabs and yes/no toggles use roving tabindex; Ctrl+K search works with arrows and Enter.

What a designer would send back:

**Home, 390 px (Phone.dc.html): not the approved layout.** The design is task-first: date + "My work" + 40 px avatar, a compact hero (padding 20, radius 20, 24 px headline, one line "1 renewal · 1 approval", no button, no KPI tiles), the 44 px segmented control, and the first task card at about y = 280. The app shows a search row, greeting, a 340 px hero with a button and two KPI tiles, then three 68 px route tiles; "My work" starts at y = 760 and no task is visible in the first 844 px. Fix under 720 px: drop the hero button and KPI tiles (hero padding 20, radius 20, headline 24 px), move the three route tiles below the task list, segmented buttons 44 px. Task icon tiles are always #e6f0f8/#00456c; the design tints them by urgency (#fbe9e7/#9b2c1f, #fdf1d6/#7a5300).

**Approval view, 390 px (PhoneApprove.dc.html).** Besides H3: the design has no top bar on this view and fits the decision in one screen (2x2 term tiles, deviations, a one-sentence route "Department head approved · You and Legal now · CEO next", pinned bar). The app keeps the search row, adds the summary paragraph and a fifth tile, and renders the route as cards in two 150 px columns where step 1 wraps to nine lines ("Department head (substitute: Søren Dahl is the owner or submitter) · Approved 20 Nov 2026"). Fix: hide `.topbar` when `.approvals--picked` under 720 px; route as a single column or the one-line summary; move the substitute explanation to a second line capped at two lines.

**Home, 1440.** Faithful. Only the urgency tint of the task icon tile is missing (measured rgb(230,240,248) on a red "Notice in 27 days" row).

**Contracts, 1440.** Quick look and table styling match. Issues: (1) the Contract column is 255 px of 767, so titles wrap to two lines and the meta line to two or three; rows are 76–96 px and 15 rows take 1,170 px. Give the column at least 40% and keep the meta on one line with ellipsis (row height about 56 px). (2) The filter chips break into two ragged rows (search/Entity/Department right-aligned on row 1, Type/Status left-aligned on row 2). Put all four chips on one row under the views. (3) The design has Export and New contract together on the h1 row; with the global top bar, Export now sits alone at the right, 40 px below New contract. Align it to the h1 baseline as a 36 px secondary button or move it into the filter row.

**Contract record, 1440.** Faithful, including hero buttons and header actions for the owner. Issues: the key-terms grid has 11 items in 3 columns, so the last row has an empty cell and two orphan hairlines; drop the bottom border on the last row and fill to 12 (for example add "Contract no."). "Signed: 14 Feb 2024, signed by both parties" wraps and makes one row 20 px taller than the others; shorten to "14 Feb 2024 · both parties".

**New contract, 1440.** Faithful. Step 4 has two primary "Submit for approval" buttons (top bar and under the summary) while "Save as draft" exists only in the page; keep both actions in the top bar or both in the page. The design's value-band chips in the Approval route card are replaced by a sentence. Error toasts cover the "What differs" card for 8 s on this step.

**Approvals, 1440.** Faithful. Issues: five term tiles in a four-column grid leave "Change of control" alone on a second row; use `repeat(5, 1fr)` at this width or `auto-fit, minmax(150px, 1fr)`. Route cards stretch to the tallest (step 1 has four lines), so the step 3 card is 130 px high with one centred line; use `align-items: start` on the route grid and top-align the avatar.

**Renewals, 1440.** Deviations are recorded in DECISIONS.md. Remaining issue is M4 (timeline 688 px high pushes the selected card below the fold). The overdue marker sits on the border between the lane-label column and the first month; give it a 12 px inset or its own narrow "Overdue" column.

**Overview, 1440.** The third KPI label wraps to two lines, so its number sits 18 px lower than the other four; fix the label to one line (`white-space: nowrap`, shorter label "Notice within 90 days") or give labels a fixed two-line height. The left column ends 120 px above the right one, leaving a hole before "Expiring without renewal"; set `align-items: stretch` on the row or let the bar list grow.

**Settings.** Consistent with the rest. On a phone the rule dialog's three-line description stays fixed in the header and takes about 120 px of a 718 px sheet; let it scroll with the body.

**768 px.** Top bar wraps to two rows on every page (132 px). Reduce the search max-width or hide the "Ctrl K" hint and scope label so the row fits.

## (d) Not tested, and why

- Anything against the real .NET API (no backend here): Entra redirect and loop guard, 401/403/409 handling, `X-Requested-With`, `peek=true` side effects, real file download/upload, CSV from the server.
- New contract by template or without a file beyond step 2, and saving a draft from those paths: blocked by C1. Only the upload path was completed (to "submitted for approval") at 360, 768 and 1440.
- Multi-user flows were run through the user switcher within one page load (mock state resets on reload); because of H1 the shell showed the wrong user while the data was the right user's.
- GiveNotice on an open-ended contract; the 25 MB upload limit; holiday-cover rerouting of a live step; the "access code no longer valid" path.
- Notifications outbox with content: "Run jobs now" returned "0 reminders, 0 escalations, 0 obligation reminders, 0 contracts expired", so the table stayed empty.
- Audit contract filter: suggestions appear on typing; ArrowDown + Enter did not select one; selection by mouse or Tab was not completed.
- Firefox, Safari, real phones, screen readers, hover/press motion timing, print.
- 768 px got the route crawl, register, renewals and the New contract upload path; record dialogs and Settings flows were run at 360 and 1440 only.
