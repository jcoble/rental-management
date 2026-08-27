# Exploratory Test Report: Mobile M01 — Today screen, navigation, Rentals
Date: 2026-08-27
Tester: m01 (Flutter app on the owner's Galaxy S22+, adb 100.73.198.92:34827, debug build of main 1ff44ff9)
Duration: ~45 minutes

## Scenario
Open the app on the phone, read today's summary, move through the five bottom tabs, and get from Rentals to a unit's detail — checking that numbers match the API and that nothing reads like developer output.

## Summary
The Today screen, the five tabs, the Rentals drill-down (property to units to unit detail) and the "Example data" go-live guard all work, and the unit/property figures I cross-checked (Eastland 8-Plex: 8 units, 7 occupied, Unit 1 $975/mo, 2 bed 1 bath) match `/api/v1/units/page` and `/units/10/dashboard` exactly. The problems are around the edges of navigation and wording: the Android back button walks the landlord out of the app instead of back to Today; a Today item can silently disappear or become untappable; the Money tab shows "Rent still owed" as a negative number while "Who's behind" on the same tab shows four tenants owing $4,100; a repair status renders as the raw enum `InProgress`; and every list's search box does nothing until you find the keyboard's search key.

Note on money figures: two web testers were recording payments against the same sandbox portfolio during this run, so cash and past-due totals moved between screenshots. I only report money findings that reproduce against a single API payload, and I say so where the data was moving.

## Bugs Found

### BUG-1: Android back exits the app from any tab other than Today
**Severity:** High
**Location:** Any bottom tab except Today (reproduced from Rentals and from Money)
**Expected:** From a non-start destination, system back returns to the start destination (Today). This is the standard Android/Material bottom-navigation contract, and it matters more here because the app moves the tab for you: tapping an overdue-rent item on Today switches you to Money without you choosing it.
**Actual:** Back pops any pushed detail first (correct), then the next back closes the app to the launcher. The Today tab is never restored.
**Evidence:**
- Path A: Today → tapped "Rent overdue — Marcus Williams" → landed on Money ▸ Insights ▸ "Who's behind" (`m01-today-tap-overdue.png`) → back → Money ▸ Insights Overview (`m01-back1.png`) → back → Samsung launcher (`m01-back2.png`).
- Path B (minimal): relaunch → tap Rentals tab → single back → `dumpsys window` reports `mCurrentFocus=com.sec.android.app.launcher/...LauncherActivity`.
**Code Reference:** `mobile/lib/features/home/mobile_domain_hub.dart:315-327` (`_handleSystemBack`) — when the content navigator and the root navigator both cannot pop, the handler returns without doing anything, so the platform closes the activity. `mobile/lib/features/home/home_shell.dart` has no `PopScope` at the shell level and keeps no tab history.
**Suggested Fix:** In `HomeShell`, wrap the shell in a `PopScope(canPop: false)` whose handler, once the active tab's navigator reports it cannot pop, sets `_selectedIndex` back to 0 (Today) and only allows the app to close when the user is already on Today.
**Why This Matters:** The landlord taps one thing on Today, is moved to a tab they did not pick, presses back twice out of habit, and the app is gone. On a phone this is the single most-used gesture in the app.

### BUG-2: "Rent still owed" shows a negative dollar amount and contradicts "Who's behind" in the same tab
**Severity:** High
**Location:** Money ▸ Insights ▸ Overview ("Rent still owed" tile)
**Expected:** A tile a landlord reads as "how much rent am I still owed" should agree with the "Who's behind" screen one tap away, and can never be negative — owing a negative amount is meaningless to the reader.
**Actual:** The tile read **-$1,250.00** while, in the same portfolio at the same moment, `/accounting/past-due` returned 4 tenants owing $4,100.00 and the same `money-position` payload carried `pastDueAmount: 4100.0`. Earlier in the session the tile read `$25.00` under the same conditions.
**Evidence:** `m01-rent-still-owed.png` (tile shows `-$1,250.00`); `m01-back1.png` (same tile showing `$25.00` at 4:45). API at 08:50 UTC: `{"rentStillOwed": -1225.0, "pastDueAmount": 4100.0, "pastDueCount": 4}` from `GET /api/v1/accounting/money-position`; `GET /api/v1/accounting/past-due` returned `totalCount 4, totalPastDueAmount 4100.0`.
**Code Reference:** `RentalCommand.Api/Services/Domain/AccountingLedgerReadModelService.cs:429-434` — `RentStillOwed` is the *net* balance of the `tenant-accounts-receivable` system account across all tenants, so prepaid/credit balances net against genuine arrears and can drive the figure to or below zero. Rendered raw at `mobile/lib/features/money/money_screen.dart:671`.
**Suggested Fix:** Have the tile show arrears only — reuse the `PastDueAmount` already present on the same `MoneyPositionResponse` for the "Rent still owed" tile, and if the net receivable figure is still wanted, give it its own row with an honest label ("Tenant balances, net of credits").
**Why This Matters:** This is the landlord's collections number. A negative or near-zero figure next to five red overdue rows on Today tells them to stop chasing money they are actually owed.

### BUG-3: The Today list silently drops items past the fifth, so a lease-expiring warning never appears
**Severity:** Medium
**Location:** Today screen, "Today" section
**Expected:** Either every briefing item the server returned is shown, or the list says how many more there are and offers a way to see them.
**Actual:** `GET /api/v1/ai/briefing` returned 6 bullets — five critical "Rent overdue" items plus one warning, "Lease expiring — Unit 2, Ends Oct 1 (35 days left)". The screen renders only the first five; the lease warning is dropped with no count, no "see all", and nothing to tap.
**Evidence:** `m01-launch.png` and `m01-today-scroll1.png` show exactly five overdue rows and then the "Your money" card. Briefing payload (curl, 08:43 UTC) contained a sixth bullet `{"title": "Lease expiring — Unit 2", "category": "LeaseExpiring", "entityType": "LeaseAgreement", "entityId": 10}`.
**Code Reference:** `mobile/lib/features/home/home_shell.dart:3023` — `final bullets = briefing.bullets.take(5).toList();`
**Suggested Fix:** Keep the cap but sort by severity before truncating and append a "3 more today" row that opens the full briefing, so a warning is never hidden behind five items of the same category.
**Why This Matters:** Five late-rent rows crowd out the one item with a deadline. A renewal the landlord never sees becomes a vacancy.

### BUG-4: A "Lease expiring" item on Today is not tappable — the entity type the server sends is not in the mobile switch
**Severity:** Medium
**Location:** Today screen, briefing rows
**Expected:** Every briefing row that names a record opens that record, as the surrounding code intends ("still actionable, never a dead end", `home_shell.dart:3251`).
**Actual:** The server tags lease-expiry bullets with `entityType: "LeaseAgreement"`. The mobile mapping handles `WorkOrder`, `Payment`, `LeaseManagement`, `Tenant`, `Appointment` and `Inspection`, and falls through to `default: return null` for `LeaseAgreement`, which renders the card with no chevron and no tap handler. (`TenantAccount`, also sent by the server, is only saved by the `RentLate` special case above the switch.)
**Evidence:** Briefing payload bullet 6 has `entityType: "LeaseAgreement"`. Not reproducible on screen only because BUG-3 truncates it away — verified by reading `_targetFor`.
**Code Reference:** `mobile/lib/features/home/home_shell.dart:3232` (`case 'LeaseManagement':`) and `:3273` (`default: return null`); server side, the bullet is emitted with `EntityType = "LeaseAgreement"` by `RentalCommand.Api/Services/Domain/DailyBriefingService.cs`.
**Suggested Fix:** Add `case 'LeaseAgreement':` alongside `case 'LeaseManagement':` in `_targetFor` (both should route to the lease/unit detail), or normalise the entity type once at the server so mobile only ever sees one spelling.
**Why This Matters:** The one item on Today with a deadline is also the one item you cannot act on.

### BUG-5: "Legal / notice: Active" is shown when there is no legal notice at all
**Severity:** Medium
**Location:** Rentals ▸ property ▸ unit ▸ Summary (the conditions panel)
**Expected:** A row labelled "Legal / notice" should describe notices. With zero open notices it should read something like "No notices".
**Actual:** Unit 1 of Eastland 8-Plex shows **Legal / notice: Active** while the API reports `legalNoticeCondition: {"status": "Active", "openNoticeCount": 0, "agreementStatus": "Active"}` — the row is showing the *lease agreement's* status under a legal-notice label.
**Evidence:** `m01-unit1.png`; `GET /api/v1/units/10/dashboard`.
**Code Reference:** `RentalCommand.Api/Services/Domain/UnitDashboardService.cs:196-203` — with no open notice the status falls back to `unitRow.AgreementStatus` ("Active"); rendered at `mobile/lib/features/units/unit_command_center_screen.dart:794-798`.
**Suggested Fix:** In `UnitDashboardService`, return a notice-specific value when `OpenNoticeCount == 0` (e.g. `"NoNotices"` when an agreement exists) and add `'NoNotices': 'No notices'` to `_plainEnglishOverrides`; leave the agreement status to the lease rows that already show it.
**Why This Matters:** "Legal / notice: Active" on a tenant who is 26 days behind reads as "an eviction notice is running". A landlord could skip the notice they still need to serve, or think they are further along than they are.

### BUG-6: Repair status renders the raw enum "InProgress"
**Severity:** Low
**Location:** Work ▸ Repairs (status chips on every row)
**Expected:** Plain landlord English. The project already has the mapping — `plainEnglishLabel` carries `'InProgress': 'In progress'`.
**Actual:** Chips read `InProgress` (two rows visible: "Roof shingle repair", "Exterior lights not working"). The category chip has the same problem waiting for it — `/accounting/summary` reports categories like `CleaningMaintenance`, and `_CategoryChip` also prints the raw value.
**Evidence:** `m01-work.png`.
**Code Reference:** `mobile/lib/features/maintenance/work_orders_screen.dart:510` renders `Text(status)` directly; the helper is at `mobile/lib/core/presentation/plain_english_labels.dart:11`.
**Suggested Fix:** Wrap both chip labels: `Text(plainEnglishLabel(status))` at `work_orders_screen.dart:510` and the same in `_CategoryChip`.
**Why This Matters:** The brief for this product is explicitly "no raw enum names". It reads as unfinished software to the one person who has to trust it.

### BUG-7: Typing in a list's search box does nothing until you press the keyboard's search key
**Severity:** Medium
**Location:** Rentals ▸ Properties search (and every list built on the shared control: Units, Tenants, Repairs, Messages, Inspections, Applications)
**Expected:** Typing filters the list, as it does on the web app; at minimum the screen should show that a search is pending.
**Actual:** With "east" in the box, the list still showed Clintonville Townhome and Dublin Single Family nine seconds later and **no request was issued** to the API. Pressing enter fired `GET /properties/page?skip=0&take=20&search=…&sort=name` and the list filtered. Nothing on screen tells the landlord a keypress is required, and the stale full list sitting under a filled-in search box reads as "search is broken".
**Evidence:** `m01-rentals-search.png`, `m01-rentals-search2.png` (unfiltered, text present); API log empty for that window, then one `/properties/page?...&search=` request after enter.
**Code Reference:** `mobile/lib/core/widgets/mobile_grid_controls.dart:143` (`onSubmitted: onSearch`) — the shared search field wires only `onSubmitted`, never `onChanged`; `mobile/lib/features/properties/properties_list_screen.dart:73` (`_submitSearch`) is therefore only ever reached on submit.
**Suggested Fix:** Add a debounced `onChanged` (about 300 ms) to the search field in `mobile_grid_controls.dart` that calls the same `onSearch` callback; keep `onSubmitted` for the keyboard key.
**Why This Matters:** A landlord with 40 units searches constantly. A box that looks dead is worse than no box.

### BUG-8: Today's AI summary appears or vanishes at random, and the whole Today list waits ~6 s for it
**Severity:** Low
**Location:** Today screen (the summary card that should sit above the Today list) / `GET /api/v1/ai/briefing`
**Expected:** The same portfolio on the same day gives the same briefing; the factual bullet list should not wait on an optional LLM flourish.
**Actual:** The phone never showed the summary card across a cold start and two pull-to-refreshes, though the endpoint does return one. Two back-to-back curls of the same endpoint returned `llmEnhanced: false, summary: null` and then `llmEnhanced: true, summary: <366 chars>`. The cause is a hard 5 s polish timeout against calls that are taking 5.0–6.0 s: the phone's briefing requests logged 5903 ms, 5479 ms and 6006 ms, straddling the cutoff. Every Today load — including every pull-to-refresh — pays that full wait before any bullet renders.
**Evidence:** `m01-launch.png`, `m01-today-ptr.png` (no summary card, bullets start immediately under the "Today" heading); `/tmp/rentalcommand-api.log` lines 146/332 (`GET /api/v1/ai/briefing - 200 ... 5903.0939ms`); `adb logcat -s flutter` `[HTTP 6006ms] GET /ai/briefing -> 200`; two curls giving different `llmEnhanced` values minutes apart.
**Code Reference:** `RentalCommand.Api/Services/Domain/DailyBriefingService.cs:20` (`DefaultLlmPolishTimeout = TimeSpan.FromSeconds(5)`) and `:151-182` (`TryPolishSummaryAsync`, which swallows the timeout and returns `(null, false)`).
**Suggested Fix:** Cache the day's polished summary per portfolio and date after the first successful generation, so a slow call costs one request rather than every load and the summary stops flickering in and out. (Raising the timeout alone just makes the Today screen slower.)
**Why This Matters:** The headline of the flagship "the computer does the typing for you" feature is missing on the phone about half the time, and the landlord stares at an empty Today list for six seconds every time they pull to refresh.

## Potential Issues (need investigation)

- **`GET /auth/contexts -> 401` mid-session with no visible retry.** At 04:52:17 — the moment I opened the profile menu — the Flutter HTTP log records a 401 on `/auth/contexts`, with no refresh-and-retry line after it. The sheet still rendered, and the session stayed valid afterwards, so nothing broke here; but the client is supposed to refresh and retry once on 401 and I could not see that happen. Worth checking whether `/auth/contexts` goes through the interceptor.
- **Bottom-tab taps occasionally swallowed.** Twice, a tab tap issued about three seconds after the previous tap did nothing (Money → Rentals, and Inbox → Today), and the identical tap worked when issued on its own. This may be adb input timing rather than the app, but if a tab tap is genuinely ignored while the previous screen is still resolving, a landlord will read it as a stuck app. Reproduce with two quick taps by hand before dismissing it.
- **The Today screen does not refresh a stale briefing when you return to the tab.** After another tester's payment cleared Darius Clark's arrears (past-due went 5/$5,175 → 4/$4,100), Today still listed him after switching tabs away and back. Pull-to-refresh does refetch (verified in the API log), so this is only about tab re-entry, and the provider is `autoDispose`, so it may simply be that the tab is kept alive.

## Observations

- **The scan FAB overlaps content on every screen.** It sits over the second Today item's text, over Unit 2's "Occupied" badge in the property's unit list, over the "Kept $15,046" figure on the money card, and over the "90 days" lease-expiry tile. Nothing is unreachable — you can scroll the content out from under it — but a landlord skimming will read a covered number wrong. Screenshots: `m01-launch.png`, `m01-property-units.png`, `m01-today-scroll1.png`, `m01-tabstate-return.png`.
- **"Good morning, Rental!"** The greeting takes the first word of the display name, and the seeded admin is "Rental Command Admin". The heuristic is reasonable; the seed data makes it look broken. Worth a nicer seeded name for demos. `mobile/lib/features/home/home_shell.dart:2200-2206`.
- **Tapping a specific tenant's overdue row lands on the list of all five.** Deliberate per the comment at `home_shell.dart:3195-3200` (the list is where the actions live), and the actions are genuinely good — Record receipt / Open ledger / Text per tenant. But the landlord tapped *Marcus* and got a list; scrolling to the right person would be one less step if the row they tapped were highlighted or pinned to the top.
- **The go-live guard is excellent.** The "Example data" banner opens a sheet that explains in plain words that this permanently deletes the example data, requires typing GO LIVE, and keeps the action disabled until you do (`m01-banner.png`). Cancel returned cleanly to Today with nothing changed. No notes.
- **Property detail repeats itself.** Units / Occupied / Owner appear in the header card and again in the Summary card immediately below (`m01-property-eastland.png`).
- **Unit detail shows occupancy twice** — "Occupancy / possession: Occupied" and "Status: Occupied" in adjacent panels (`m01-unit1.png`).
- **Empty states are good** but the Messages one says "Open the action button and choose New conversation" — "the action button" is not a thing the landlord can see a name for (`m01-inbox.png`).
- **Addresses truncate mid-city** in the property list ("55 High Pines Ct, Columbus, O…"), while the wider rows show the full address. Cosmetic.
- **Background and resume is clean.** Home key, several seconds away, relaunch: straight back to Today, still signed in, no re-login (`m01-resume.png`).
- **No Dart errors.** `adb logcat -s flutter:V` shows only HTTP lines and a healthy SignalR connect for the whole session. `GET /dev/clock -> 404` on cold start is expected and documented at `mobile/lib/core/time/app_clock.dart:8-9`.

## What Was Tested
1. Launched the app (already signed in as admin@rentalcommand.local); read the Today screen top to bottom, scrolling to the money card.
2. Compared the Today list against `GET /api/v1/ai/briefing` — found 6 bullets served, 5 rendered, and the summary card missing; curled the endpoint repeatedly to catch the `llmEnhanced` flapping; correlated with request durations in `/tmp/rentalcommand-api.log` and `adb logcat -s flutter`.
3. Pull-to-refresh on Today (verified the refetch in the API log).
4. Tapped "Rent overdue — Marcus Williams"; verified the destination ("Who's behind", 5 tenants / $5,175.00 at that moment — which reconciled with the briefing) and then pressed back twice, landing outside the app.
5. Reproduced the back-exits-the-app path minimally: relaunch, tap Rentals, one back, confirmed with `dumpsys window`.
6. Walked Rentals ▸ Properties ▸ Eastland 8-Plex ▸ Rentals tab ▸ Unit 1, and checked each figure against `GET /api/v1/properties/page`, `GET /api/v1/units/page?propertyId=6` and `GET /api/v1/units/10/dashboard` (8 units, 7 occupied, Unit 1 $975 Occupied, 2 bed 1 bath, "ends in 66 days" — all matched).
7. Visited all five tabs (Today, Rentals, Money, Work, Inbox); checked each loads and looked at every status chip.
8. Cross-checked Money against `accounting/snapshot`, `accounting/summary`, `accounting/money-position` and `accounting/past-due` back to back.
9. Notification bell (opens Inbox ▸ Notifications, "You're all caught up") and profile icon (Getting started / Team / Settings / Sign out).
10. Opened the "Example data" banner, read the go-live sheet, and cancelled without confirming anything.
11. Typed "east" into the Rentals search, waited ~9 s, then pressed enter — watching the API log for the request.
12. Backgrounded the app with the home key and relaunched to confirm the session survived.
13. Read `flutter` logcat for the whole session.

Device cleanup: app left on Today, logged in
